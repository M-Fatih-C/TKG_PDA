using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TkgPda.Api;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Api.Tests;

/// <summary>
/// WP-05: Global Task Visibility and Machine-Scoped Cancellation Authorization Tests.
/// Validates:
/// 1. Global queue remains fleet-wide / across all machines without filtering by device binding.
/// 2. Warehouse roles can read the global queue endpoint.
/// 3. Global queue DTO projection does not carry canCancel.
/// 4. Old global cancel route (/api/v1/transport-requests/{id}/cancel) is removed (404/405) and cannot mutate state.
/// 5. Machine-scoped cancel (/api/v1/machines/{machineId}/transport-requests/{id}/cancel) succeeds for bound operator on own machine.
/// 6. Cross-machine cancel is rejected with DEVICE_MACHINE_SCOPE_MISMATCH (403).
/// 7. Path machine and request machine mismatch is rejected with REQUEST_MACHINE_MISMATCH (403).
/// 8. Warehouse operators cannot cancel even with known request ID (403).
/// 9. Cancellation state-machine rules are preserved for execution-started or terminal requests.
/// </summary>
public sealed class GlobalTaskVisibilityAndCancellationScopeApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Section27_Global_queue_returns_all_machines_without_machine_filter()
    {
        await fixture.ResetAsync();

        var contextA = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var contextB = await fixture.ConfigureMachineAsync("SH-01-B", "1");

        var slotA = Assert.Single(await fixture.LogisticsSlotsAsync(contextA.LayoutId, 1));
        var slotB = Assert.Single(await fixture.LogisticsSlotsAsync(contextB.LayoutId, 1));

        await fixture.SetSlotAsync(slotA, "VACANT", "UNKNOWN");
        await fixture.SetSlotAsync(slotB, "VACANT", "UNKNOWN");

        var creatorA = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A");
        using var clientA = await fixture.LoginClientAsync(creatorA);

        var respA = await fixture.SendTransportAsync(
            clientA,
            new CreateTransportHttpRequest(contextA.MachineId, contextA.VariantId, contextA.LayoutId, null, null,
                await fixture.PartForSlotAsync(slotA, "P05-A1"), null, "BRING_EMPTY"),
            "bring-machine-a");
        Assert.Equal(HttpStatusCode.Created, respA.StatusCode);
        var reqA = await respA.Content.ReadFromJsonAsync<TransportHttpResponse>();

        var creatorB = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-B");
        using var clientB = await fixture.LoginClientAsync(creatorB);

        var respB = await fixture.SendTransportAsync(
            clientB,
            new CreateTransportHttpRequest(contextB.MachineId, contextB.VariantId, contextB.LayoutId, null, null,
                await fixture.PartForSlotAsync(slotB, "P05-B1"), null, "BRING_EMPTY"),
            "bring-machine-b");
        Assert.Equal(HttpStatusCode.Created, respB.StatusCode);
        var reqB = await respB.Content.ReadFromJsonAsync<TransportHttpResponse>();

        // Line operator bound to Machine A
        var lineOpA = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);
        using var lineClientA = await fixture.LoginClientAsync(lineOpA);

        // Call global queue
        var queueResp = await lineClientA.GetAsync("/api/v1/transport-requests/queue");
        Assert.Equal(HttpStatusCode.OK, queueResp.StatusCode);

        var queueDoc = await queueResp.Content.ReadFromJsonAsync<JsonElement>();
        var items = queueDoc.GetProperty("items").EnumerateArray().ToList();

        // Both Machine A and Machine B requests must be visible in global queue
        var requestIds = items.Select(i => i.GetProperty("requestId").GetGuid()).ToList();
        Assert.Contains(reqA!.Id, requestIds);
        Assert.Contains(reqB!.Id, requestIds);
    }

    [Fact]
    public async Task Section28_Warehouse_roles_can_read_global_queue()
    {
        await fixture.ResetAsync();

        var context = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(context.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");

        var supervisor = await fixture.SeedIdentityAsync(role: RoleCodes.Supervisor);
        using var supervisorClient = await fixture.LoginClientAsync(supervisor);

        var createResp = await fixture.SendTransportAsync(
            supervisorClient,
            new CreateTransportHttpRequest(context.MachineId, context.VariantId, context.LayoutId, null, null,
                await fixture.PartForSlotAsync(slot, "P05-WHREAD"), null, "BRING_EMPTY"),
            "bring-wh-read");
        var req = await createResp.Content.ReadFromJsonAsync<TransportHttpResponse>();

        // 1. SemiFinishedWarehouseOperator can read global queue
        var semiWh = await fixture.SeedIdentityAsync(role: RoleCodes.SemiFinishedWarehouseOperator);
        using var semiClient = await fixture.LoginClientAsync(semiWh);

        var semiResp = await semiClient.GetAsync("/api/v1/transport-requests/queue");
        Assert.Equal(HttpStatusCode.OK, semiResp.StatusCode);
        var semiDoc = await semiResp.Content.ReadFromJsonAsync<JsonElement>();
        var semiIds = semiDoc.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("requestId").GetGuid()).ToList();
        Assert.Contains(req!.Id, semiIds);

        // 2. FinishedGoodsWarehouseOperator can read global queue
        var finWh = await fixture.SeedIdentityAsync(role: RoleCodes.FinishedGoodsWarehouseOperator);
        using var finClient = await fixture.LoginClientAsync(finWh);

        var finResp = await finClient.GetAsync("/api/v1/transport-requests/queue");
        Assert.Equal(HttpStatusCode.OK, finResp.StatusCode);
        var finDoc = await finResp.Content.ReadFromJsonAsync<JsonElement>();
        var finIds = finDoc.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("requestId").GetGuid()).ToList();
        Assert.Contains(req.Id, finIds);
    }

    [Fact]
    public async Task Section29_Global_queue_projection_does_not_contain_canCancel()
    {
        await fixture.ResetAsync();

        var context = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(context.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");

        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.Supervisor);
        using var client = await fixture.LoginClientAsync(identity);

        await fixture.SendTransportAsync(
            client,
            new CreateTransportHttpRequest(context.MachineId, context.VariantId, context.LayoutId, null, null,
                await fixture.PartForSlotAsync(slot, "P05-NOCANCEL"), null, "BRING_EMPTY"),
            "bring-dto-check");

        var response = await client.GetAsync("/api/v1/transport-requests/queue");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = doc.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);

        foreach (var item in items)
        {
            Assert.False(item.TryGetProperty("canCancel", out _), "Global queue item should not contain canCancel property.");
        }
    }

    [Fact]
    public async Task Section30_Old_global_cancel_route_returns_not_found_and_does_not_mutate()
    {
        await fixture.ResetAsync();

        var context = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(context.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");

        var supervisor = await fixture.SeedIdentityAsync(role: RoleCodes.Supervisor);
        using var supervisorClient = await fixture.LoginClientAsync(supervisor);

        var createResp = await fixture.SendTransportAsync(
            supervisorClient,
            new CreateTransportHttpRequest(context.MachineId, context.VariantId, context.LayoutId, null, null,
                await fixture.PartForSlotAsync(slot, "P05-OLDROUTE"), null, "BRING_EMPTY"),
            "bring-old-route");
        var req = await createResp.Content.ReadFromJsonAsync<TransportHttpResponse>();

        // Call old route: POST /api/v1/transport-requests/{id}/cancel
        var oldCancelResp = await supervisorClient.PostAsync($"/api/v1/transport-requests/{req!.Id}/cancel", null);
        Assert.True(
            oldCancelResp.StatusCode == HttpStatusCode.NotFound || oldCancelResp.StatusCode == HttpStatusCode.MethodNotAllowed,
            $"Expected 404 or 405 but received {oldCancelResp.StatusCode}");

        // Verify status in DB remains WAITING_PREPARATION or QUEUED (not CANCELLED)
        var status = await fixture.ScalarAsync<string>(
            "SELECT status FROM ops.transport_requests WHERE id = @id",
            ("id", req.Id));
        Assert.NotEqual("CANCELLED", status);
    }

    [Fact]
    public async Task Section31_Machine_scoped_cancel_succeeds_for_bound_line_operator_on_own_machine()
    {
        await fixture.ResetAsync();

        var context = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(context.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");

        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);
        using var client = await fixture.LoginClientAsync(identity);

        var createResp = await fixture.SendTransportAsync(
            client,
            new CreateTransportHttpRequest(context.MachineId, context.VariantId, context.LayoutId, null, null,
                await fixture.PartForSlotAsync(slot, "P05-OWNCANCEL"), null, "BRING_EMPTY"),
            "bring-own-cancel");
        var req = await createResp.Content.ReadFromJsonAsync<TransportHttpResponse>();

        // New machine-scoped cancel endpoint: POST /api/v1/machines/{machineId}/transport-requests/{requestId}/cancel
        var cancelResp = await client.PostAsync(
            $"/api/v1/machines/{context.MachineId}/transport-requests/{req!.Id}/cancel",
            null);
        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);

        var body = await cancelResp.Content.ReadFromJsonAsync<TransportHttpResponse>();
        Assert.NotNull(body);
        Assert.Equal("CANCELLED", body.Status);

        var dbStatus = await fixture.ScalarAsync<string>(
            "SELECT status FROM ops.transport_requests WHERE id = @id",
            ("id", req.Id));
        Assert.Equal("CANCELLED", dbStatus);
    }

    [Fact]
    public async Task Section32_Cross_machine_cancel_is_forbidden_with_device_machine_scope_mismatch()
    {
        await fixture.ResetAsync();

        var contextA = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var contextB = await fixture.ConfigureMachineAsync("SH-01-B", "1");

        var slotB = Assert.Single(await fixture.LogisticsSlotsAsync(contextB.LayoutId, 1));
        await fixture.SetSlotAsync(slotB, "VACANT", "UNKNOWN");

        var creatorB = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-B");
        using var clientB = await fixture.LoginClientAsync(creatorB);

        var createResp = await fixture.SendTransportAsync(
            clientB,
            new CreateTransportHttpRequest(contextB.MachineId, contextB.VariantId, contextB.LayoutId, null, null,
                await fixture.PartForSlotAsync(slotB, "P05-CROSSCAN"), null, "BRING_EMPTY"),
            "bring-cross-req");
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var reqB = await createResp.Content.ReadFromJsonAsync<TransportHttpResponse>();

        // Terminal A bound to Machine A
        var lineOpA = await fixture.SeedIdentityAsync(
            role: RoleCodes.LineOperator,
            machineCode: "SH-01-A",
            bindDeviceToMachine: true);
        using var lineClientA = await fixture.LoginClientAsync(lineOpA);

        // Attempt to cancel Machine B's request via Machine B endpoint from Terminal A
        var cancelResp = await lineClientA.PostAsync(
            $"/api/v1/machines/{contextB.MachineId}/transport-requests/{reqB!.Id}/cancel",
            null);

        Assert.Equal(HttpStatusCode.Forbidden, cancelResp.StatusCode);
        Assert.Equal("DEVICE_MACHINE_SCOPE_MISMATCH", await ApiTestFixture.ErrorCodeAsync(cancelResp));

        // State remains unchanged
        var dbStatus = await fixture.ScalarAsync<string>(
            "SELECT status FROM ops.transport_requests WHERE id = @id",
            ("id", reqB.Id));
        Assert.NotEqual("CANCELLED", dbStatus);
    }

    [Fact]
    public async Task Section33_Path_and_request_machine_mismatch_is_rejected()
    {
        await fixture.ResetAsync();

        var contextA = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var contextB = await fixture.ConfigureMachineAsync("SH-01-B", "1");

        var slotA = Assert.Single(await fixture.LogisticsSlotsAsync(contextA.LayoutId, 1));
        await fixture.SetSlotAsync(slotA, "VACANT", "UNKNOWN");

        var supervisor = await fixture.SeedIdentityAsync(
            role: RoleCodes.Supervisor,
            machineCode: "SH-01-A",
            bindDeviceToMachine: false);
        await fixture.ExecuteAsync(
            "INSERT INTO auth.user_machine_permissions (user_id, machine_id, can_view, can_operate, can_change_variant, can_manage_slots) VALUES (@uid, @mid, true, true, true, false)",
            ("uid", supervisor.UserId), ("mid", contextB.MachineId));
        using var supervisorClient = await fixture.LoginClientAsync(supervisor);

        // Request A created on Machine A
        var createResp = await fixture.SendTransportAsync(
            supervisorClient,
            new CreateTransportHttpRequest(contextA.MachineId, contextA.VariantId, contextA.LayoutId, null, null,
                await fixture.PartForSlotAsync(slotA, "P05-PATHMISMATCH"), null, "BRING_EMPTY"),
            "bring-mismatch-req");
        var reqA = await createResp.Content.ReadFromJsonAsync<TransportHttpResponse>();

        // Even though supervisor has access to both machines, calling with Machine B in path for Request A fails
        var cancelResp = await supervisorClient.PostAsync(
            $"/api/v1/machines/{contextB.MachineId}/transport-requests/{reqA!.Id}/cancel",
            null);

        Assert.Equal(HttpStatusCode.Forbidden, cancelResp.StatusCode);
        Assert.Equal("REQUEST_MACHINE_MISMATCH", await ApiTestFixture.ErrorCodeAsync(cancelResp));

        // Request A remains unchanged
        var dbStatus = await fixture.ScalarAsync<string>(
            "SELECT status FROM ops.transport_requests WHERE id = @id",
            ("id", reqA.Id));
        Assert.NotEqual("CANCELLED", dbStatus);
    }

    [Fact]
    public async Task Section34_Warehouse_operator_cannot_cancel_even_with_known_request_id()
    {
        await fixture.ResetAsync();

        var context = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(context.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");

        var supervisor = await fixture.SeedIdentityAsync(role: RoleCodes.Supervisor);
        using var supervisorClient = await fixture.LoginClientAsync(supervisor);

        var createResp = await fixture.SendTransportAsync(
            supervisorClient,
            new CreateTransportHttpRequest(context.MachineId, context.VariantId, context.LayoutId, null, null,
                await fixture.PartForSlotAsync(slot, "P05-WHNOCAN"), null, "BRING_EMPTY"),
            "bring-wh-nocan");
        var req = await createResp.Content.ReadFromJsonAsync<TransportHttpResponse>();

        // Warehouse operator
        var warehouseOp = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator);
        using var warehouseClient = await fixture.LoginClientAsync(warehouseOp);

        // Queue read succeeds
        var queueResp = await warehouseClient.GetAsync("/api/v1/transport-requests/queue");
        Assert.Equal(HttpStatusCode.OK, queueResp.StatusCode);

        // Machine cancel fails with 403 Forbidden
        var cancelResp = await warehouseClient.PostAsync(
            $"/api/v1/machines/{context.MachineId}/transport-requests/{req!.Id}/cancel",
            null);
        Assert.Equal(HttpStatusCode.Forbidden, cancelResp.StatusCode);

        // Status remains unchanged
        var dbStatus = await fixture.ScalarAsync<string>(
            "SELECT status FROM ops.transport_requests WHERE id = @id",
            ("id", req.Id));
        Assert.NotEqual("CANCELLED", dbStatus);
    }

    [Fact]
    public async Task Section35_Terminal_state_request_cancellation_is_rejected()
    {
        await fixture.ResetAsync();

        var context = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(context.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");

        var supervisor = await fixture.SeedIdentityAsync(role: RoleCodes.Supervisor);
        using var supervisorClient = await fixture.LoginClientAsync(supervisor);

        var createResp = await fixture.SendTransportAsync(
            supervisorClient,
            new CreateTransportHttpRequest(context.MachineId, context.VariantId, context.LayoutId, null, null,
                await fixture.PartForSlotAsync(slot, "P05-TERMINALCAN"), null, "BRING_EMPTY"),
            "bring-terminal-can");
        var req = await createResp.Content.ReadFromJsonAsync<TransportHttpResponse>();

        // Set request to COMPLETED in database
        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET status = 'COMPLETED' WHERE id = @id",
            ("id", req!.Id));

        var cancelResp = await supervisorClient.PostAsync(
            $"/api/v1/machines/{context.MachineId}/transport-requests/{req.Id}/cancel",
            null);

        Assert.Equal(HttpStatusCode.Conflict, cancelResp.StatusCode);
        Assert.Equal("CANCELLATION_NOT_ALLOWED", await ApiTestFixture.ErrorCodeAsync(cancelResp));
    }
}
