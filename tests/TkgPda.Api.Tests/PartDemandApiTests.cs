using System.Net;
using System.Net.Http.Json;
using TkgPda.Api;
using TkgPda.Application;
using TkgPda.Domain;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class PartDemandApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Vacant_alt_part_creates_bring_full_waiting_for_preparation()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var created = await DemandAsync(client, setup, setup.AltPart, "vacant-once");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<PartDemandHttpResponse>();
        Assert.NotNull(body);
        Assert.Equal("BRING_FULL", body.TaskType);
        Assert.Equal("QUEUED", body.Status);
        Assert.Equal("WAITING_PREPARATION", body.Readiness);
        Assert.Null(body.PredecessorRequestNo);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Same_idempotency_key_returns_the_same_request()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var first = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "same-key"));
        var secondResponse = await DemandAsync(client, setup, setup.AltPart, "same-key");

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = await ReadDemandAsync(secondResponse);
        Assert.Equal(first.RequestNo, second.RequestNo);
        Assert.True(second.IdempotentReplay);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task A_new_key_does_not_create_a_second_request_while_one_is_active()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);
        await DemandAsync(client, setup, setup.AltPart, "active-first");

        var duplicate = await DemandAsync(client, setup, setup.AltPart, "active-second");

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("PART_REQUEST_ALREADY_ACTIVE", await ApiTestFixture.ErrorCodeAsync(duplicate));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Occupied_full_area_creates_remove_full_then_dependent_bring_full()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "FULL");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var body = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "again-full"));

        Assert.Equal("BRING_FULL", body.TaskType);
        Assert.Equal("REMOVE_FULL", body.PredecessorTaskType);
        Assert.Equal("WAITING_PREPARATION", body.Readiness);
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_request_dependencies"));
        Assert.False(body.PredecessorRequestNo is null);
    }

    [Fact]
    public async Task Occupied_empty_area_creates_remove_empty_then_dependent_bring_full()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "EMPTY");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var body = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "again-empty"));

        Assert.Equal("REMOVE_EMPTY", body.PredecessorTaskType);
        Assert.Equal("BRING_FULL", body.TaskType);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_request_dependencies"));
    }

    [Fact]
    public async Task Dependent_bring_full_waits_for_predecessor_completion_and_warehouse_readiness()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "FULL");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);
        var body = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "chain-gate"));
        var successorId = await RequestIdAsync(body.RequestNo);
        var predecessorId = await RequestIdAsync(body.PredecessorRequestNo!.Value);

        await MarkPreparedAsync(setup, successorId);
        Assert.Equal(0L, await EligibleCountAsync(successorId));

        await CompleteAsync(predecessorId);
        Assert.Equal("READY", await fixture.ScalarAsync<string>("SELECT readiness_state FROM ops.transport_requests WHERE id = @id", ("id", successorId)));
        Assert.Equal(1L, await EligibleCountAsync(successorId));
    }

    [Fact]
    public async Task Completed_predecessor_without_preparation_is_still_not_dispatchable()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "FULL");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);
        var body = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "not-ready"));
        var successorId = await RequestIdAsync(body.RequestNo);

        await CompleteAsync(await RequestIdAsync(body.PredecessorRequestNo!.Value));

        Assert.Equal("WAITING_PREPARATION", await fixture.ScalarAsync<string>(
            "SELECT readiness_state FROM ops.transport_requests WHERE id = @id", ("id", successorId)));
        Assert.Equal(0L, await EligibleCountAsync(successorId));
    }

    [Fact]
    public async Task Unknown_area_does_not_guess_a_task()
    {
        var setup = await SetupAsync(occupancy: "UNKNOWN", load: "UNKNOWN");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var response = await DemandAsync(client, setup, setup.AltPart, "unknown-area");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("PART_AREA_STATE_UNKNOWN", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Batch_creates_one_request_per_alt_part_and_rolls_back_when_one_part_is_not_alt()
    {
        var setup = await SetupAsync();
        var secondSlot = await NextSlotAsync(setup.Context.LayoutId, setup.Slot);
        var second = await fixture.ScalarAsync<Guid>("INSERT INTO master.parts (part_code, description) VALUES ('P05-ALT-B', 'Alt B') RETURNING id");
        await fixture.ExecuteAsync(
            "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot, @part)",
            ("slot", secondSlot), ("part", second));
        await fixture.SetSlotAsync(secondSlot, "VACANT", "UNKNOWN");
        await fixture.ExecuteAsync(
            """
            INSERT INTO master.product_group_parts (product_group_id, part_id, relation_type, is_active)
            VALUES (@group, @part, 'ALT_PART', true)
            """,
            ("group", setup.Group), ("part", second));
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var created = await BatchAsync(client, setup, [setup.AltPart, second], "batch-ok");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<PartDemandBatchHttpResponse>();
        Assert.NotNull(body);
        Assert.Equal(2, body.Items.Count);
        Assert.All(body.Items, item => Assert.Equal("BRING_FULL", item.TaskType));
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));

        var rejected = await BatchAsync(client, setup, [setup.MainPart, second], "batch-bad");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal("PART_NOT_ALT_PART", await ApiTestFixture.ErrorCodeAsync(rejected));
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task A_part_outside_the_active_group_is_rejected()
    {
        var setup = await SetupAsync();
        var stranger = await fixture.ScalarAsync<Guid>("INSERT INTO master.parts (part_code) VALUES ('P05-STRANGER') RETURNING id");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var response = await DemandAsync(client, setup, stranger, "stranger");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("PART_NOT_ALT_PART", await ApiTestFixture.ErrorCodeAsync(response));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    private async Task<Setup> SetupAsync(string occupancy = "VACANT", string load = "UNKNOWN")
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var slot = await NextSlotAsync(context.LayoutId, null);
        var main = await fixture.ScalarAsync<Guid>("INSERT INTO master.parts (part_code, description) VALUES ('P05-MAMUL', 'Mamul') RETURNING id");
        var alt = await fixture.ScalarAsync<Guid>("INSERT INTO master.parts (part_code, description) VALUES ('P05-ALT-A', 'Alt A') RETURNING id");
        var group = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.product_groups (variant_id, main_part_id, name) VALUES (@variant, @part, 'Demo group') RETURNING id",
            ("variant", context.VariantId), ("part", main));
        var containerTypeId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
        await fixture.ExecuteAsync(
            """
            INSERT INTO master.product_group_parts (product_group_id, part_id, container_type_id, relation_type, is_active)
            VALUES (@group, @part, @container, 'ALT_PART', true)
            """,
            ("group", group), ("part", alt), ("container", containerTypeId));
        await fixture.ExecuteAsync(
            "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot, @part)",
            ("slot", slot), ("part", alt));
        await fixture.SetSlotAsync(slot, occupancy, load);
        var oper = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A");
        return new Setup(context, oper, group, main, alt, slot);
    }

    private Task<Guid> NextSlotAsync(Guid layoutId, Guid? except) => except is null
        ? fixture.ScalarAsync<Guid>(
            """
            SELECT id FROM master.layout_slots
            WHERE layout_id = @layout AND is_enabled AND slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
            ORDER BY display_order, code
            LIMIT 1
            """,
            ("layout", layoutId))
        : fixture.ScalarAsync<Guid>(
            """
            SELECT id FROM master.layout_slots
            WHERE layout_id = @layout AND is_enabled AND slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
              AND id <> @except
            ORDER BY display_order, code
            LIMIT 1
            """,
            ("layout", layoutId),
            ("except", except.Value));

    private static async Task StartProductionAsync(HttpClient client, Setup setup)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.Context.MachineId}/production-sessions")
        {
            Content = JsonContent.Create(new StartProductionSessionHttpRequest(setup.Context.VariantId, setup.Group, setup.MainPart))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, "prod-" + Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static Task<HttpResponseMessage> DemandAsync(HttpClient client, Setup setup, Guid partId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.Context.MachineId}/part-demands")
        {
            Content = JsonContent.Create(new PartDemandHttpRequest(partId))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> BatchAsync(HttpClient client, Setup setup, Guid[] parts, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{setup.Context.MachineId}/part-demands/batch")
        {
            Content = JsonContent.Create(new PartDemandBatchHttpRequest(parts))
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }

    private static async Task<PartDemandHttpResponse> ReadDemandAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return System.Text.Json.JsonSerializer.Deserialize<PartDemandHttpResponse>(
            body,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private Task<Guid> RequestIdAsync(long requestNo) => fixture.ScalarAsync<Guid>(
        "SELECT id FROM ops.transport_requests WHERE request_no = @no", ("no", requestNo));

    private Task<long> EligibleCountAsync(Guid requestId) => fixture.ScalarAsync<long>(
        """
        SELECT count(*)
        FROM ops.transport_requests
        WHERE id = @id
          AND status = 'QUEUED'
          AND readiness_state <> 'WAITING_PREPARATION'
          AND NOT EXISTS (
              SELECT 1
              FROM ops.transport_request_dependencies d
              JOIN ops.transport_requests pred ON pred.id = d.predecessor_request_id
              WHERE d.successor_request_id = ops.transport_requests.id
                AND pred.status <> 'COMPLETED')
        """,
        ("id", requestId));

    private async Task MarkPreparedAsync(Setup setup, Guid requestId)
    {
        var warehouse = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator, machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(warehouse);
        var areaId = await fixture.ScalarAsync<Guid>("SELECT id FROM ops.preparation_areas WHERE is_enabled ORDER BY area_number LIMIT 1");
        var containerTypeId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
        var place = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/warehouse/preparation/areas/{areaId}/pallet")
        {
            Content = JsonContent.Create(new PlacePalletHttpRequest("FULL", containerTypeId, setup.AltPart, requestId))
        };
        place.Headers.Add(ApiHeaders.IdempotencyKey, "place-" + requestId.ToString("N"));
        var placed = await client.SendAsync(place);
        var placedBody = await placed.Content.ReadAsStringAsync();
        Assert.True(placed.StatusCode == HttpStatusCode.Created, placedBody);
        var pallet = System.Text.Json.JsonSerializer.Deserialize<PreparationPalletRecord>(placedBody, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(pallet);
        var ready = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/warehouse/preparation/pallets/{pallet.Id}/ready")
        {
            Content = JsonContent.Create(new PalletVersionHttpRequest(pallet.RowVersion))
        };
        var readyResponse = await client.SendAsync(ready);
        Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
    }

    private async Task CompleteAsync(Guid requestId)
    {
        var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        await using (store)
        {
            await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds);
            await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Running);
            await store.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed);
        }
    }

    private sealed record Setup(LayoutContext Context, TestIdentity Operator, Guid Group, Guid MainPart, Guid AltPart, Guid Slot);
}
