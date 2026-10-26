using Npgsql;
using TkgPda.Application;
using TkgPda.Domain;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Infrastructure.Postgres.Tests;

public sealed class PostgresTransportReadinessTests
{
    private readonly string _connectionString = Environment.GetEnvironmentVariable("TKG_PDA_TEST_CONNECTION_STRING") ?? string.Empty;

    [PostgresFact]
    public async Task Requires_preparation_omitted_or_false_creates_a_not_required_request()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slots = await LogisticsSlotsAsync(context.LayoutId, 2);
        await SetSlotAsync(slots[0], OccupancyState.Vacant, LoadState.Unknown);
        await SetSlotAsync(slots[1], OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var omitted = await store.CreateAndQueueAsync(Command(context, "readiness-omitted", slots[0]));
        var explicitFalse = await store.CreateAndQueueAsync(Command(context, "readiness-false", slots[1], requiresPreparation: false));

        Assert.Equal(TransportReadinessState.NotRequired, omitted.Request.ReadinessState);
        Assert.Null(omitted.Request.ReadyAt);
        Assert.Equal(TransportReadinessState.NotRequired, explicitFalse.Request.ReadinessState);
        Assert.Null(explicitFalse.Request.ReadyAt);
    }

    [PostgresFact]
    public async Task Requires_preparation_true_creates_a_waiting_preparation_request()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var created = await store.CreateAndQueueAsync(Command(context, "readiness-true", slotId, requiresPreparation: true));

        Assert.Equal(TransportReadinessState.WaitingPreparation, created.Request.ReadinessState);
        Assert.Null(created.Request.ReadyAt);
    }

    [PostgresFact]
    public async Task Waiting_preparation_requests_are_excluded_from_the_dispatch_queue()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slots = await LogisticsSlotsAsync(context.LayoutId, 2);
        await SetSlotAsync(slots[0], OccupancyState.Vacant, LoadState.Unknown);
        await SetSlotAsync(slots[1], OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var notRequired = await store.CreateAndQueueAsync(Command(context, "dispatch-not-required", slots[0]));
        var waiting = await store.CreateAndQueueAsync(Command(context, "dispatch-waiting", slots[1], requiresPreparation: true));
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        var executionStore = new PostgresRdsExecutionStore(dataSource);

        var queued = await executionStore.ListQueuedRequestIdsAsync(100);

        Assert.Contains(notRequired.Request.Id, queued);
        Assert.DoesNotContain(waiting.Request.Id, queued);
    }

    [PostgresFact]
    public async Task A_higher_precedence_waiting_preparation_request_does_not_block_a_lower_precedence_eligible_request()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slots = await LogisticsSlotsAsync(context.LayoutId, 2);
        await SetSlotAsync(slots[0], OccupancyState.Vacant, LoadState.Unknown);
        await SetSlotAsync(slots[1], OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        // A: numerically lower priority = higher dispatch precedence, but still WAITING_PREPARATION.
        var waitingHighPrecedence = await store.CreateAndQueueAsync(Command(context, "sched-a-waiting", slots[0], requiresPreparation: true));
        await ExecuteAsync("UPDATE ops.transport_requests SET priority = 1 WHERE id = @id", ("id", waitingHighPrecedence.Request.Id));
        // B: lower dispatch precedence, but eligible (NOT_REQUIRED).
        var eligibleLowPrecedence = await store.CreateAndQueueAsync(Command(context, "sched-b-eligible", slots[1]));
        await ExecuteAsync("UPDATE ops.transport_requests SET priority = 50 WHERE id = @id", ("id", eligibleLowPrecedence.Request.Id));
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        var executionStore = new PostgresRdsExecutionStore(dataSource);

        var queued = await executionStore.ListQueuedRequestIdsAsync(100);

        Assert.DoesNotContain(waitingHighPrecedence.Request.Id, queued);
        Assert.Contains(eligibleLowPrecedence.Request.Id, queued);
    }

    [PostgresFact]
    public async Task Eligible_requests_preserve_priority_ordering_then_fifo_ordering()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slots = await LogisticsSlotsAsync(context.LayoutId, 4);
        foreach (var slot in slots)
        {
            await SetSlotAsync(slot, OccupancyState.Vacant, LoadState.Unknown);
        }
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        // Same priority, inserted in this order -> FIFO (queued_at) must preserve insertion order.
        var fifoFirst = await store.CreateAndQueueAsync(Command(context, "sched-fifo-first", slots[0]));
        var fifoSecond = await store.CreateAndQueueAsync(Command(context, "sched-fifo-second", slots[1]));
        await ExecuteAsync("UPDATE ops.transport_requests SET priority = 50, queued_at = now() WHERE id = @id", ("id", fifoFirst.Request.Id));
        await ExecuteAsync("UPDATE ops.transport_requests SET priority = 50, queued_at = now() + interval '1 second' WHERE id = @id", ("id", fifoSecond.Request.Id));

        // Higher precedence (lower numeric priority) must sort before both, regardless of queued_at.
        var highPrecedence = await store.CreateAndQueueAsync(Command(context, "sched-high-precedence", slots[2]));
        await ExecuteAsync("UPDATE ops.transport_requests SET priority = 1, queued_at = now() + interval '2 seconds' WHERE id = @id", ("id", highPrecedence.Request.Id));

        // A WAITING_PREPARATION request at the numerically lowest priority must still never appear.
        var waitingTop = await store.CreateAndQueueAsync(Command(context, "sched-waiting-top", slots[3], requiresPreparation: true));
        await ExecuteAsync("UPDATE ops.transport_requests SET priority = 0 WHERE id = @id", ("id", waitingTop.Request.Id));

        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        var executionStore = new PostgresRdsExecutionStore(dataSource);

        var queued = await executionStore.ListQueuedRequestIdsAsync(100);

        Assert.Equal([highPrecedence.Request.Id, fifoFirst.Request.Id, fifoSecond.Request.Id], queued);
        Assert.DoesNotContain(waitingTop.Request.Id, queued);
    }

    [PostgresFact]
    public async Task Mark_ready_transitions_waiting_preparation_to_ready_and_sets_ready_at_once()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var created = await store.CreateAndQueueAsync(Command(context, "mark-ready", slotId, requiresPreparation: true));
        await InsertReadyEmptyPreparationPalletAsync(created.Request.Id);

        var firstMarkReady = await store.MarkReadyAsync(created.Request.Id, actorUserId: null);
        var repeated = await store.MarkReadyAsync(created.Request.Id, actorUserId: null);

        Assert.Equal(TransportReadinessState.Ready, firstMarkReady.ReadinessState);
        Assert.NotNull(firstMarkReady.ReadyAt);
        Assert.Equal(TransportReadinessState.Ready, repeated.ReadinessState);
        // The no-op replay returns the row as read back from Postgres (microsecond
        // precision), while the initial transition returns the in-memory
        // DateTimeOffset.UtcNow (100ns ticks) it just wrote — compare with a
        // tolerance rather than bit-for-bit equality.
        Assert.True(
            (firstMarkReady.ReadyAt!.Value - repeated.ReadyAt!.Value).Duration() < TimeSpan.FromMilliseconds(1),
            $"Expected ready_at to stay stable across repeated mark-ready calls: {firstMarkReady.ReadyAt:O} vs {repeated.ReadyAt:O}.");
        Assert.Equal(
            1,
            await ScalarAsync<long>(
                "SELECT count(*) FROM ops.transport_request_events WHERE transport_request_id = @id AND event_type = 'WAREHOUSE_PREPARATION_READY'",
                ("id", created.Request.Id)));
    }

    [PostgresFact]
    public async Task Not_required_requests_still_follow_the_existing_workflow_and_reject_mark_ready()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var service = new TransportWorkflowService(store);
        var created = await service.CreateAsync(Command(context, "not-required-lifecycle", slotId));

        var rejected = await CaptureRuleAsync(() => store.MarkReadyAsync(created.Request.Id, actorUserId: null));
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.SentToRds);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Running);
        var completed = await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Completed);

        Assert.NotNull(rejected);
        Assert.Equal("READINESS_NOT_APPLICABLE", rejected!.Code);
        Assert.Equal(TransportRequestStatus.Completed, completed.Status);
        Assert.Equal(TransportReadinessState.NotRequired, completed.ReadinessState);
        Assert.Equal((OccupancyState.Occupied, LoadState.Empty), await ReadSlotStateAsync(slotId));
    }

    [PostgresFact]
    public async Task A_terminal_request_cannot_be_marked_ready()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var created = await store.CreateAndQueueAsync(Command(context, "terminal-readiness", slotId, requiresPreparation: true));
        var cancelled = await store.CancelAsync(created.Request.Id, actorUserId: null);

        var rejected = await CaptureRuleAsync(() => store.MarkReadyAsync(created.Request.Id, actorUserId: null));

        Assert.Equal(TransportRequestStatus.Cancelled, cancelled.Request.Status);
        Assert.NotNull(rejected);
        Assert.Equal("REQUEST_TERMINAL", rejected!.Code);
    }

    private async Task ResetAsync()
    {
        await ExecuteAsync(
            """
            TRUNCATE integration.rds_tasks, ops.transport_request_events, ops.transport_requests, ops.slot_runtime_state, ops.machine_runtime_state RESTART IDENTITY CASCADE;
            INSERT INTO ops.store_units (store_location_id, load_state)
            SELECT location.id, 'EMPTY'
            FROM ops.store_locations location
            WHERE location.code LIKE 'PHASE03-EMPTY-%'
              AND NOT EXISTS (
                  SELECT 1 FROM ops.store_units unit
                  WHERE unit.store_location_id = location.id AND unit.removed_at IS NULL);
            """);
    }

    private Task InsertReadyEmptyPreparationPalletAsync(Guid requestId) => ExecuteAsync(
        """
        WITH actor AS (
            INSERT INTO auth.users (username, password_hash, full_name, is_active)
            VALUES ('phase03-readiness-operator', 'test-only-hash', 'Phase 03 Readiness Operator', true)
            ON CONFLICT (username) DO UPDATE SET is_active = true
            RETURNING id
        ), device AS (
            INSERT INTO audit.client_devices (device_code, name, device_type, is_active)
            VALUES ('PHASE03-READINESS', 'Phase 03 Readiness Device', 'PDA', true)
            ON CONFLICT (device_code) DO UPDATE SET is_active = true
            RETURNING id
        )
        INSERT INTO ops.preparation_pallets (
            preparation_area_id, container_type_id, load_state, transport_request_id,
            placed_by, placed_device_id, placed_at, ready_at, ready_by)
        SELECT area.id, container.id, 'EMPTY', @request_id,
               actor.id, device.id, stamp.value, stamp.value, actor.id
        FROM ops.preparation_areas area
        CROSS JOIN LATERAL (
            SELECT id FROM master.container_types WHERE is_active ORDER BY code LIMIT 1
        ) container
        CROSS JOIN actor
        CROSS JOIN device
        CROSS JOIN LATERAL (SELECT clock_timestamp() AS value) stamp
        WHERE area.warehouse_scope = 'FINISHED_GOODS'
        ORDER BY area.area_number
        LIMIT 1
        """,
        ("request_id", requestId));

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
        await EnsureDefaultBufferSlotsAsync(context.LayoutId);
        return context;
    }

    /// <summary>
    /// Customer Workflow — Empty Container Buffer: every BRING_EMPTY request
    /// in this file omits an explicit source, so it now needs a configured
    /// EMPTY_STORAGE buffer slot ready to bring (OCCUPIED/EMPTY) — see
    /// PostgresTransportWorkflowStore.AllocateBufferSlotAsync. A small pool
    /// (rather than one) lets this file's multi-request tests each get their
    /// own; a high display_order keeps them out of LogisticsSlotsAsync's
    /// ordered picks.
    /// </summary>
    private async Task EnsureDefaultBufferSlotsAsync(Guid layoutId)
    {
        const int poolSize = 6;
        for (var i = 1; i <= poolSize; i++)
        {
            var code = $"BUFFER-TEST-BRING-{i}";
            await ExecuteAsync(
                """
                INSERT INTO master.layout_slots (layout_id, code, display_name, slot_role, display_order)
                SELECT @layout_id, @code, @display_name, 'EMPTY_STORAGE', 9000
                WHERE NOT EXISTS (
                    SELECT 1 FROM master.layout_slots WHERE layout_id = @layout_id AND code = @code
                )
                """,
                ("layout_id", layoutId),
                ("code", code),
                ("display_name", $"Test Buffer Bring {i}"));
            var bufferSlotId = await ScalarAsync<Guid>(
                "SELECT id FROM master.layout_slots WHERE layout_id = @layout_id AND code = @code",
                ("layout_id", layoutId),
                ("code", code));
            await ExecuteAsync(
                """
                INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state)
                VALUES (@slot_id, 'OCCUPIED', 'EMPTY')
                ON CONFLICT (layout_slot_id) DO UPDATE
                SET occupancy_state = 'OCCUPIED', load_state = 'EMPTY',
                    row_version = ops.slot_runtime_state.row_version + 1, updated_at = now()
                """,
                ("slot_id", bufferSlotId));
        }
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

    private async Task<Guid> LogisticsSlotAsync(Guid layoutId, bool requireContainer)
    {
        var condition = requireContainer ? "AND default_container_type_id IS NOT NULL" : string.Empty;
        return await ScalarAsync<Guid>(
            $"SELECT id FROM master.layout_slots WHERE layout_id = @layout_id AND slot_role <> 'MACHINE' {condition} ORDER BY display_order, code LIMIT 1",
            ("layout_id", layoutId));
    }

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
        string key,
        Guid destination,
        bool requiresPreparation = false) => new(
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
        TransportTaskType.BringEmpty,
        null,
        destination,
        RequiresPreparation: requiresPreparation);

    private static async Task<WorkflowRuleException?> CaptureRuleAsync(Func<Task> action)
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
