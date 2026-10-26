using System.Net;
using System.Net.Http.Json;
using Npgsql;
using TkgPda.Api;
using TkgPda.Application;
using TkgPda.Domain;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class PartDemandApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Alt_demand_waits_for_machine_before_locking_the_production_session(bool batch)
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);
        await using var blocker = new NpgsqlConnection(fixture.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var machineLock = new NpgsqlCommand("SELECT 1 FROM ops.machine_runtime_state WHERE machine_id = @machine FOR UPDATE", blocker, transaction))
        {
            machineLock.Parameters.AddWithValue("machine", setup.Context.MachineId);
            await machineLock.ExecuteScalarAsync();
        }

        var pending = batch
            ? BatchAsync(client, setup, [setup.AltPart], "concurrent-batch")
            : DemandAsync(client, setup, setup.AltPart, "concurrent-single");
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            var waiting = false;
            while (DateTimeOffset.UtcNow < deadline && !pending.IsCompleted)
            {
                waiting = await fixture.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE @pid = ANY(pg_blocking_pids(pid)))", ("pid", blocker.ProcessID));
                if (waiting) break;
                await Task.Delay(25);
            }
            Assert.True(waiting, "Demand must actually overlap the held machine lock.");
            // An End transaction already holding machine must be able to acquire session.
            // The old session -> machine order causes PostgreSQL 55P03 here.
            await using var sessionLock = new NpgsqlCommand("SELECT 1 FROM ops.production_sessions WHERE machine_id = @machine AND ended_at IS NULL FOR UPDATE NOWAIT", blocker, transaction);
            sessionLock.Parameters.AddWithValue("machine", setup.Context.MachineId);
            Assert.NotNull(await sessionLock.ExecuteScalarAsync());
        }
        finally
        {
            await transaction.RollbackAsync();
        }
        Assert.Equal(HttpStatusCode.Created, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Restart_empty_pallet_chain_preserves_dependency_and_releases_successor_once()
    {
        await using var api = new RestartApiProcess(fixture.ConnectionString);
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "EMPTY");
        await api.StartAsync();
        using var client = await api.LoginAsync(setup.Operator);
        await StartProductionAsync(client, setup);
        var created = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "restart-alt"));
        var successor = await RequestIdAsync(created.RequestNo);
        var predecessor = await PredecessorIdAsync(successor);
        await MarkPreparedAsync(setup, successor);
        await using (var activationStore = new PostgresTransportWorkflowStore(fixture.ConnectionString))
        {
            Assert.Equal(0, await activationStore.ActivatePreparedPartDemandsAsync(10, null));
        }
        Assert.Equal(0L, await EligibleCountAsync(successor));
        await api.RestartAsync();
        using var restarted = await api.LoginAsync(setup.Operator);
        var replay = await ReadDemandAsync(await DemandAsync(restarted, setup, setup.AltPart, "restart-alt"));
        Assert.Equal(created.RequestNo, replay.RequestNo);
        Assert.NotNull(replay.PredecessorRequestNo);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_request_dependencies WHERE predecessor_request_id=@p AND successor_request_id=@s", ("p", predecessor), ("s", successor)));
        Assert.Equal(0L, await EligibleCountAsync(successor));
        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        await store.RecordExecutionTransitionAsync(predecessor, TransportRequestStatus.SentToRds);
        await store.RecordExecutionTransitionAsync(predecessor, TransportRequestStatus.Running);
        await store.RecordExecutionTransitionAsync(predecessor, TransportRequestStatus.Completed);
        await store.RecordExecutionTransitionAsync(predecessor, TransportRequestStatus.Completed);
        Assert.Equal(1L, await EligibleCountAsync(successor));
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_request_events WHERE transport_request_id=@id AND event_type='REQUEST_COMPLETED'", ("id", predecessor)));
        Assert.Equal("QUEUED", await fixture.ScalarAsync<string>("SELECT status FROM ops.transport_requests WHERE id=@id", ("id", successor)));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.rds_tasks"));
    }

    [Fact]
    public async Task Concurrent_alt_orders_with_distinct_keys_create_independent_queued_pallet_demands()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);
        var responses = await Task.WhenAll(DemandAsync(client, setup, setup.AltPart, "click-a"), DemandAsync(client, setup, setup.AltPart, "click-b"));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Alt_part_without_container_type_is_rejected_before_a_request_exists()
    {
        var setup = await SetupAsync();
        await fixture.ExecuteAsync(
            "UPDATE master.product_group_parts SET container_type_id = NULL WHERE part_id = @part",
            ("part", setup.AltPart));
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var rejected = await DemandAsync(client, setup, setup.AltPart, "missing-container");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal("CONTAINER_TYPE_MISSING", await ApiTestFixture.ErrorCodeAsync(rejected));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        var options = await client.GetFromJsonAsync<List<MachineMainPartOptionRecord>>($"/api/v1/machines/{setup.Context.MachineId}/part-options");
        var alt = options!.SelectMany(option => option.RelatedParts).Single(part => part.PartId == setup.AltPart);
        Assert.False(alt.Requestable);
        Assert.Contains("CONTAINER_TYPE_MISSING", alt.Blockers!);
    }

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
    public async Task A_new_key_creates_another_independent_pallet_demand_while_one_is_active()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);
        await DemandAsync(client, setup, setup.AltPart, "active-first");

        var duplicate = await DemandAsync(client, setup, setup.AltPart, "active-second");

        Assert.Equal(HttpStatusCode.Created, duplicate.StatusCode);
        Assert.False((await ReadDemandAsync(duplicate)).IdempotentReplay);
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Batch_order_creates_its_own_pallet_demand_when_the_same_part_is_already_queued()
    {
        var setup = await SetupAsync();
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);
        await DemandAsync(client, setup, setup.AltPart, "single-before-batch");

        var created = await ReadBatchAsync(await BatchAsync(
            client,
            setup,
            [setup.AltPart],
            "batch-after-single"));

        Assert.Single(created.Items);
        Assert.False(created.IdempotentReplay);
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE batch_id IS NOT NULL"));

        var replay = await ReadBatchAsync(await BatchAsync(
            client,
            setup,
            [setup.AltPart],
            "batch-after-single"));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(created.Items.Single().RequestNo, replay.Items.Single().RequestNo);
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Occupied_full_area_keeps_the_new_order_queued_until_the_current_pallet_is_empty()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "FULL");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var body = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "again-full"));
        var successorId = await RequestIdAsync(body.RequestNo);

        Assert.Equal("BRING_FULL", body.TaskType);
        Assert.Null(body.PredecessorTaskType);
        Assert.Equal("WAITING_PREPARATION", body.Readiness);
        Assert.False(await ReservationActiveAsync(successorId));
        await MarkPreparedAsync(setup, successorId);
        Assert.Equal(0, await ActivatePreparedAsync());
        Assert.False(await ReservationActiveAsync(successorId));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));

        await fixture.ExecuteAsync(
            "UPDATE ops.slot_runtime_state SET load_state = 'EMPTY', row_version = row_version + 1 WHERE layout_slot_id = @slot",
            ("slot", setup.Slot));
        Assert.Equal(1, await ActivatePreparedAsync());
        var predecessorId = await PredecessorIdAsync(successorId);
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_request_dependencies"));
        Assert.Equal("REMOVE_EMPTY", await TaskTypeAsync(predecessorId));
        Assert.True(await ReservationActiveAsync(successorId));
    }

    [Fact]
    public async Task Occupied_empty_area_creates_remove_empty_then_dependent_bring_full()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "EMPTY");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var body = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "again-empty"));
        var successorId = await RequestIdAsync(body.RequestNo);

        Assert.Equal("BRING_FULL", body.TaskType);
        Assert.Equal("REMOVE_EMPTY", body.PredecessorTaskType);
        var predecessorId = await PredecessorIdAsync(successorId);
        Assert.Equal("WAITING_PREPARATION", body.Readiness);
        Assert.Equal("REMOVE_EMPTY", await TaskTypeAsync(predecessorId));
        Assert.Equal(1L, await EligibleCountAsync(predecessorId));
        Assert.Equal(0L, await EligibleCountAsync(successorId));
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_request_dependencies"));

        await MarkPreparedAsync(setup, successorId);
        Assert.Equal(0, await ActivatePreparedAsync());
        Assert.Equal(0L, await EligibleCountAsync(successorId));
    }

    [Fact]
    public async Task Dependent_bring_full_waits_for_predecessor_completion_and_warehouse_readiness()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "EMPTY");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);
        var body = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "chain-gate"));
        var successorId = await RequestIdAsync(body.RequestNo);
        var predecessorId = await PredecessorIdAsync(successorId);

        await MarkPreparedAsync(setup, successorId);
        Assert.Equal(0, await ActivatePreparedAsync());
        Assert.Equal(0L, await EligibleCountAsync(successorId));

        await CompleteAsync(predecessorId);
        Assert.Equal("READY", await fixture.ScalarAsync<string>("SELECT readiness_state FROM ops.transport_requests WHERE id = @id", ("id", successorId)));
        Assert.Equal(1L, await EligibleCountAsync(successorId));
    }

    [Fact]
    public async Task Unprepared_demand_starts_empty_removal_and_reserves_the_dependent_delivery()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "EMPTY");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);
        var body = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "not-ready"));
        var successorId = await RequestIdAsync(body.RequestNo);

        Assert.Equal(0, await ActivatePreparedAsync());
        Assert.Equal("WAITING_PREPARATION", await fixture.ScalarAsync<string>(
            "SELECT readiness_state FROM ops.transport_requests WHERE id = @id", ("id", successorId)));
        Assert.True(await ReservationActiveAsync(successorId));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_request_dependencies WHERE successor_request_id = @id", ("id", successorId)));
        Assert.Equal(0L, await EligibleCountAsync(successorId));
        Assert.Equal(1L, await EligibleCountAsync(await PredecessorIdAsync(successorId)));
    }

    [Fact]
    public async Task Unknown_area_accepts_the_warehouse_order_but_keeps_robot_dispatch_closed()
    {
        var setup = await SetupAsync(occupancy: "UNKNOWN", load: "UNKNOWN");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var response = await DemandAsync(client, setup, setup.AltPart, "unknown-area");

        var body = await ReadDemandAsync(response);
        var requestId = await RequestIdAsync(body.RequestNo);
        await MarkPreparedAsync(setup, requestId);
        Assert.Equal(0, await ActivatePreparedAsync());
        Assert.False(await ReservationActiveAsync(requestId));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
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
        var containerTypeId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
        await fixture.ExecuteAsync(
            """
            INSERT INTO master.product_group_parts (product_group_id, part_id, container_type_id, relation_type, is_active)
            VALUES (@group, @part, @container, 'ALT_PART', true)
            """,
            ("group", setup.Group), ("part", second), ("container", containerTypeId));
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
    public async Task Batch_parts_sharing_one_machine_slot_create_separate_pallet_orders_and_serialize_dispatch()
    {
        var setup = await SetupAsync();
        var second = await AddAltPartToSlotAsync(setup, "P05-ALT-SHARED", setup.Slot);
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var batch = await ReadBatchAsync(await BatchAsync(
            client,
            setup,
            [setup.AltPart, second],
            "shared-slot-batch"));
        var requestIds = new List<Guid>();
        foreach (var item in batch.Items)
        {
            requestIds.Add(await RequestIdAsync(item.RequestNo));
        }

        Assert.Equal(2, batch.Items.Count);
        Assert.All(batch.Items, item =>
        {
            Assert.Equal("BRING_FULL", item.TaskType);
            Assert.Equal("WAITING_PREPARATION", item.Readiness);
            Assert.Null(item.PredecessorRequestNo);
        });
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE id = ANY(@ids) AND machine_slot_reservation_active",
            ("ids", requestIds.ToArray())));

        var warehouse = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator, machineCode: "SH-01-A");
        using (var warehouseClient = await fixture.LoginClientAsync(warehouse))
        {
            var visible = await warehouseClient.GetFromJsonAsync<List<WarehousePreparationRequestRecord>>(
                "/api/v1/warehouse/preparation-requests");
            Assert.NotNull(visible);
            Assert.Equal(2, visible.Count(item => requestIds.Contains(item.RequestId)));
        }

        await MarkPreparedAsync(setup, requestIds[0], setup.AltPart, areaOffset: 0);
        await MarkPreparedAsync(setup, requestIds[1], second, areaOffset: 1);
        await using (var firstWorker = new PostgresTransportWorkflowStore(fixture.ConnectionString))
        await using (var secondWorker = new PostgresTransportWorkflowStore(fixture.ConnectionString))
        {
            var activations = await Task.WhenAll(
                firstWorker.ActivatePreparedPartDemandsAsync(100, null),
                secondWorker.ActivatePreparedPartDemandsAsync(100, null));
            Assert.Equal(1, activations.Sum());
        }
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE id = ANY(@ids) AND machine_slot_reservation_active",
            ("ids", requestIds.ToArray())));
        Assert.Equal(0, await ActivatePreparedAsync());

        var firstActive = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM ops.transport_requests WHERE id = ANY(@ids) AND machine_slot_reservation_active",
            ("ids", requestIds.ToArray()));
        var secondWaiting = requestIds.Single(id => id != firstActive);

        // Reproduce the demo-server failure: the old simulation worker filled
        // every EMPTY_STORAGE position, leaving no VACANT/UNKNOWN destination
        // for the REMOVE_EMPTY predecessor needed by the second pallet.
        await fixture.ExecuteAsync(
            """
            UPDATE ops.slot_runtime_state state
            SET occupancy_state = 'OCCUPIED',
                load_state = 'EMPTY',
                current_part_id = NULL,
                current_container_type_id = NULL
            FROM master.layout_slots slot
            WHERE state.layout_slot_id = slot.id
              AND slot.layout_id = @layout
              AND slot.slot_role = 'EMPTY_STORAGE'
            """,
            ("layout", setup.Context.LayoutId));

        await CompleteAsync(firstActive);
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            Assert.Equal(1, await DemoSimulationWorker.SimulateConsumedSharedSlotPalletsAsync(
                connection,
                DateTimeOffset.UtcNow.AddSeconds(1),
                CancellationToken.None));

            Assert.Equal(0, await ActivatePreparedAsync());
            Assert.Equal(1, await DemoSimulationWorker.PrepareNextEmptyBufferAsync(
                connection,
                CancellationToken.None));
        }

        Assert.Equal(1, await ActivatePreparedAsync());
        Assert.True(await ReservationActiveAsync(secondWaiting));
        var predecessorId = await PredecessorIdAsync(secondWaiting);
        Assert.Equal("REMOVE_EMPTY", await TaskTypeAsync(predecessorId));
        Assert.Equal("VACANT", await fixture.ScalarAsync<string>(
            """
            SELECT state.occupancy_state
            FROM ops.transport_requests request
            JOIN master.layout_slots slot ON slot.id = request.destination_slot_id
            JOIN ops.slot_runtime_state state ON state.layout_slot_id = slot.id
            WHERE request.id = @id AND slot.slot_role = 'EMPTY_STORAGE'
            """,
            ("id", predecessorId)));
    }

    [Fact]
    public async Task Occupied_by_a_different_part_uses_a_vacant_slot_without_a_predecessor()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "FULL");
        var other = await fixture.ScalarAsync<Guid>("INSERT INTO master.parts (part_code) VALUES ('P05-OTHER') RETURNING id");
        await fixture.SetSlotAsync(setup.Slot, "OCCUPIED", "FULL", other);
        var vacant = await NextSlotAsync(setup.Context.LayoutId, setup.Slot);
        await fixture.ExecuteAsync(
            "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot, @part)",
            ("slot", vacant), ("part", setup.AltPart));
        await fixture.SetSlotAsync(vacant, "VACANT", "UNKNOWN");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var body = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "other-plus-vacant"));

        Assert.Equal("BRING_FULL", body.TaskType);
        Assert.Null(body.PredecessorRequestNo);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_request_dependencies"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.transport_requests WHERE source_slot_id = @slot OR destination_slot_id = @slot",
            ("slot", setup.Slot)));
        Assert.False(await ReservationActiveAsync(await RequestIdAsync(body.RequestNo)));
    }

    [Fact]
    public async Task Occupied_by_a_different_full_part_creates_an_independent_order_and_waits_for_the_slot()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "FULL");
        var other = await fixture.ScalarAsync<Guid>("INSERT INTO master.parts (part_code) VALUES ('P05-OTHER') RETURNING id");
        await fixture.SetSlotAsync(setup.Slot, "OCCUPIED", "FULL", other);
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var response = await DemandAsync(client, setup, setup.AltPart, "other-blocked");

        var body = await ReadDemandAsync(response);
        var requestId = await RequestIdAsync(body.RequestNo);
        await MarkPreparedAsync(setup, requestId);
        Assert.Equal(0, await ActivatePreparedAsync());
        Assert.Equal("READY", await fixture.ScalarAsync<string>(
            "SELECT readiness_state FROM ops.transport_requests WHERE id = @id", ("id", requestId)));
        Assert.False(await ReservationActiveAsync(requestId));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Occupied_without_a_known_part_accepts_preparation_but_does_not_dispatch()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "FULL");
        await fixture.SetSlotAsync(setup.Slot, "OCCUPIED", "FULL", null);
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var response = await DemandAsync(client, setup, setup.AltPart, "unknown-part");

        var body = await ReadDemandAsync(response);
        var requestId = await RequestIdAsync(body.RequestNo);
        await MarkPreparedAsync(setup, requestId);
        Assert.Equal(0, await ActivatePreparedAsync());
        Assert.False(await ReservationActiveAsync(requestId));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Multiple_mapped_slots_choose_one_preparation_destination_deterministically()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "FULL");
        var second = await NextSlotAsync(setup.Context.LayoutId, setup.Slot);
        await fixture.ExecuteAsync(
            "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot, @part)",
            ("slot", second), ("part", setup.AltPart));
        await fixture.SetSlotAsync(second, "OCCUPIED", "EMPTY", setup.AltPart);
        await SetOccupiedContainerAsync(second);
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var response = await DemandAsync(client, setup, setup.AltPart, "two-same");

        var body = await ReadDemandAsync(response);
        var requestId = await RequestIdAsync(body.RequestNo);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(setup.Slot, await fixture.ScalarAsync<Guid>(
            "SELECT destination_slot_id FROM ops.transport_requests WHERE id = @id", ("id", requestId)));
        Assert.False(await ReservationActiveAsync(requestId));
    }

    [Fact]
    public async Task Completed_alt_part_removal_does_not_enter_finished_goods_or_notify()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "EMPTY");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);
        var body = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "not-finished-goods"));
        var successorId = await RequestIdAsync(body.RequestNo);
        await MarkPreparedAsync(setup, successorId);
        Assert.Equal(0, await ActivatePreparedAsync());
        var predecessorId = await PredecessorIdAsync(successorId);

        Assert.Equal("EMPTY_STORAGE", await fixture.ScalarAsync<string>(
            """
            SELECT ls.slot_role
            FROM ops.transport_requests r
            JOIN master.layout_slots ls ON ls.id = r.destination_slot_id
            WHERE r.id = @id
            """,
            ("id", predecessorId)));
        await CompleteAsync(predecessorId);

        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.store_units WHERE load_state = 'FULL' OR transport_request_id IS NOT NULL"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.notification_outbox"));
    }

    [Fact]
    public async Task Cancelling_the_predecessor_cancels_the_successor_without_changing_the_slot()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "EMPTY");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);
        var body = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "cancel-chain"));
        var successorId = await RequestIdAsync(body.RequestNo);
        await MarkPreparedAsync(setup, successorId);
        Assert.Equal(0, await ActivatePreparedAsync());
        var predecessorId = await PredecessorIdAsync(successorId);

        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        await store.CancelAsync(predecessorId, setup.Operator.UserId);
        await store.CancelAsync(predecessorId, setup.Operator.UserId);

        Assert.Equal("CANCELLED", await fixture.ScalarAsync<string>("SELECT status FROM ops.transport_requests WHERE id = @id", ("id", successorId)));
        Assert.Equal(1L, await CancelledEventsAsync(successorId));
        Assert.Equal(0L, await EligibleCountAsync(successorId));
        Assert.Equal(setup.AltPart, await fixture.ScalarAsync<Guid>(
            "SELECT current_part_id FROM ops.slot_runtime_state WHERE layout_slot_id = @slot",
            ("slot", setup.Slot)));
        Assert.Equal("EMPTY", await fixture.ScalarAsync<string>(
            "SELECT load_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot",
            ("slot", setup.Slot)));
    }

    [Fact]
    public async Task Cancelling_a_prepared_successor_waiting_for_removal_does_not_clear_the_occupied_slot()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "EMPTY");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);
        var body = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "cancel-successor"));
        var successorId = await RequestIdAsync(body.RequestNo);
        await MarkPreparedAsync(setup, successorId);
        Assert.Equal(0, await ActivatePreparedAsync());
        var predecessorId = await PredecessorIdAsync(successorId);

        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        await store.CancelAsync(successorId, setup.Operator.UserId);

        Assert.Equal("CANCELLED", await fixture.ScalarAsync<string>(
            "SELECT status FROM ops.transport_requests WHERE id = @id", ("id", successorId)));
        Assert.Equal("QUEUED", await fixture.ScalarAsync<string>(
            "SELECT status FROM ops.transport_requests WHERE id = @id", ("id", predecessorId)));
        Assert.Equal("OCCUPIED", await fixture.ScalarAsync<string>(
            "SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot",
            ("slot", setup.Slot)));
        Assert.Equal("EMPTY", await fixture.ScalarAsync<string>(
            "SELECT load_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot",
            ("slot", setup.Slot)));
        Assert.Equal(setup.AltPart, await fixture.ScalarAsync<Guid>(
            "SELECT current_part_id FROM ops.slot_runtime_state WHERE layout_slot_id = @slot",
            ("slot", setup.Slot)));
    }

    [Fact]
    public async Task Failing_the_predecessor_cancels_the_successor_once()
    {
        var setup = await SetupAsync(occupancy: "OCCUPIED", load: "EMPTY");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);
        var body = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "fail-chain"));
        var successorId = await RequestIdAsync(body.RequestNo);
        await MarkPreparedAsync(setup, successorId);
        Assert.Equal(0, await ActivatePreparedAsync());
        var predecessorId = await PredecessorIdAsync(successorId);

        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        await store.RecordExecutionTransitionAsync(predecessorId, TransportRequestStatus.SentToRds);
        var outcome = new ExecutionOutcome(TransportRequestStatus.Failed, "ROBOT_FAULT", "observed fault", "TEST");
        await store.RecordExecutionOutcomeAsync(predecessorId, outcome);
        await store.RecordExecutionOutcomeAsync(predecessorId, outcome);

        Assert.Equal("CANCELLED", await fixture.ScalarAsync<string>("SELECT status FROM ops.transport_requests WHERE id = @id", ("id", successorId)));
        Assert.Equal(1L, await CancelledEventsAsync(successorId));
        Assert.Equal(0L, await EligibleCountAsync(successorId));
        Assert.Equal("UNKNOWN", await fixture.ScalarAsync<string>(
            "SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot",
            ("slot", setup.Slot)));
        Assert.Equal("UNKNOWN", await fixture.ScalarAsync<string>(
            "SELECT load_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot",
            ("slot", setup.Slot)));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.slot_runtime_state WHERE layout_slot_id = @slot AND current_part_id IS NOT NULL",
            ("slot", setup.Slot)));
    }

    [Fact]
    public async Task Single_part_key_replays_only_for_the_same_actor_machine_and_part()
    {
        var setup = await SetupAsync();
        var second = await AddAltPartAsync(setup, "P05-ALT-OWNER");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var created = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "single-owner"));
        var replay = await ReadDemandAsync(await DemandAsync(client, setup, setup.AltPart, "single-owner"));
        Assert.Equal(created.RequestNo, replay.RequestNo);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));

        var otherOperator = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator, machineCode: "SH-01-A");
        using var otherClient = await fixture.LoginClientAsync(otherOperator);
        var otherActor = await DemandAsync(otherClient, setup, setup.AltPart, "single-owner");
        Assert.Equal(HttpStatusCode.Conflict, otherActor.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", await ApiTestFixture.ErrorCodeAsync(otherActor));

        var otherPart = await DemandAsync(client, setup, second, "single-owner");
        Assert.Equal(HttpStatusCode.Conflict, otherPart.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", await ApiTestFixture.ErrorCodeAsync(otherPart));

        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        var otherMachine = await Assert.ThrowsAsync<WorkflowRuleException>(() => store.CreateAsync(
            new PartDemandCommand("single-owner", setup.Operator.UserId, Guid.NewGuid(), setup.AltPart, null)));
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", otherMachine.Code);

        await fixture.ExecuteAsync(
            "UPDATE ops.transport_requests SET request_origin = 'PLC' WHERE idempotency_key = 'single-owner'");
        var otherOrigin = await Assert.ThrowsAsync<WorkflowRuleException>(() => store.CreateAsync(
            new PartDemandCommand("single-owner", setup.Operator.UserId, setup.Context.MachineId, setup.AltPart, null)));
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", otherOrigin.Code);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Batch_key_replays_the_same_part_set_and_rejects_a_different_one()
    {
        var setup = await SetupAsync();
        var second = await AddAltPartAsync(setup, "P05-ALT-B");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);

        var created = await ReadBatchAsync(await BatchAsync(client, setup, [setup.AltPart, second], "batch-key"));
        var reversed = await ReadBatchAsync(await BatchAsync(client, setup, [second, setup.AltPart], "batch-key"));
        Assert.Equal(created.Items.Select(item => item.RequestNo).Order(), reversed.Items.Select(item => item.RequestNo).Order());
        Assert.True(reversed.IdempotentReplay);
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));

        var added = await BatchAsync(client, setup, [setup.AltPart, second, setup.MainPart], "batch-key");
        Assert.Equal(HttpStatusCode.Conflict, added.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", await ApiTestFixture.ErrorCodeAsync(added));
        var removed = await BatchAsync(client, setup, [setup.AltPart], "batch-key");
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", await ApiTestFixture.ErrorCodeAsync(removed));
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));

        var duplicate = await BatchAsync(client, setup, [setup.AltPart, setup.AltPart], "batch-duplicate");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Equal("PART_DEMAND_DUPLICATE", await ApiTestFixture.ErrorCodeAsync(duplicate));
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));

        var again = await ReadBatchAsync(await BatchAsync(client, setup, [setup.AltPart, second], "batch-key"));
        Assert.Equal(2, again.Items.Count);
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [Fact]
    public async Task Batch_key_cannot_be_reused_for_another_machine_or_actor()
    {
        var setup = await SetupAsync();
        var second = await AddAltPartAsync(setup, "P05-ALT-C");
        using var client = await fixture.LoginClientAsync(setup.Operator);
        await StartProductionAsync(client, setup);
        Assert.Equal(HttpStatusCode.Created, (await BatchAsync(client, setup, [setup.AltPart, second], "batch-owner")).StatusCode);

        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        var otherMachine = await Assert.ThrowsAsync<WorkflowRuleException>(() => store.CreateBatchAsync(
            new PartDemandBatchCommand("batch-owner", setup.Operator.UserId, Guid.NewGuid(), [setup.AltPart, second], null)));
        var otherActor = await Assert.ThrowsAsync<WorkflowRuleException>(() => store.CreateBatchAsync(
            new PartDemandBatchCommand("batch-owner", Guid.NewGuid(), setup.Context.MachineId, [second, setup.AltPart], null)));

        Assert.Equal("IDEMPOTENCY_KEY_REUSED", otherMachine.Code);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", otherActor.Code);
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.transport_batches"));
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
        await fixture.SetSlotAsync(slot, occupancy, load, occupancy == "OCCUPIED" ? alt : null);
        if (occupancy == "OCCUPIED")
        {
            await SetOccupiedContainerAsync(slot, containerTypeId);
        }
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

    private async Task<Guid> AddAltPartAsync(Setup setup, string code)
    {
        var slot = await NextSlotAsync(setup.Context.LayoutId, setup.Slot);
        await fixture.SetSlotAsync(slot, "VACANT", "UNKNOWN");
        return await AddAltPartToSlotAsync(setup, code, slot);
    }

    private async Task<Guid> AddAltPartToSlotAsync(Setup setup, string code, Guid slot)
    {
        var part = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.parts (part_code, description) VALUES (@code, @code) RETURNING id",
            ("code", code));
        await fixture.ExecuteAsync(
            "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot, @part)",
            ("slot", slot), ("part", part));
        var containerTypeId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
        await fixture.ExecuteAsync(
            """
            INSERT INTO master.product_group_parts (product_group_id, part_id, container_type_id, relation_type, is_active)
            VALUES (@group, @part, @container, 'ALT_PART', true)
            """,
            ("group", setup.Group), ("part", part), ("container", containerTypeId));
        return part;
    }

    private static async Task<PartDemandBatchHttpResponse> ReadBatchAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return System.Text.Json.JsonSerializer.Deserialize<PartDemandBatchHttpResponse>(
            body,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private Task<long> CancelledEventsAsync(Guid requestId) => fixture.ScalarAsync<long>(
        """
        SELECT count(*)
        FROM ops.transport_request_events
        WHERE transport_request_id = @id AND event_type = 'REQUEST_CANCELLED'
        """,
        ("id", requestId));

    private Task<long> EligibleCountAsync(Guid requestId) => fixture.ScalarAsync<long>(
        """
        SELECT count(*)
        FROM ops.transport_requests
        WHERE id = @id
          AND status = 'QUEUED'
          AND readiness_state <> 'WAITING_PREPARATION'
          AND machine_slot_reservation_active
          AND store_endpoint_reservation_active
          AND NOT EXISTS (
              SELECT 1
              FROM ops.transport_request_dependencies d
              JOIN ops.transport_requests pred ON pred.id = d.predecessor_request_id
              WHERE d.successor_request_id = ops.transport_requests.id
                AND pred.status <> 'COMPLETED')
        """,
        ("id", requestId));

    private Task MarkPreparedAsync(Setup setup, Guid requestId) =>
        MarkPreparedAsync(setup, requestId, setup.AltPart, areaOffset: 0);

    private async Task MarkPreparedAsync(Setup setup, Guid requestId, Guid partId, int areaOffset)
    {
        var warehouse = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator, machineCode: "SH-01-A");
        using var client = await fixture.LoginClientAsync(warehouse);
        var areaId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM ops.preparation_areas WHERE is_enabled ORDER BY area_number OFFSET @offset LIMIT 1",
            ("offset", areaOffset));
        var containerTypeId = await fixture.ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
        var place = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/warehouse/preparation/areas/{areaId}/pallet")
        {
            Content = JsonContent.Create(new PlacePalletHttpRequest("FULL", containerTypeId, partId, requestId))
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

    private async Task<int> ActivatePreparedAsync()
    {
        await using var store = new PostgresTransportWorkflowStore(fixture.ConnectionString);
        return await store.ActivatePreparedPartDemandsAsync(100, null);
    }

    private Task<bool> ReservationActiveAsync(Guid requestId) => fixture.ScalarAsync<bool>(
        "SELECT machine_slot_reservation_active FROM ops.transport_requests WHERE id = @id",
        ("id", requestId));

    private Task<Guid> PredecessorIdAsync(Guid successorId) => fixture.ScalarAsync<Guid>(
        "SELECT predecessor_request_id FROM ops.transport_request_dependencies WHERE successor_request_id = @id",
        ("id", successorId));

    private Task<string> TaskTypeAsync(Guid requestId) => fixture.ScalarAsync<string>(
        "SELECT tt.code FROM ops.transport_requests r JOIN master.task_types tt ON tt.id = r.task_type_id WHERE r.id = @id",
        ("id", requestId));

    private async Task SetOccupiedContainerAsync(Guid slotId, Guid? containerTypeId = null)
    {
        var resolved = containerTypeId ?? await fixture.ScalarAsync<Guid>(
            "SELECT id FROM master.container_types ORDER BY code LIMIT 1");
        await fixture.ExecuteAsync(
            "UPDATE ops.slot_runtime_state SET current_container_type_id = @container WHERE layout_slot_id = @slot",
            ("container", resolved), ("slot", slotId));
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
