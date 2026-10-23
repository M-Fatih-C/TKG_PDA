using System.Net;
using System.Net.Http.Json;
using TkgPda.Api;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Api.Tests;

// Customer UI — Warehouse Preparation Console: GET /api/v1/warehouse/preparation-requests
// lists only WAITING_PREPARATION transport requests, scoped to the machines
// the authenticated warehouse/supervisor user can operate, with no RDS or
// admin-only data leaked.
public sealed class WarehousePreparationApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Warehouse_operator_sees_waiting_preparation_requests()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1)).Single();
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        var creator = await fixture.SeedIdentityAsync();
        using var creatorClient = await fixture.LoginClientAsync(creator);
        var created = await CreateWaitingRequestAsync(creatorClient, context, slot, "warehouse-sees-waiting");

        var warehouseOperator = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator);
        using var warehouseClient = await fixture.LoginClientAsync(warehouseOperator);
        var response = await warehouseClient.GetAsync("/api/v1/warehouse/preparation-requests");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var requests = await ReadAsync(response);

        var match = Assert.Single(requests, r => r.RequestId == created.Id);
        Assert.Equal("BRING_FULL", match.Operation);
        Assert.Equal(context.MachineId, match.MachineId);
        Assert.Equal("SH-01-A", match.MachineCode);
        Assert.Equal(slot, match.DestinationSlotId);
        Assert.Null(match.SourceSlotId);
    }

    [Fact]
    public async Task Supervisor_sees_waiting_preparation_requests()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1)).Single();
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        var creator = await fixture.SeedIdentityAsync();
        using var creatorClient = await fixture.LoginClientAsync(creator);
        var created = await CreateWaitingRequestAsync(creatorClient, context, slot, "supervisor-sees-waiting");

        var supervisor = await fixture.SeedIdentityAsync(role: RoleCodes.Supervisor);
        using var supervisorClient = await fixture.LoginClientAsync(supervisor);
        var response = await supervisorClient.GetAsync("/api/v1/warehouse/preparation-requests");
        response.EnsureSuccessStatusCode();
        var requests = await ReadAsync(response);

        Assert.Contains(requests, r => r.RequestId == created.Id);
    }

    [Fact]
    public async Task Line_operator_is_forbidden_from_listing_preparation_requests()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator);
        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.GetAsync("/api/v1/warehouse/preparation-requests");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("ROLE_NOT_AUTHORIZED", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Ready_and_not_required_requests_are_excluded()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 3);
        foreach (var slot in slots)
        {
            await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        }
        var creator = await fixture.SeedIdentityAsync();
        using var creatorClient = await fixture.LoginClientAsync(creator);
        var waiting = await CreateWaitingRequestAsync(creatorClient, context, slots[0], "excl-waiting");
        var toBeReady = await CreateWaitingRequestAsync(creatorClient, context, slots[1], "excl-ready");
        var notRequiredResponse = await fixture.SendTransportAsync(
            creatorClient,
            Bring(context, await fixture.PartForSlotAsync(slots[2], "P05-WAREHO1")),
            "excl-not-required");
        var notRequired = await notRequiredResponse.Content.ReadFromJsonAsync<TransportHttpResponse>();

        var warehouseOperator = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator);
        using var warehouseClient = await fixture.LoginClientAsync(warehouseOperator);
        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET readiness_state = 'READY' WHERE id = @id",
            ("id", toBeReady.Id));

        var response = await warehouseClient.GetAsync("/api/v1/warehouse/preparation-requests");
        response.EnsureSuccessStatusCode();
        var requests = await ReadAsync(response);

        Assert.Contains(requests, r => r.RequestId == waiting.Id);
        Assert.DoesNotContain(requests, r => r.RequestId == toBeReady.Id);
        Assert.DoesNotContain(requests, r => r.RequestId == notRequired!.Id);
    }

    [Fact]
    public async Task Cancelled_waiting_request_is_excluded()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slot = Assert.Single(await fixture.LogisticsSlotsAsync(context.LayoutId, 1));
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        var creator = await fixture.SeedIdentityAsync();
        using var creatorClient = await fixture.LoginClientAsync(creator);
        var created = await CreateWaitingRequestAsync(creatorClient, context, slot, "cancelled-hidden");
        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET status = 'CANCELLED', cancelled_at = now() WHERE id = @id",
            ("id", created.Id));

        var warehouseOperator = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator);
        using var warehouseClient = await fixture.LoginClientAsync(warehouseOperator);
        var response = await warehouseClient.GetAsync("/api/v1/warehouse/preparation-requests");
        response.EnsureSuccessStatusCode();

        Assert.DoesNotContain(await ReadAsync(response), request => request.RequestId == created.Id);
    }

    [Fact]
    public async Task Results_are_ordered_deterministically_oldest_first()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slots = await fixture.LogisticsSlotsAsync(context.LayoutId, 2);
        foreach (var slot in slots)
        {
            await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        }
        var creator = await fixture.SeedIdentityAsync();
        using var creatorClient = await fixture.LoginClientAsync(creator);
        var second = await CreateWaitingRequestAsync(creatorClient, context, slots[0], "order-second");
        var first = await CreateWaitingRequestAsync(creatorClient, context, slots[1], "order-first");
        // Force a deterministic created_at ordering independent of wall-clock
        // timing between the two HTTP calls above.
        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET created_at = now() - interval '1 minute' WHERE id = @id",
            ("id", first.Id));
        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET created_at = now() WHERE id = @id",
            ("id", second.Id));

        var warehouseOperator = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator);
        using var warehouseClient = await fixture.LoginClientAsync(warehouseOperator);
        var response = await warehouseClient.GetAsync("/api/v1/warehouse/preparation-requests");
        response.EnsureSuccessStatusCode();
        var requests = await ReadAsync(response);
        var relevantIds = requests
            .Select(r => r.RequestId)
            .Where(id => id == first.Id || id == second.Id)
            .ToArray();

        Assert.Equal([first.Id, second.Id], relevantIds);
    }

    [Fact]
    public async Task A_machine_without_permission_is_not_leaked()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var otherContext = await fixture.ConfigureMachineAsync(machineCode: "SH-01-B");
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1)).Single();
        var otherSlot = (await fixture.LogisticsSlotsAsync(otherContext.LayoutId, 1)).Single();
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        await fixture.SetSlotAsync(otherSlot, "VACANT", "UNKNOWN");
        using var creatorClientA = await fixture.LoginClientAsync(
            await fixture.SeedIdentityAsync(machineCode: "SH-01-A"));
        using var creatorClientB = await fixture.LoginClientAsync(
            await fixture.SeedIdentityAsync(machineCode: "SH-01-B"));
        var visible = await CreateWaitingRequestAsync(creatorClientA, context, slot, "leak-visible");
        var hidden = await CreateWaitingRequestAsync(creatorClientB, otherContext, otherSlot, "leak-hidden");

        // Warehouse-authorized, but only permitted on SH-01-A.
        var warehouseOperator = await fixture.SeedIdentityAsync(
            role: RoleCodes.WarehouseOperator,
            machineCode: "SH-01-A");
        using var warehouseClient = await fixture.LoginClientAsync(warehouseOperator);
        var response = await warehouseClient.GetAsync("/api/v1/warehouse/preparation-requests");
        response.EnsureSuccessStatusCode();
        var requests = await ReadAsync(response);

        Assert.Contains(requests, r => r.RequestId == visible.Id);
        Assert.DoesNotContain(requests, r => r.RequestId == hidden.Id);
    }

    private async Task<TransportHttpResponse> CreateWaitingRequestAsync(
        HttpClient client,
        LayoutContext context,
        Guid destination,
        string idempotencyKey)
    {
        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/transport-requests")
            {
                Content = JsonContent.Create(BringFull(context, await fixture.PartForSlotAsync(destination, "P05-WAREHO2"))),
                Headers = { { ApiHeaders.IdempotencyKey, idempotencyKey } },
            });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<TransportHttpResponse>()
            ?? throw new InvalidOperationException("Transport response was empty.");
    }

    private static CreateTransportHttpRequest Bring(
        LayoutContext context,
        Guid partId,
        bool requiresPreparation = false) => new(
        context.MachineId,
        context.VariantId,
        context.LayoutId,
        null,
        null,
        partId,
        null,
        "BRING_EMPTY",
        RequiresPreparation: requiresPreparation);

    private static CreateTransportHttpRequest BringFull(
        LayoutContext context,
        Guid partId) => new(
        context.MachineId,
        context.VariantId,
        context.LayoutId,
        null,
        null,
        partId,
        null,
        "BRING_FULL",
        RequiresPreparation: true);

    private static async Task<List<WarehousePreparationRequestRecord>> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<List<WarehousePreparationRequestRecord>>()
            ?? throw new InvalidOperationException("Warehouse preparation response was empty.");
}
