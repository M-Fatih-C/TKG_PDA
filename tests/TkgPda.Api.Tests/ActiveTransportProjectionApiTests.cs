using System.Net;
using System.Net.Http.Json;
using TkgPda.Api;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Api.Tests;

/// <summary>
/// WP-07: the machine operational context's active-request projection carries
/// server-derived part, relation, readiness, queue-order and elapsed values. All
/// of them are projection-only; nothing here is an RDS or robot value.
/// </summary>
public sealed class ActiveTransportProjectionApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Part_code_and_description_are_projected_from_master_data()
    {
        await fixture.ResetAsync();
        var machine = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(machine.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        var partId = await fixture.PartForSlotAsync(slot, "P07-PART-INFO");
        await fixture.ExecuteAsync(
            "UPDATE master.parts SET description = 'Ön Sol Braket' WHERE id = @id", ("id", partId));
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);
        var requestId = await CreateBringAsync(client, machine, partId, "wp07-part-info");

        var request = Assert.Single((await GetContextAsync(client, machine.MachineId)).ActiveTransportRequests);

        Assert.Equal(requestId, request.RequestId);
        Assert.Equal("P07-PART-INFO", request.PartCode);
        Assert.Equal("Ön Sol Braket", request.PartDescription);
    }

    [Fact]
    public async Task Relation_type_is_derived_from_master_data_and_never_guessed()
    {
        await fixture.ResetAsync();
        var machine = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(machine.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        var partId = await fixture.PartForSlotAsync(slot, "P07-REL-REQUEST");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);
        var requestId = await CreateBringAsync(client, machine, partId, "wp07-relation");

        var mainPart = await InsertPartAsync("P07-REL-MAIN");
        var altPart = await InsertPartAsync("P07-REL-ALT");
        var strangerPart = await InsertPartAsync("P07-REL-STRANGER");
        var groupId = await fixture.ScalarAsync<Guid>(
            """
            INSERT INTO master.product_groups (variant_id, main_part_id, name, display_order, is_active)
            VALUES (@variant_id, @main_part_id, 'P07 group', 901, true)
            RETURNING id
            """,
            ("variant_id", machine.VariantId),
            ("main_part_id", mainPart));
        await fixture.ExecuteAsync(
            """
            INSERT INTO master.product_group_parts (product_group_id, part_id, relation_type)
            VALUES (@group_id, @part_id, 'ALT_PART')
            """,
            ("group_id", groupId),
            ("part_id", altPart));

        // ALT_PART: the request part is listed under the group as an alternative part.
        await PointRequestAtAsync(requestId, groupId, altPart);
        Assert.Equal("ALT_PART", await RelationAsync(client, machine));

        // MAIN_PART: the request part is the group's main part.
        await PointRequestAtAsync(requestId, groupId, mainPart);
        Assert.Equal("MAIN_PART", await RelationAsync(client, machine));

        // Another stored relation type is passed through as server evidence.
        await fixture.ExecuteAsync(
            "UPDATE master.product_group_parts SET relation_type = 'RELATED' WHERE product_group_id = @g AND part_id = @p",
            ("g", groupId), ("p", altPart));
        await PointRequestAtAsync(requestId, groupId, altPart);
        Assert.Equal("RELATED", await RelationAsync(client, machine));

        // No evidence: the part is neither the main part nor listed in the group.
        await PointRequestAtAsync(requestId, groupId, strangerPart);
        Assert.Null(await RelationAsync(client, machine));

        // No product group on the request: nothing to derive a relation from.
        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET product_group_id = NULL, part_id = @part WHERE id = @id",
            ("id", requestId), ("part", altPart));
        Assert.Null(await RelationAsync(client, machine));

        // No part at all must not fail the projection.
        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET product_group_id = NULL, part_id = NULL WHERE id = @id",
            ("id", requestId));
        var withoutPart = Assert.Single((await GetContextAsync(client, machine.MachineId)).ActiveTransportRequests);
        Assert.Null(withoutPart.PartCode);
        Assert.Null(withoutPart.PartDescription);
        Assert.Null(withoutPart.RelationType);
    }

    [Fact]
    public async Task Queue_position_is_machine_local_unique_and_deterministic()
    {
        await fixture.ResetAsync();
        var machineA = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var machineB = await fixture.ConfigureMachineAsync("SH-01-B", "1");
        var slotsA = await fixture.LogisticsSlotsAsync(machineA.LayoutId, 3);
        var slotB = Assert.Single(await fixture.LogisticsSlotsAsync(machineB.LayoutId, 1));
        foreach (var slot in slotsA.Append(slotB))
        {
            await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        }

        var identityA = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        var identityB = await fixture.SeedIdentityAsync(machineCode: "SH-01-B");
        using var clientA = await fixture.LoginClientAsync(identityA);
        using var clientB = await fixture.LoginClientAsync(identityB);
        var first = await CreateBringAsync(clientA, machineA, await fixture.PartForSlotAsync(slotsA[0], "P07-Q-1"), "wp07-q-1");
        var second = await CreateBringAsync(clientA, machineA, await fixture.PartForSlotAsync(slotsA[1], "P07-Q-2"), "wp07-q-2");
        var third = await CreateBringAsync(clientA, machineA, await fixture.PartForSlotAsync(slotsA[2], "P07-Q-3"), "wp07-q-3");
        var other = await CreateBringAsync(clientB, machineB, await fixture.PartForSlotAsync(slotB, "P07-Q-B"), "wp07-q-b");

        // Pin the ordering inputs: created_at order first=oldest, third=newest; the
        // newest request is the one already running, which outranks queued ones.
        await fixture.ExecuteAsync(
            """
            UPDATE ops.transport_requests SET created_at = now() - interval '30 minutes' WHERE id = @first;
            UPDATE ops.transport_requests SET created_at = now() - interval '20 minutes' WHERE id = @second;
            UPDATE ops.transport_requests SET created_at = now() - interval '10 minutes', status = 'RUNNING' WHERE id = @third;
            """,
            ("first", first), ("second", second), ("third", third));

        var contextA = await GetContextAsync(clientA, machineA.MachineId);
        var positions = contextA.ActiveTransportRequests.ToDictionary(item => item.RequestId, item => item.QueuePosition);
        Assert.Equal(3, positions.Count);
        Assert.All(positions.Values, position => Assert.NotNull(position));
        Assert.Equal(3, positions.Values.Distinct().Count());
        Assert.Equal(1, positions[third]);
        Assert.Equal(2, positions[first]);
        Assert.Equal(3, positions[second]);
        Assert.DoesNotContain(contextA.ActiveTransportRequests, item => item.RequestId == other);

        // The other machine ranks only its own request and is unaffected by machine A.
        var contextB = await GetContextAsync(clientB, machineB.MachineId);
        var onlyB = Assert.Single(contextB.ActiveTransportRequests);
        Assert.Equal(other, onlyB.RequestId);
        Assert.Equal(1, onlyB.QueuePosition);

        // Repeat reads are stable.
        var again = (await GetContextAsync(clientA, machineA.MachineId)).ActiveTransportRequests
            .ToDictionary(item => item.RequestId, item => item.QueuePosition);
        Assert.Equal(positions, again);
    }

    [Fact]
    public async Task Elapsed_values_come_from_the_server_clock_and_execution_elapsed_needs_start_evidence()
    {
        await fixture.ResetAsync();
        var machine = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(machine.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);
        var requestId = await CreateBringAsync(
            client, machine, await fixture.PartForSlotAsync(slot, "P07-ELAPSED"), "wp07-elapsed");

        // A request created "now" is never negative, even with a future timestamp.
        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET created_at = now() + interval '1 hour' WHERE id = @id",
            ("id", requestId));
        var future = Assert.Single((await GetContextAsync(client, machine.MachineId)).ActiveTransportRequests);
        Assert.Equal(0, future.AgeSeconds);
        Assert.Null(future.ExecutionElapsedSeconds);

        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET created_at = now() - interval '90 seconds' WHERE id = @id",
            ("id", requestId));
        var aged = Assert.Single((await GetContextAsync(client, machine.MachineId)).ActiveTransportRequests);
        Assert.InRange(aged.AgeSeconds, 90, 150);
        Assert.Null(aged.ExecutionElapsedSeconds);

        // An RDS task row without a start timestamp is not start evidence.
        await fixture.ExecuteAsync(
            """
            INSERT INTO integration.rds_tasks (
                transport_request_id, rds_task_record_id, rds_task_label, out_order_no,
                agv_id, rds_status, sent_at, last_sync_at)
            VALUES (@id, 'RDS-P07-ELAPSED', 'P07-LABEL', 'OUT-P07', 'AGV-P07', 1000, now(), now())
            """,
            ("id", requestId));
        var sent = Assert.Single((await GetContextAsync(client, machine.MachineId)).ActiveTransportRequests);
        Assert.Null(sent.ExecutionElapsedSeconds);

        await fixture.ExecuteAsync(
            "UPDATE integration.rds_tasks SET started_at = now() - interval '30 seconds' WHERE transport_request_id = @id",
            ("id", requestId));
        var started = Assert.Single((await GetContextAsync(client, machine.MachineId)).ActiveTransportRequests);
        Assert.NotNull(started.ExecutionElapsedSeconds);
        Assert.InRange(started.ExecutionElapsedSeconds!.Value, 30, 90);
        Assert.True(started.AgeSeconds >= started.ExecutionElapsedSeconds);
    }

    [Fact]
    public async Task Readiness_state_is_projected_separately_from_status()
    {
        await fixture.ResetAsync();
        var machine = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(machine.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);
        var requestId = await CreateBringAsync(
            client, machine, await fixture.PartForSlotAsync(slot, "P07-READY"), "wp07-ready");

        var plain = Assert.Single((await GetContextAsync(client, machine.MachineId)).ActiveTransportRequests);
        Assert.Equal("NOT_REQUIRED", plain.ReadinessState);

        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET readiness_state = 'WAITING_PREPARATION' WHERE id = @id",
            ("id", requestId));
        var waiting = Assert.Single((await GetContextAsync(client, machine.MachineId)).ActiveTransportRequests);
        Assert.Equal("WAITING_PREPARATION", waiting.ReadinessState);
        Assert.Equal("QUEUED", waiting.Status);
    }

    [Fact]
    public async Task Line_operator_keeps_cancel_capability_while_warehouse_roles_have_no_machine_context_or_cancel()
    {
        await fixture.ResetAsync();
        var machine = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(machine.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");

        var lineOperator = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A");
        using var lineClient = await fixture.LoginClientAsync(lineOperator);
        var requestId = await CreateBringAsync(
            lineClient, machine, await fixture.PartForSlotAsync(slot, "P07-LINE-OWN"), "wp07-line-own");

        var lineContext = await GetContextAsync(lineClient, machine.MachineId);
        var projected = Assert.Single(lineContext.ActiveTransportRequests);
        Assert.True(projected.CanCancel);
        Assert.Null(projected.CancelDisabledCode);

        // Even the legacy owner of a request gets neither the machine projection nor
        // the cancel endpoint. (The canCancel=false projection for every warehouse role,
        // including own delivery, is covered by PdaOperationalContextPolicyTests.)
        var legacyWarehouse = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator, machineCode: "SH-01-A");
        using var legacyClient = await fixture.LoginClientAsync(legacyWarehouse);
        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET requested_by = @user WHERE id = @id",
            ("user", legacyWarehouse.UserId), ("id", requestId));
        var contextResponse = await legacyClient.GetAsync($"/api/v1/machines/{machine.MachineId}/operational-context");
        Assert.Equal(HttpStatusCode.Forbidden, contextResponse.StatusCode);
        var cancelResponse = await legacyClient.PostAsync(
            $"/api/v1/machines/{machine.MachineId}/transport-requests/{requestId}/cancel", null);
        Assert.Equal(HttpStatusCode.Forbidden, cancelResponse.StatusCode);
        Assert.NotEqual("CANCELLED", await fixture.ScalarAsync<string>(
            "SELECT status FROM ops.transport_requests WHERE id = @id", ("id", requestId)));
    }
    [Fact]
    public async Task Pda_projection_exposes_no_raw_rds_identifiers_or_progress_values()
    {
        await fixture.ResetAsync();
        var machine = await fixture.ConfigureMachineAsync("SH-01-A", "1");
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(machine.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        var identity = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(identity);
        var requestId = await CreateBringAsync(
            client, machine, await fixture.PartForSlotAsync(slot, "P07-NORAW"), "wp07-noraw");
        await fixture.ExecuteAsync(
            """
            INSERT INTO integration.rds_tasks (
                transport_request_id, rds_task_record_id, rds_task_label, out_order_no,
                agv_id, rds_status, sent_at, started_at, last_sync_at)
            VALUES (@id, 'RDS-P07-SECRET-TASK', 'P07-SECRET-LABEL', 'OUT-P07-SECRET', 'AGV-P07-SECRET', 1000,
                    now(), now(), now())
            """,
            ("id", requestId));

        var response = await client.GetAsync($"/api/v1/machines/{machine.MachineId}/operational-context");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("P07-SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("progress", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("percent", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rdsQueue", json, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Guid> CreateBringAsync(HttpClient client, LayoutContext machine, Guid partId, string key)
    {
        var response = await fixture.SendTransportAsync(
            client,
            new CreateTransportHttpRequest(
                machine.MachineId, machine.VariantId, machine.LayoutId, null, null, partId, null, "BRING_EMPTY"),
            key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TransportHttpResponse>();
        Assert.NotNull(created);
        return created.Id;
    }

    private Task<Guid> InsertPartAsync(string code) => fixture.ScalarAsync<Guid>(
        "INSERT INTO master.parts (part_code, description, is_active) VALUES (@code, 'WP-07 part', true) RETURNING id",
        ("code", code));

    private Task PointRequestAtAsync(Guid requestId, Guid groupId, Guid partId) => fixture.ExecuteAsync(
        "UPDATE ops.transport_requests SET product_group_id = @group, part_id = @part WHERE id = @id",
        ("id", requestId), ("group", groupId), ("part", partId));

    private async Task<string?> RelationAsync(HttpClient client, LayoutContext machine) =>
        Assert.Single((await GetContextAsync(client, machine.MachineId)).ActiveTransportRequests).RelationType;

    private static async Task<OperationalContextRecord> GetContextAsync(HttpClient client, Guid machineId)
    {
        var response = await client.GetAsync($"/api/v1/machines/{machineId}/operational-context");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<OperationalContextRecord>()
            ?? throw new InvalidOperationException("Operational context response was empty.");
    }
}
