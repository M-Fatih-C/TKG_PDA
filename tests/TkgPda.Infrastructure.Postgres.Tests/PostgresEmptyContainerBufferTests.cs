using Npgsql;
using TkgPda.Application;
using TkgPda.Domain;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Infrastructure.Postgres.Tests;

// Customer Workflow — Empty Container Buffer: BRING_EMPTY draws an empty
// container from a configured EMPTY_STORAGE slot; REMOVE_EMPTY returns one to
// one. Buffer eligibility comes only from master.layout_slots.slot_role —
// never from slot name, WorkSite name, machine code or coordinates.
// REMOVE_FULL never touches the buffer. This file owns its own dedicated
// layout slots per test (rather than the shared pool other files provision)
// so each test can precisely control which/how many EMPTY_STORAGE slots
// exist.
public sealed class PostgresEmptyContainerBufferTests
{
    private readonly string _connectionString = Environment.GetEnvironmentVariable("TKG_PDA_TEST_CONNECTION_STRING") ?? string.Empty;

    [PostgresFact]
    public async Task Bring_empty_ignores_a_same_state_slot_that_is_not_configured_as_EMPTY_STORAGE()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var destination = await LogisticsSlotAsync(context.LayoutId);
        await SetSlotAsync(destination, OccupancyState.Vacant, LoadState.Unknown);
        // Same occupancy/load a buffer would need, but the wrong role — must
        // never be picked.
        var wrongRoleSlot = await CreateSlotAsync(context.LayoutId, "WRONG-ROLE", "STAGING");
        await SetSlotAsync(wrongRoleSlot, OccupancyState.Occupied, LoadState.Empty);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var blocked = await CaptureRuleAsync(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "wrong-role-blocked", destination: destination)));
        Assert.NotNull(blocked);
        Assert.Equal("EMPTY_BUFFER_UNAVAILABLE", blocked!.Code);

        // Now configure a real EMPTY_STORAGE buffer — it must be the one used.
        var buffer = await CreateSlotAsync(context.LayoutId, "REAL-BUFFER", "EMPTY_STORAGE");
        await SetSlotAsync(buffer, OccupancyState.Occupied, LoadState.Empty);

        var result = await store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "wrong-role-then-real", destination: destination));

        Assert.Equal(buffer, result.Request.SourceSlotId);
    }

    [PostgresFact]
    public async Task Bring_empty_source_must_be_OCCUPIED_and_EMPTY()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var destination = await LogisticsSlotAsync(context.LayoutId);
        await SetSlotAsync(destination, OccupancyState.Vacant, LoadState.Unknown);
        var buffer = await CreateSlotAsync(context.LayoutId, "BUFFER-WRONG-STATE", "EMPTY_STORAGE");
        await SetSlotAsync(buffer, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var error = await CaptureRuleAsync(() =>
            store.CreateAndQueueAsync(Command(
                context,
                TransportTaskType.BringEmpty,
                "explicit-wrong-state",
                source: buffer,
                destination: destination)));

        Assert.NotNull(error);
        Assert.Equal("EMPTY_BUFFER_STATE_MISMATCH", error!.Code);
    }

    [PostgresFact]
    public async Task Remove_empty_ignores_a_same_state_slot_that_is_not_configured_as_EMPTY_STORAGE()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var source = await LogisticsSlotAsync(context.LayoutId);
        await SetSlotAsync(source, OccupancyState.Occupied, LoadState.Empty);
        var wrongRoleSlot = await CreateSlotAsync(context.LayoutId, "WRONG-ROLE-2", "STAGING");
        await SetSlotAsync(wrongRoleSlot, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var blocked = await CaptureRuleAsync(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.RemoveEmpty, "remove-wrong-role-blocked", source: source)));
        Assert.NotNull(blocked);
        Assert.Equal("EMPTY_BUFFER_UNAVAILABLE", blocked!.Code);

        var buffer = await CreateSlotAsync(context.LayoutId, "REAL-BUFFER-2", "EMPTY_STORAGE");
        await SetSlotAsync(buffer, OccupancyState.Vacant, LoadState.Unknown);

        var result = await store.CreateAndQueueAsync(Command(context, TransportTaskType.RemoveEmpty, "remove-wrong-role-then-real", source: source));

        Assert.Equal(buffer, result.Request.DestinationSlotId);
    }

    [PostgresFact]
    public async Task Remove_empty_destination_must_be_VACANT()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var source = await LogisticsSlotAsync(context.LayoutId);
        await SetSlotAsync(source, OccupancyState.Occupied, LoadState.Empty);
        var buffer = await CreateSlotAsync(context.LayoutId, "BUFFER-OCCUPIED", "EMPTY_STORAGE");
        await SetSlotAsync(buffer, OccupancyState.Occupied, LoadState.Empty);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var error = await CaptureRuleAsync(() =>
            store.CreateAndQueueAsync(Command(
                context,
                TransportTaskType.RemoveEmpty,
                "remove-dest-occupied",
                source: source,
                destination: buffer)));

        Assert.NotNull(error);
        Assert.Equal("EMPTY_BUFFER_STATE_MISMATCH", error!.Code);
    }

    [PostgresFact]
    public async Task Bring_empty_completion_updates_both_the_buffer_and_the_machine_slot()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var destination = await LogisticsSlotAsync(context.LayoutId);
        await SetSlotAsync(destination, OccupancyState.Vacant, LoadState.Unknown);
        var buffer = await CreateSlotAsync(context.LayoutId, "COMPLETION-BUFFER", "EMPTY_STORAGE");
        await SetSlotAsync(buffer, OccupancyState.Occupied, LoadState.Empty);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var service = new TransportWorkflowService(store);
        var created = await service.CreateAsync(Command(context, TransportTaskType.BringEmpty, "bring-completion", destination: destination));
        Assert.Equal(buffer, created.Request.SourceSlotId);

        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.SentToRds);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Running);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Completed);

        Assert.Equal((OccupancyState.Occupied, LoadState.Empty), await ReadSlotStateAsync(destination));
        Assert.Equal((OccupancyState.Vacant, LoadState.Unknown), await ReadSlotStateAsync(buffer));
    }

    [PostgresFact]
    public async Task Remove_empty_completion_updates_both_the_machine_and_the_buffer_slot()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var source = await LogisticsSlotAsync(context.LayoutId);
        await SetSlotAsync(source, OccupancyState.Occupied, LoadState.Empty);
        var buffer = await CreateSlotAsync(context.LayoutId, "COMPLETION-BUFFER-2", "EMPTY_STORAGE");
        await SetSlotAsync(buffer, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var service = new TransportWorkflowService(store);
        var created = await service.CreateAsync(Command(context, TransportTaskType.RemoveEmpty, "remove-completion", source: source));
        Assert.Equal(buffer, created.Request.DestinationSlotId);

        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.SentToRds);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Running);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Completed);

        Assert.Equal((OccupancyState.Vacant, LoadState.Unknown), await ReadSlotStateAsync(source));
        Assert.Equal((OccupancyState.Occupied, LoadState.Empty), await ReadSlotStateAsync(buffer));
    }

    [PostgresFact]
    public async Task Remove_full_never_uses_the_empty_container_buffer()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var source = await LogisticsSlotAsync(context.LayoutId);
        await SetSlotAsync(source, OccupancyState.Occupied, LoadState.Full);
        // Deliberately no EMPTY_STORAGE slot configured anywhere on this
        // layout — REMOVE_FULL must not need one.
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var result = await store.CreateAndQueueAsync(Command(context, TransportTaskType.RemoveFull, "remove-full-no-buffer", source: source));

        Assert.Equal(TransportRequestStatus.Queued, result.Request.Status);
        Assert.Null(result.Request.DestinationSlotId);
    }

    [PostgresFact]
    public async Task Missing_buffer_configuration_fails_cleanly_instead_of_guessing()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var destination = await LogisticsSlotAsync(context.LayoutId);
        await SetSlotAsync(destination, OccupancyState.Vacant, LoadState.Unknown);
        // No EMPTY_STORAGE slot exists on this layout at all.
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var error = await CaptureRuleAsync(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "no-buffer-configured", destination: destination)));

        Assert.NotNull(error);
        Assert.Equal("EMPTY_BUFFER_UNAVAILABLE", error!.Code);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [PostgresFact]
    public async Task Missing_worksite_mapping_on_the_allocated_buffer_is_reported_cleanly_at_dispatch_resolution()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var destination = await LogisticsSlotAsync(context.LayoutId);
        await SetSlotAsync(destination, OccupancyState.Vacant, LoadState.Unknown);
        var buffer = await CreateSlotAsync(context.LayoutId, "UNMAPPED-BUFFER", "EMPTY_STORAGE");
        await SetSlotAsync(buffer, OccupancyState.Occupied, LoadState.Empty);
        // No integration.layout_slot_rds_mapping row for the buffer slot —
        // execution-time resolution must reflect that cleanly, not guess a
        // WorkSite.
        await ExecuteAsync(
            """
            INSERT INTO master.task_templates (
                variant_id, task_type_id, rds_task_label, priority, config_json, is_active)
            SELECT @variant_id, id, 'EMPTY_BUFFER_TEST_TASK', 50, '{"testScope":"EMPTY_BUFFER"}'::jsonb, true
            FROM master.task_types WHERE code = 'BRING_EMPTY'
            """,
            ("variant_id", context.VariantId));
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var created = await store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "unmapped-buffer", destination: destination));
        Assert.Equal(buffer, created.Request.SourceSlotId);

        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        var executionStore = new PostgresRdsExecutionStore(dataSource);
        var dispatch = await executionStore.GetDispatchContextAsync(created.Request.Id);

        Assert.NotNull(dispatch);
        Assert.False(dispatch!.Source.IsMapped);
        Assert.Null(dispatch.Source.SiteId);
    }

    [PostgresFact]
    public async Task An_active_reservation_prevents_a_second_request_from_claiming_the_same_buffer_slot()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var destinations = await LogisticsSlotsAsync(context.LayoutId, 2);
        await SetSlotAsync(destinations[0], OccupancyState.Vacant, LoadState.Unknown);
        await SetSlotAsync(destinations[1], OccupancyState.Vacant, LoadState.Unknown);
        // Exactly one eligible buffer slot for two requests to compete over.
        var buffer = await CreateSlotAsync(context.LayoutId, "ONLY-BUFFER", "EMPTY_STORAGE");
        await SetSlotAsync(buffer, OccupancyState.Occupied, LoadState.Empty);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var first = await store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "buffer-race-a", destination: destinations[0]));
        Assert.Equal(buffer, first.Request.SourceSlotId);

        var second = await CaptureRuleAsync(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "buffer-race-b", destination: destinations[1])));

        Assert.NotNull(second);
        Assert.Equal("EMPTY_BUFFER_UNAVAILABLE", second!.Code);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [PostgresFact]
    public async Task Multiple_eligible_buffer_slots_resolve_deterministically_by_display_order_then_code()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var destination = await LogisticsSlotAsync(context.LayoutId);
        await SetSlotAsync(destination, OccupancyState.Vacant, LoadState.Unknown);
        // Configured out of natural insertion order — the lowest display_order
        // (then code) must win, not insertion order.
        var third = await CreateSlotAsync(context.LayoutId, "ORDER-C", "EMPTY_STORAGE", displayOrder: 30);
        var expectedFirst = await CreateSlotAsync(context.LayoutId, "ORDER-A", "EMPTY_STORAGE", displayOrder: 10);
        var second = await CreateSlotAsync(context.LayoutId, "ORDER-B", "EMPTY_STORAGE", displayOrder: 20);
        await SetSlotAsync(third, OccupancyState.Occupied, LoadState.Empty);
        await SetSlotAsync(expectedFirst, OccupancyState.Occupied, LoadState.Empty);
        await SetSlotAsync(second, OccupancyState.Occupied, LoadState.Empty);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var result = await store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "deterministic-order", destination: destination));

        Assert.Equal(expectedFirst, result.Request.SourceSlotId);
    }

    [PostgresFact]
    public async Task Existing_standalone_transport_flows_remain_valid()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slots = await LogisticsSlotsAsync(context.LayoutId, 2);
        await SetSlotAsync(slots[0], OccupancyState.Vacant, LoadState.Unknown);
        await SetSlotAsync(slots[1], OccupancyState.Occupied, LoadState.Full);
        var buffer = await CreateSlotAsync(context.LayoutId, "STANDALONE-BUFFER", "EMPTY_STORAGE");
        await SetSlotAsync(buffer, OccupancyState.Occupied, LoadState.Empty);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        // BRING_EMPTY with an explicit, correctly configured buffer source.
        var bringResult = await store.CreateAndQueueAsync(Command(
            context,
            TransportTaskType.BringEmpty,
            "standalone-bring",
            source: buffer,
            destination: slots[0]));
        Assert.Equal(TransportRequestStatus.Queued, bringResult.Request.Status);

        // REMOVE_FULL, entirely unaffected by the buffer feature.
        var removeFullResult = await store.CreateAndQueueAsync(Command(
            context,
            TransportTaskType.RemoveFull,
            "standalone-remove-full",
            source: slots[1]));
        Assert.Equal(TransportRequestStatus.Queued, removeFullResult.Request.Status);
    }

    private async Task ResetAsync()
    {
        await ExecuteAsync(
            """
            TRUNCATE integration.rds_tasks, ops.transport_request_events, ops.transport_requests, ops.slot_runtime_state, ops.machine_runtime_state RESTART IDENTITY CASCADE;
            DELETE FROM integration.layout_slot_rds_mapping
            WHERE rds_worksite_id IN (SELECT id FROM integration.rds_worksites WHERE rds_site_id LIKE 'EMPTYBUF-%');
            DELETE FROM integration.rds_worksites WHERE rds_site_id LIKE 'EMPTYBUF-%';
            DELETE FROM master.task_templates WHERE config_json ->> 'testScope' = 'EMPTY_BUFFER';
            DELETE FROM master.layout_slots WHERE metadata_json ->> 'testScope' = 'EMPTY_BUFFER';
            INSERT INTO ops.store_units (store_location_id, load_state)
            SELECT location.id, 'EMPTY'
            FROM ops.store_locations location
            WHERE location.code LIKE 'PHASE03-EMPTY-%'
              AND NOT EXISTS (
                  SELECT 1 FROM ops.store_units unit
                  WHERE unit.store_location_id = location.id AND unit.removed_at IS NULL);
            """);
    }

    private async Task<LayoutContext> ConfigureAsync(string machineCode, string variantCode)
    {
        var context = await ContextAsync(machineCode, variantCode);
        await ExecuteAsync(
            """
            INSERT INTO ops.machine_runtime_state (machine_id, active_variant_id, active_layout_id, active_navigation_id)
            VALUES (@machine_id, @variant_id, @layout_id, NULL)
            ON CONFLICT (machine_id) DO UPDATE
            SET active_variant_id = EXCLUDED.active_variant_id,
                active_layout_id = EXCLUDED.active_layout_id,
                active_navigation_id = NULL,
                row_version = ops.machine_runtime_state.row_version + 1,
                updated_at = now()
            """,
            ("machine_id", context.MachineId),
            ("variant_id", context.VariantId),
            ("layout_id", context.LayoutId));
        return context;
    }

    private async Task<LayoutContext> ContextAsync(string machineCode, string variantCode)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT m.id, v.id, l.id
            FROM master.machines m
            JOIN master.variants v ON v.machine_id = m.id AND v.code = @variant_code
            JOIN master.variant_layout_assignments a ON a.variant_id = v.id AND a.machine_id = m.id
            JOIN master.layouts l ON l.id = a.layout_id AND l.machine_id = m.id
            WHERE m.code = @machine_code AND a.is_default
            """,
            connection);
        command.Parameters.AddWithValue("machine_code", machineCode);
        command.Parameters.AddWithValue("variant_code", variantCode);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Missing layout context for {machineCode} V{variantCode}.");
        return new LayoutContext(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2));
    }

    private async Task<Guid> LogisticsSlotAsync(Guid layoutId) =>
        await ScalarAsync<Guid>(
            "SELECT id FROM master.layout_slots WHERE layout_id = @layout_id AND slot_role <> 'MACHINE' ORDER BY display_order, code LIMIT 1",
            ("layout_id", layoutId));

    private async Task<Guid[]> LogisticsSlotsAsync(Guid layoutId, int count)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id FROM master.layout_slots WHERE layout_id = @layout_id AND slot_role <> 'MACHINE' ORDER BY display_order, code LIMIT @count",
            connection);
        command.Parameters.AddWithValue("layout_id", layoutId);
        command.Parameters.AddWithValue("count", count);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<Guid>();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetGuid(0));
        }

        Assert.Equal(count, result.Count);
        return [.. result];
    }

    /// <summary>
    /// Creates a dedicated, uniquely-coded test slot (tagged so ResetAsync
    /// can clean it up), rather than reusing the layout's seeded logistics
    /// slots — this file needs precise, per-test control over exactly which
    /// EMPTY_STORAGE slots exist.
    /// </summary>
    private async Task<Guid> CreateSlotAsync(Guid layoutId, string codeSuffix, string slotRole, int displayOrder = 9500) =>
        await ScalarAsync<Guid>(
            """
            INSERT INTO master.layout_slots (layout_id, code, display_name, slot_role, display_order, metadata_json)
            VALUES (@layout_id, @code, @display_name, @slot_role, @display_order, '{"testScope":"EMPTY_BUFFER"}'::jsonb)
            RETURNING id
            """,
            ("layout_id", layoutId),
            ("code", $"EMPTYBUF-{codeSuffix}-{Guid.NewGuid():N}"),
            ("display_name", $"Empty Buffer Test {codeSuffix}"),
            ("slot_role", slotRole),
            ("display_order", displayOrder));

    private Task SetSlotAsync(Guid slotId, OccupancyState occupancy, LoadState load) => ExecuteAsync(
        """
        INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state)
        VALUES (@slot_id, @occupancy, @load)
        ON CONFLICT (layout_slot_id) DO UPDATE
        SET occupancy_state = EXCLUDED.occupancy_state,
            load_state = EXCLUDED.load_state,
            row_version = ops.slot_runtime_state.row_version + 1,
            updated_at = now()
        """,
        ("slot_id", slotId),
        ("occupancy", occupancy.ToDatabase()),
        ("load", load.ToDatabase()));

    private async Task<(OccupancyState Occupancy, LoadState Load)> ReadSlotStateAsync(Guid slotId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT occupancy_state, load_state FROM ops.slot_runtime_state WHERE layout_slot_id = @id",
            connection);
        command.Parameters.AddWithValue("id", slotId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (WorkflowCode.ParseOccupancy(reader.GetString(0)), WorkflowCode.ParseLoad(reader.GetString(1)));
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

    private static async Task<WorkflowRuleException?> CaptureRuleAsync(Func<Task<CreateTransportResult>> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (WorkflowRuleException error)
        {
            return error;
        }
    }

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        var value = await command.ExecuteScalarAsync();
        return value is T typed ? typed : throw new InvalidOperationException($"Scalar query did not return {typeof(T).Name}.");
    }

    private static void AddParameters(NpgsqlCommand command, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private sealed record LayoutContext(Guid MachineId, Guid VariantId, Guid LayoutId);
}
