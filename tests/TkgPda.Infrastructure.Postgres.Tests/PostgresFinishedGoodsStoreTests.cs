using Npgsql;
using TkgPda.Application;
using TkgPda.Domain;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Infrastructure.Postgres.Tests;

/// <summary>
/// The one link the SQL and HTTP checks cannot reach: the hook that records a
/// delivery when a transport actually completes. Everything here runs against a
/// real database and cleans up after itself.
/// </summary>
public sealed class PostgresFinishedGoodsStoreTests
{
    private readonly string _connectionString =
        Environment.GetEnvironmentVariable("TKG_PDA_TEST_CONNECTION_STRING") ?? string.Empty;

    [PostgresFact]
    public async Task Completing_a_removal_puts_the_container_in_the_linked_store_location()
    {
        await ResetAsync();
        var context = await ConfigureAsync();
        var machineSlotId = await LogisticsSlotAsync(context.LayoutId);
        var dropSlotId = await LogisticsSlotAsync(context.LayoutId, code: "TEST-STORE-DROP");
        await SetSlotAsync(machineSlotId, OccupancyState.Occupied, LoadState.Full);
        var locationId = await CreateStoreLocationAsync(capacity: 5);
        await LinkDeliverySlotAsync(dropSlotId, locationId);

        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var service = new TransportWorkflowService(store);
        var created = await service.CreateAsync(RemoveFull(context, "store-delivery", machineSlotId, dropSlotId));

        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.SentToRds);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Running);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Completed);

        var locations = await store.ListLocationsAsync();
        var location = Assert.Single(locations, candidate => candidate.Id == locationId);
        var unit = Assert.Single(location.Units);
        Assert.Equal(created.Request.Id, unit.TransportRequestId);
        Assert.Equal(context.MachineId, unit.MachineId);
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM ops.store_events WHERE store_location_id = @id AND event_type = 'UNIT_DELIVERED'",
            ("id", locationId)));
    }

    [PostgresFact]
    public async Task A_replayed_completion_does_not_fill_the_store_twice()
    {
        await ResetAsync();
        var context = await ConfigureAsync();
        var machineSlotId = await LogisticsSlotAsync(context.LayoutId);
        var dropSlotId = await LogisticsSlotAsync(context.LayoutId, code: "TEST-STORE-DROP");
        await SetSlotAsync(machineSlotId, OccupancyState.Occupied, LoadState.Full);
        var locationId = await CreateStoreLocationAsync(capacity: 5);
        await LinkDeliverySlotAsync(dropSlotId, locationId);

        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var service = new TransportWorkflowService(store);
        var created = await service.CreateAsync(RemoveFull(context, "store-replay", machineSlotId, dropSlotId));
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.SentToRds);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Running);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Completed);

        // Reconciliation re-reports the same terminal status.
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Completed);

        var locations = await store.ListLocationsAsync();
        Assert.Single(Assert.Single(locations, candidate => candidate.Id == locationId).Units);
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM ops.store_events WHERE store_location_id = @id AND event_type = 'UNIT_DELIVERED'",
            ("id", locationId)));
    }

    [PostgresFact]
    public async Task An_unlinked_drop_position_leaves_the_store_untouched()
    {
        // Until the customer says which store location a machine's DROP feeds,
        // a completion records nothing rather than guessing a location.
        await ResetAsync();
        var context = await ConfigureAsync();
        var machineSlotId = await LogisticsSlotAsync(context.LayoutId);
        var dropSlotId = await LogisticsSlotAsync(context.LayoutId, code: "TEST-STORE-DROP");
        await SetSlotAsync(machineSlotId, OccupancyState.Occupied, LoadState.Full);
        var locationId = await CreateStoreLocationAsync(capacity: 5);

        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var service = new TransportWorkflowService(store);
        var created = await service.CreateAsync(RemoveFull(context, "store-unlinked", machineSlotId, dropSlotId));
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.SentToRds);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Running);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Completed);

        var locations = await store.ListLocationsAsync();
        Assert.Empty(Assert.Single(locations, candidate => candidate.Id == locationId).Units);
    }

    [PostgresFact]
    public async Task Completing_non_remove_full_operations_never_creates_a_store_unit()
    {
        await ResetAsync();
        var context = await ConfigureAsync();
        var locationId = await CreateStoreLocationAsync(capacity: 5);
        var dropSlotId = await LayoutDropAsync(context.LayoutId);
        await LinkDeliverySlotAsync(dropSlotId, locationId);

        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var service = new TransportWorkflowService(store);

        var bringDest = await LogisticsSlotAsync(context.LayoutId, "TEST-FG-BE-DEST");
        await SetSlotAsync(bringDest, OccupancyState.Vacant, LoadState.Unknown);
        var bringBuffer = await CreateSlotAsync(context.LayoutId, "TEST-FG-BE-BUF", "EMPTY_STORAGE");
        await SetSlotAsync(bringBuffer, OccupancyState.Occupied, LoadState.Empty);
        var bringEmpty = await service.CreateAsync(
            Command(context, TransportTaskType.BringEmpty, "fg-be", source: bringBuffer, destination: bringDest));
        await CompleteAsync(service, bringEmpty.Request.Id);

        var bringFullDest = await LogisticsSlotAsync(context.LayoutId, "TEST-FG-BF-DEST");
        await SetSlotAsync(bringFullDest, OccupancyState.Vacant, LoadState.Unknown);
        var bringFull = await service.CreateAsync(
            Command(context, TransportTaskType.BringFull, "fg-bf", destination: bringFullDest));
        await CompleteAsync(service, bringFull.Request.Id);

        var removeSource = await LogisticsSlotAsync(context.LayoutId, "TEST-FG-RE-SRC");
        await SetSlotAsync(removeSource, OccupancyState.Occupied, LoadState.Empty);
        var removeBuffer = await CreateSlotAsync(context.LayoutId, "TEST-FG-RE-BUF", "EMPTY_STORAGE");
        await SetSlotAsync(removeBuffer, OccupancyState.Vacant, LoadState.Unknown);
        var removeEmpty = await service.CreateAsync(
            Command(context, TransportTaskType.RemoveEmpty, "fg-re", source: removeSource, destination: removeBuffer));
        await CompleteAsync(service, removeEmpty.Request.Id);

        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM ops.store_units WHERE load_state = 'FULL' AND transport_request_id IS NOT NULL"));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM ops.store_events WHERE event_type = 'UNIT_DELIVERED'"));
    }

    [PostgresFact]
    public async Task Machine_side_remove_full_without_a_mapped_destination_is_refused_before_insert()
    {
        await ResetAsync();
        var context = await ConfigureAsync();
        var machineSlotId = await LogisticsSlotAsync(context.LayoutId);
        await SetSlotAsync(machineSlotId, OccupancyState.Occupied, LoadState.Full);

        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(RemoveFull(context, "fg-unmapped-create", machineSlotId, destination: null)
                with { RequiresFinishedGoodsDestination = true }));

        Assert.Equal("FINISHED_GOODS_DESTINATION_NOT_CONFIGURED", error.Code);
        Assert.Contains("yapılandırılmamış", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
        Assert.Equal("FULL", await ScalarAsync<string>(
            "SELECT load_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot",
            ("slot", machineSlotId)));
    }

    [PostgresFact]
    public async Task Machine_side_remove_full_stores_the_resolved_mapped_drop()
    {
        await ResetAsync();
        var context = await ConfigureAsync();
        var machineSlotId = await LogisticsSlotAsync(context.LayoutId);
        await SetSlotAsync(machineSlotId, OccupancyState.Occupied, LoadState.Full);
        var dropSlotId = await LayoutDropAsync(context.LayoutId);
        var locationId = await CreateStoreLocationAsync(capacity: 5);
        await LinkDeliverySlotAsync(dropSlotId, locationId);

        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var created = await store.CreateAndQueueAsync(
            RemoveFull(context, "fg-mapped-create", machineSlotId, destination: null)
                with { RequiresFinishedGoodsDestination = true });

        Assert.Equal(dropSlotId, created.Request.DestinationSlotId);
        Assert.Equal(TransportTaskType.RemoveFull, created.Request.TaskType);
    }

    [PostgresFact]
    public async Task A_disabled_store_location_is_not_a_valid_finished_goods_destination()
    {
        await ResetAsync();
        var context = await ConfigureAsync();
        var machineSlotId = await LogisticsSlotAsync(context.LayoutId);
        await SetSlotAsync(machineSlotId, OccupancyState.Occupied, LoadState.Full);
        var dropSlotId = await LayoutDropAsync(context.LayoutId);
        var locationId = await CreateStoreLocationAsync(capacity: 5);
        await LinkDeliverySlotAsync(dropSlotId, locationId);
        await ExecuteAsync("UPDATE ops.store_locations SET is_enabled = false WHERE id = @id", ("id", locationId));

        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(RemoveFull(context, "fg-disabled-create", machineSlotId, destination: null)
                with { RequiresFinishedGoodsDestination = true }));

        Assert.Equal("FINISHED_GOODS_DESTINATION_NOT_CONFIGURED", error.Code);
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [PostgresFact]
    public async Task Removing_the_last_container_leaves_the_location_empty()
    {
        await ResetAsync();
        var context = await ConfigureAsync();
        var machineSlotId = await LogisticsSlotAsync(context.LayoutId);
        var dropSlotId = await LogisticsSlotAsync(context.LayoutId, code: "TEST-STORE-DROP");
        await SetSlotAsync(machineSlotId, OccupancyState.Occupied, LoadState.Full);
        var locationId = await CreateStoreLocationAsync(capacity: 5);
        await LinkDeliverySlotAsync(dropSlotId, locationId);

        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var service = new TransportWorkflowService(store);
        var created = await service.CreateAsync(RemoveFull(context, "store-remove", machineSlotId, dropSlotId));
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.SentToRds);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Running);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Completed);

        var before = Assert.Single(await store.ListLocationsAsync(), c => c.Id == locationId);
        var unit = Assert.Single(before.Units);
        var actor = await EnsureActorAsync();
        var device = await EnsureDeviceAsync();

        var after = await store.RemoveUnitAsync(
            new RemoveStoreUnitCommand(unit.Id, unit.RowVersion, actor, device));

        Assert.True(after.IsEmpty);
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM ops.store_events WHERE store_location_id = @id AND event_type = 'UNIT_REMOVED'",
            ("id", locationId)));
    }

    // ---- helpers -----------------------------------------------------------

    private sealed record LayoutContext(Guid MachineId, Guid VariantId, Guid LayoutId);

    private async Task ResetAsync() => await ExecuteAsync(
        """
        DELETE FROM ops.store_events
        WHERE store_location_id IN (SELECT id FROM ops.store_locations WHERE code LIKE 'TEST-%');
        DELETE FROM ops.store_units
        WHERE store_location_id IN (SELECT id FROM ops.store_locations WHERE code LIKE 'TEST-%');
        DELETE FROM ops.store_location_delivery_slots
        WHERE store_location_id IN (SELECT id FROM ops.store_locations WHERE code LIKE 'TEST-%')
           OR layout_slot_id IN (SELECT id FROM master.layout_slots WHERE code LIKE 'TEST-%');
        DELETE FROM ops.store_locations WHERE code LIKE 'TEST-%';
        TRUNCATE integration.rds_tasks, ops.transport_request_events, ops.transport_requests,
                 ops.slot_runtime_state, ops.machine_runtime_state RESTART IDENTITY CASCADE;
        DELETE FROM master.layout_slots WHERE code LIKE 'TEST-%';
        INSERT INTO ops.store_units (store_location_id, load_state)
        SELECT location.id, 'EMPTY'
        FROM ops.store_locations location
        WHERE location.code LIKE 'PHASE03-EMPTY-%'
          AND NOT EXISTS (
              SELECT 1 FROM ops.store_units unit
              WHERE unit.store_location_id = location.id AND unit.removed_at IS NULL);
        """);

    private async Task<LayoutContext> ConfigureAsync()
    {
        var machineId = await ScalarAsync<Guid>("SELECT id FROM master.machines WHERE code = 'SH-01-A'");
        var variantId = await ScalarAsync<Guid>(
            "SELECT id FROM master.variants WHERE machine_id = @machine AND code = '1'", ("machine", machineId));
        var layoutId = await ScalarAsync<Guid>(
            "SELECT layout_id FROM master.variant_layout_assignments WHERE variant_id = @variant LIMIT 1",
            ("variant", variantId));
        await ExecuteAsync(
            """
            INSERT INTO ops.machine_runtime_state (machine_id, active_variant_id, active_layout_id)
            VALUES (@machine, @variant, @layout)
            ON CONFLICT (machine_id) DO UPDATE
            SET active_variant_id = EXCLUDED.active_variant_id, active_layout_id = EXCLUDED.active_layout_id
            """,
            ("machine", machineId), ("variant", variantId), ("layout", layoutId));
        return new LayoutContext(machineId, variantId, layoutId);
    }

    private async Task<Guid> LogisticsSlotAsync(Guid layoutId, string code = "TEST-STORE-SLOT")
    {
        var slotId = await ScalarAsync<Guid>(
            """
            INSERT INTO master.layout_slots (layout_id, code, display_name, slot_role, display_order)
            VALUES (@layout, @code, @code, 'STAGING', 900)
            RETURNING id
            """,
            ("layout", layoutId), ("code", code));
        await ExecuteAsync(
            """
            INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state)
            VALUES (@slot, 'VACANT', 'UNKNOWN') ON CONFLICT (layout_slot_id) DO NOTHING
            """,
            ("slot", slotId));
        return slotId;
    }

    private Task SetSlotAsync(Guid slotId, OccupancyState occupancy, LoadState load) => ExecuteAsync(
        """
        INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state)
        VALUES (@slot, @occupancy, @load)
        ON CONFLICT (layout_slot_id) DO UPDATE
        SET occupancy_state = EXCLUDED.occupancy_state, load_state = EXCLUDED.load_state
        """,
        ("slot", slotId), ("occupancy", occupancy.ToDatabase()), ("load", load.ToDatabase()));

    private Task<Guid> CreateStoreLocationAsync(int? capacity) => ScalarAsync<Guid>(
        """
        INSERT INTO ops.store_locations (code, display_name, capacity, area_purpose, is_operator_visible)
        VALUES ('TEST-STORE-LOC', 'Test Store Location', @capacity, 'DELIVERY', true)
        RETURNING id
        """,
        ("capacity", capacity));

    private Task LinkDeliverySlotAsync(Guid slotId, Guid locationId) => ExecuteAsync(
        """
        INSERT INTO ops.store_location_delivery_slots (layout_slot_id, store_location_id)
        VALUES (@slot, @location)
        """,
        ("slot", slotId), ("location", locationId));

    private Task<Guid> EnsureActorAsync() => ScalarAsync<Guid>(
        """
        WITH actor AS (
            INSERT INTO auth.users (username, password_hash, full_name, employee_no, is_active)
            VALUES ('phase03-store-actor', 'test-only-hash', 'Phase 03 Store Actor', 'PHASE03-ACTOR', true)
            ON CONFLICT (username) DO UPDATE SET is_active = true
            RETURNING id
        )
        INSERT INTO auth.user_roles (user_id, role_id)
        SELECT actor.id, roles.id
        FROM actor
        JOIN auth.roles AS roles ON roles.code = 'WAREHOUSE_OPERATOR'
        ON CONFLICT DO NOTHING
        RETURNING user_id
        """);

    private Task<Guid> EnsureDeviceAsync() => ScalarAsync<Guid>(
        """
        INSERT INTO audit.client_devices (device_code, name, device_type, is_active)
        VALUES ('PHASE03-STORE', 'Phase 03 store device', 'PDA', true)
        ON CONFLICT (device_code) DO UPDATE SET is_active = true
        RETURNING id
        """);

    private async Task<Guid> LayoutDropAsync(Guid layoutId)
    {
        await ExecuteAsync(
            """
            INSERT INTO master.layout_slots (layout_id, code, display_name, slot_role, display_order)
            SELECT @layout, 'TEST-FG-DROP', 'Test Finished Goods Drop', 'DROP', 9001
            WHERE NOT EXISTS (
                SELECT 1 FROM master.layout_slots
                WHERE layout_id = @layout AND slot_role = 'DROP');
            INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state)
            SELECT id, 'VACANT', 'UNKNOWN' FROM master.layout_slots
            WHERE layout_id = @layout AND slot_role = 'DROP'
            ON CONFLICT (layout_slot_id) DO NOTHING;
            """,
            ("layout", layoutId));
        return await ScalarAsync<Guid>(
            """
            SELECT id FROM master.layout_slots
            WHERE layout_id = @layout AND slot_role = 'DROP' AND is_enabled
            ORDER BY display_order, id
            LIMIT 1
            """,
            ("layout", layoutId));
    }

    private async Task<Guid> CreateSlotAsync(Guid layoutId, string code, string role)
    {
        var slotId = await ScalarAsync<Guid>(
            """
            INSERT INTO master.layout_slots (layout_id, code, display_name, slot_role, display_order)
            VALUES (@layout, @code, @code, @role, 910)
            RETURNING id
            """,
            ("layout", layoutId), ("code", code), ("role", role));
        await ExecuteAsync(
            """
            INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state)
            VALUES (@slot, 'VACANT', 'UNKNOWN') ON CONFLICT (layout_slot_id) DO NOTHING
            """,
            ("slot", slotId));
        return slotId;
    }

    private static async Task CompleteAsync(TransportWorkflowService service, Guid requestId)
    {
        await service.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds);
        await service.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Running);
        await service.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Completed);
    }

    private static CreateTransportRequest Command(
        LayoutContext context,
        TransportTaskType type,
        string key,
        Guid? source = null,
        Guid? destination = null) => new(
        key,
        RequestOrigin.LineOperator,
        null,
        context.MachineId,
        context.VariantId,
        context.LayoutId,
        null,
        null,
        null,
        null,
        type,
        source,
        destination);

    private static CreateTransportRequest RemoveFull(
        LayoutContext context, string key, Guid source, Guid? destination) => new(
        key,
        RequestOrigin.LineOperator,
        null,
        context.MachineId,
        context.VariantId,
        context.LayoutId,
        null,
        null,
        null,
        null,
        TransportTaskType.RemoveFull,
        source,
        destination);

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }
}
