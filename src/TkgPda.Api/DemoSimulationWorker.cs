using Npgsql;
using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Api;

public sealed class DemoSimulationWorker(
    NpgsqlDataSource dataSource,
    ITransportWorkflowStore workflowStore,
    DemoCancellationRecovery cancellationRecovery,
    DemoModeOptions options,
    TimeProvider timeProvider,
    ILogger<DemoSimulationWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        logger.LogInformation(
            "Demo simulation worker started. Delay: {Delay}s, RunningDuration: {RunDuration}s, Robot: {AgvId}, AutoPrepare: {AutoPrepare}.",
            options.SimulationDelaySeconds,
            options.RunningDurationSeconds,
            options.SimulatedAgvId,
            options.AutoPrepareWarehouseDemands);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await gate.WaitAsync(0, stoppingToken))
            {
                try
                {
                    await ProcessActiveDemandsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    try
                    {
                        logger.LogWarning(error, "Demo simulation worker cycle encountered an error: {Message}", error.Message);
                    }
                    catch
                    {
                        // Logging failure must never tear down the simulation worker
                    }
                }
                finally
                {
                    gate.Release();
                }
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }

    private async Task ProcessActiveDemandsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var minAge = TimeSpan.FromSeconds(options.SimulationDelaySeconds);

        // A simulated cancellation proves that no pallet moved. Close any
        // reconciliation hold left by older builds before allocating the next
        // waiting request, so a cancelled demo task cannot strand an area.
        await cancellationRecovery.ReconcileAsync(cancellationToken: cancellationToken);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // A newly selected demo production context must not inherit stale
        // occupancy from the last time that variant-specific layout was used.
        // Only positions that have not physically started work in the current
        // session and are not owned by any active request are reset.
        await ResetFreshProductionSlotsAsync(connection, cancellationToken);

        // In demo simulation mode, keep empty storage buffers replenished so repeated demo flows succeed
        await ReplenishEmptyBuffersAsync(connection, cancellationToken);

        // Customer layouts can assign more than one alt part to one physical
        // position. In a real cell the line reports the delivered pallet EMPTY
        // after consuming it. Demo mode has no PLC, so model that consumption
        // after the configured delay when another prepared pallet is waiting.
        await SimulateConsumedSharedSlotPalletsAsync(
            connection,
            now - minAge,
            cancellationToken);

        await workflowStore.ActivatePreparedPartDemandsAsync(
            100,
            OperationalEventContext.System(Guid.NewGuid()),
            cancellationToken);

        var candidates = await ListCandidatesAsync(
            connection,
            options.AutoPrepareWarehouseDemands,
            cancellationToken);

        foreach (var item in candidates)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                await AdvanceCandidateAsync(connection, item, now, minAge, cancellationToken);
            }
            catch (WorkflowRuleException ruleError)
            {
                logger.LogInformation("Demo simulation skipped request {RequestNo} due to workflow rule: {Code} ({Message})",
                    item.RequestNo, ruleError.Code, ruleError.Message);
            }
            catch (Exception itemError)
            {
                logger.LogWarning(itemError, "Failed advancing demo request {RequestNo}: {Message}", item.RequestNo, itemError.Message);
            }
        }
    }

    internal static async Task<IReadOnlyList<DemoCandidateRequest>> ListCandidatesAsync(
        NpgsqlConnection connection,
        bool autoPrepareWarehouseDemands,
        CancellationToken cancellationToken)
    {
        // One configured simulated AGV may own only one newly dispatched task.
        // Existing in-flight rows are advanced first (including legacy data
        // that accidentally contains more than one); no new request is sent
        // until that set becomes terminal.
        var active = await ReadCandidatesAsync(
            connection,
            """
            r.status IN ('SENT_TO_RDS', 'RUNNING')
            ORDER BY r.created_at, r.id
            LIMIT 100
            """,
            cancellationToken);
        if (active.Count > 0)
        {
            return active;
        }

        // Match the real execution queue: warehouse preparation must be real,
        // the machine reservation must be active and every predecessor must
        // have completed. Selecting one row prevents simultaneous dispatches
        // to DemoMode's single SimulatedAgvId.
        var queued = await ReadCandidatesAsync(
            connection,
            """
            r.status = 'QUEUED'
              AND r.readiness_state <> 'WAITING_PREPARATION'
              AND r.machine_slot_reservation_active
              AND r.store_endpoint_reservation_active
              AND NOT EXISTS (
                  SELECT 1
                  FROM ops.transport_request_dependencies dependency
                  JOIN ops.transport_requests predecessor
                    ON predecessor.id = dependency.predecessor_request_id
                  WHERE dependency.successor_request_id = r.id
                    AND predecessor.status <> 'COMPLETED')
            ORDER BY r.priority, r.queued_at, r.id
            LIMIT 1
            """,
            cancellationToken);
        if (queued.Count > 0 || !autoPrepareWarehouseDemands)
        {
            return queued;
        }

        // Compatibility-only developer mode. Installed demo builds keep this
        // disabled because customer warehouse confirmation is a real workflow.
        return await ReadCandidatesAsync(
            connection,
            """
            r.status = 'QUEUED'
              AND r.readiness_state = 'WAITING_PREPARATION'
            ORDER BY r.created_at, r.id
            LIMIT 1
            """,
            cancellationToken);
    }

    private static async Task<List<DemoCandidateRequest>> ReadCandidatesAsync(
        NpgsqlConnection connection,
        string predicateAndOrder,
        CancellationToken cancellationToken)
    {
        var candidates = new List<DemoCandidateRequest>();
        await using var command = new NpgsqlCommand(
            $"""
             SELECT r.id, r.request_no, tt.code AS task_type, r.status, r.readiness_state,
                    r.created_at, r.ready_at,
                    t.rds_status, t.sent_at, t.started_at
             FROM ops.transport_requests r
             JOIN master.task_types tt ON tt.id = r.task_type_id
             LEFT JOIN integration.rds_tasks t ON t.transport_request_id = r.id
             WHERE {predicateAndOrder}
             """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new DemoCandidateRequest(
                reader.GetGuid(0),
                reader.GetInt64(1).ToString(),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9)));
        }

        return candidates;
    }

    private async Task AdvanceCandidateAsync(
        NpgsqlConnection connection,
        DemoCandidateRequest req,
        DateTimeOffset now,
        TimeSpan minAge,
        CancellationToken cancellationToken)
    {
        // 1. Warehouse preparation phase
        if (req.ReadinessState == "WAITING_PREPARATION")
        {
            if (!options.AutoPrepareWarehouseDemands) return;
            if (now - req.CreatedAt < minAge) return;

            await using var prepCmd = new NpgsqlCommand(
                """
                UPDATE ops.transport_requests
                SET readiness_state = 'READY',
                    ready_at = @ready_at,
                    status = 'QUEUED',
                    updated_at = now()
                WHERE id = @id
                  AND status = 'QUEUED'
                  AND readiness_state = 'WAITING_PREPARATION';
                """, connection);
            prepCmd.Parameters.AddWithValue("id", req.Id);
            prepCmd.Parameters.AddWithValue("ready_at", now);
            await prepCmd.ExecuteNonQueryAsync(cancellationToken);

            logger.LogInformation("Demo simulation: Auto-prepared warehouse demand for request {RequestNo}.", req.RequestNo);
            return;
        }

        // 2. Queued -> SentToRds
        if (req.Status == "QUEUED")
        {
            var effectiveStart = req.ReadyAt ?? req.CreatedAt;
            if (now - effectiveStart < minAge) return;

            var taskRecordId = $"DEMO-{req.RequestNo}";
            await using var upsertTaskCmd = new NpgsqlCommand(
                """
                INSERT INTO integration.rds_tasks (
                    transport_request_id, rds_task_record_id, rds_task_label, agv_id,
                    rds_status, sent_at, last_sync_at, last_status_json
                ) VALUES (
                    @id, @record_id, 'DEMO_TASK', @agv_id,
                    1001, @now, @now, '{"state":"SENT","simulated":true}'::jsonb
                )
                ON CONFLICT (transport_request_id) DO UPDATE SET
                    rds_status = 1001,
                    agv_id = @agv_id,
                    sent_at = COALESCE(integration.rds_tasks.sent_at, @now),
                    last_sync_at = @now,
                    last_status_json = '{"state":"SENT","simulated":true}'::jsonb,
                    updated_at = now();
                """, connection);
            upsertTaskCmd.Parameters.AddWithValue("id", req.Id);
            upsertTaskCmd.Parameters.AddWithValue("record_id", taskRecordId);
            upsertTaskCmd.Parameters.AddWithValue("agv_id", options.SimulatedAgvId);
            upsertTaskCmd.Parameters.AddWithValue("now", now);
            await upsertTaskCmd.ExecuteNonQueryAsync(cancellationToken);

            await workflowStore.RecordExecutionTransitionAsync(req.Id, TransportRequestStatus.SentToRds, OperationalEventContext.System(Guid.NewGuid()), cancellationToken);
            logger.LogInformation("Demo simulation: Dispatched request {RequestNo} to simulated robot {AgvId}.", req.RequestNo, options.SimulatedAgvId);
            return;
        }

        // 3. SentToRds -> Running
        if (req.Status == "SENT_TO_RDS")
        {
            var sentAt = req.SentAt ?? req.CreatedAt;
            var sentDelay = TimeSpan.FromSeconds(Math.Max(3, options.SimulationDelaySeconds));
            if (now - sentAt < sentDelay) return;

            var runSeconds = options.RunningDurationSeconds;
            await using var updateRunCmd = new NpgsqlCommand(
                """
                UPDATE integration.rds_tasks
                SET rds_status = 1002,
                    started_at = COALESCE(started_at, @now),
                    last_sync_at = @now,
                    last_status_json = jsonb_build_object('remaining_time', @seconds, 'state', 'RUNNING', 'simulated', true),
                    updated_at = now()
                WHERE transport_request_id = @id;
                """, connection);
            updateRunCmd.Parameters.AddWithValue("id", req.Id);
            updateRunCmd.Parameters.AddWithValue("now", now);
            updateRunCmd.Parameters.AddWithValue("seconds", runSeconds);
            await updateRunCmd.ExecuteNonQueryAsync(cancellationToken);

            await workflowStore.RecordExecutionTransitionAsync(req.Id, TransportRequestStatus.Running, OperationalEventContext.System(Guid.NewGuid()), cancellationToken);
            logger.LogInformation("Demo simulation: Request {RequestNo} is RUNNING by robot {AgvId} (ETA {Seconds}s).", req.RequestNo, options.SimulatedAgvId, runSeconds);
            return;
        }

        // 4. Running -> Progress Countdown or Completed
        if (req.Status == "RUNNING")
        {
            var startedAt = req.StartedAt ?? req.CreatedAt;
            var elapsed = now - startedAt;
            var totalRunDuration = TimeSpan.FromSeconds(options.RunningDurationSeconds);

            if (elapsed < totalRunDuration)
            {
                var remaining = (int)Math.Max(1, Math.Ceiling((totalRunDuration - elapsed).TotalSeconds));
                await using var updateCountdownCmd = new NpgsqlCommand(
                    """
                    UPDATE integration.rds_tasks
                    SET last_sync_at = @now,
                        last_status_json = jsonb_build_object('remaining_time', @remaining, 'state', 'RUNNING', 'simulated', true)
                    WHERE transport_request_id = @id;
                    """, connection);
                updateCountdownCmd.Parameters.AddWithValue("id", req.Id);
                updateCountdownCmd.Parameters.AddWithValue("now", now);
                updateCountdownCmd.Parameters.AddWithValue("remaining", remaining);
                await updateCountdownCmd.ExecuteNonQueryAsync(cancellationToken);
                return;
            }

            // Task execution completed
            await using var updateCompleteCmd = new NpgsqlCommand(
                """
                UPDATE integration.rds_tasks
                SET rds_status = 1003,
                    ended_at = COALESCE(ended_at, @now),
                    last_sync_at = @now,
                    last_status_json = '{"remaining_time": 0, "state": "COMPLETED", "simulated": true}'::jsonb,
                    updated_at = now()
                WHERE transport_request_id = @id;
                """, connection);
            updateCompleteCmd.Parameters.AddWithValue("id", req.Id);
            updateCompleteCmd.Parameters.AddWithValue("now", now);
            await updateCompleteCmd.ExecuteNonQueryAsync(cancellationToken);

            await workflowStore.RecordExecutionTransitionAsync(req.Id, TransportRequestStatus.Completed, OperationalEventContext.System(Guid.NewGuid()), cancellationToken);

            // Release any warehouse preparation pallet linked to this request so preparation areas become VACANT ("BOŞ")
            await ReleasePreparationPalletIfLinkedAsync(connection, req.Id, now, cancellationToken);

            // If this was REMOVE_FULL, ensure finished goods store received a delivered unit so FG location becomes OCCUPIED ("DOLU MAMUL")
            if (req.TaskType == "REMOVE_FULL")
            {
                await EnsureFinishedGoodsDeliveryAsync(connection, req.Id, now, cancellationToken);
            }

            // If this was BRING_EMPTY, ensure finished goods store vacates an empty container unit so FG location becomes VACANT ("ALAN BOŞ")
            if (req.TaskType == "BRING_EMPTY")
            {
                await EnsureFinishedGoodsEmptyCollectionAsync(connection, req.Id, now, cancellationToken);
            }

            logger.LogInformation("Demo simulation: Request {RequestNo} COMPLETED successfully. Warehouse states and slot states updated.", req.RequestNo);
        }
    }

    private static async Task ReleasePreparationPalletIfLinkedAsync(
        NpgsqlConnection connection,
        Guid requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var releaseCmd = new NpgsqlCommand(
            """
            WITH released AS (
                UPDATE ops.preparation_pallets
                SET released_at = @now,
                    released_by = COALESCE(ready_by, placed_by),
                    released_reason = CASE WHEN ready_at IS NOT NULL THEN 'COLLECTED' ELSE 'DISPATCH_BYPASS' END,
                    row_version = row_version + 1
                WHERE transport_request_id = @id AND released_at IS NULL
                RETURNING id, preparation_area_id, part_id, container_type_id, transport_request_id, ready_by, placed_by, placed_device_id
            )
            INSERT INTO ops.preparation_events (
                preparation_area_id, preparation_pallet_id, event_type, part_id, container_type_id,
                transport_request_id, actor_user_id, device_id, occurred_at, data_json
            )
            SELECT r.preparation_area_id, r.id, 'PALLET_COLLECTED', r.part_id, r.container_type_id,
                   r.transport_request_id, COALESCE(r.ready_by, r.placed_by), r.placed_device_id, @now, '{"simulated": true}'::jsonb
            FROM released r;
            """, connection);
        releaseCmd.Parameters.AddWithValue("id", requestId);
        releaseCmd.Parameters.AddWithValue("now", now);
        await releaseCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureFinishedGoodsDeliveryAsync(
        NpgsqlConnection connection,
        Guid requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var deliverCmd = new NpgsqlCommand(
            """
            WITH already_delivered AS (
                SELECT se.store_unit_id AS unit_id, se.store_location_id
                FROM ops.store_events se
                WHERE se.transport_request_id = @id AND se.event_type = 'UNIT_DELIVERED'
                LIMIT 1
            ),
            candidate AS (
                SELECT sl.id AS location_id
                FROM ops.transport_requests r
                JOIN master.product_groups pg
                  ON pg.id = r.product_group_id
                 AND pg.main_part_id = r.part_id
                CROSS JOIN LATERAL (
                    SELECT sl.id
                    FROM ops.store_locations sl
                    WHERE sl.is_enabled
                      AND NOT EXISTS (
                          SELECT 1 FROM ops.store_units u
                          WHERE u.store_location_id = sl.id AND u.removed_at IS NULL
                      )
                      AND (
                          (r.destination_business_code IS NOT NULL AND sl.code = r.destination_business_code)
                          OR (r.destination_business_code IS NULL)
                      )
                    ORDER BY sl.code ASC
                    LIMIT 1
                ) sl
                WHERE r.id = @id
                  AND NOT EXISTS (SELECT 1 FROM already_delivered)
                  AND NOT EXISTS (
                      SELECT 1 FROM ops.store_units existing 
                      WHERE existing.transport_request_id = r.id AND existing.removed_at IS NULL
                  )
            ),
            inserted_unit AS (
                INSERT INTO ops.store_units (
                    store_location_id, transport_request_id, part_id, container_type_id, machine_id, delivered_at, load_state
                )
                SELECT c.location_id, r.id, r.part_id, r.container_type_id, r.machine_id, @now, 'FULL'
                FROM candidate c
                JOIN ops.transport_requests r ON r.id = @id
                ON CONFLICT (transport_request_id) WHERE transport_request_id IS NOT NULL DO NOTHING
                RETURNING id, store_location_id, part_id, container_type_id, transport_request_id
            ),
            inserted_event AS (
                INSERT INTO ops.store_events (
                    store_location_id, store_unit_id, event_type, part_id, container_type_id,
                    transport_request_id, occurred_at, data_json
                )
                SELECT u.store_location_id, u.id, 'UNIT_DELIVERED', u.part_id, u.container_type_id,
                       u.transport_request_id, @now, '{"source": "SIMULATION_COMPLETED"}'::jsonb
                FROM inserted_unit u
                RETURNING store_location_id, store_unit_id
            )
            INSERT INTO integration.notification_outbox
                (event_key, registration_id, notification_type, title, body, data_json)
            SELECT 'fg:' || u.store_unit_id::text, reg.id, 'FINISHED_GOODS_DELIVERED',
                   'Bitmiş Ürün Teslim Edildi · ' || l.display_name,
                   COALESCE(p.part_code, 'Parça') || ' (' || m.code || ') alana bırakıldı. Talep no: ' || r.request_no,
                   jsonb_build_object(
                       'notificationType', 'FINISHED_GOODS_DELIVERED',
                       'unitId', u.store_unit_id::text,
                       'locationId', l.id::text,
                       'locationName', l.display_name,
                       'requestNo', r.request_no::text,
                       'partCode', p.part_code,
                       'machineCode', m.code
                   )
            FROM inserted_event u
            JOIN ops.store_locations l ON l.id = u.store_location_id
            JOIN ops.transport_requests r ON r.id = @id
            JOIN master.machines m ON m.id = r.machine_id
            LEFT JOIN master.parts p ON p.id = r.part_id
            CROSS JOIN integration.device_notification_registrations reg
            JOIN auth.users usr ON usr.id = reg.user_id AND usr.is_active
            JOIN audit.client_devices d ON d.id = reg.device_id AND d.is_active
            WHERE reg.is_active
              AND EXISTS (
                  SELECT 1 FROM auth.user_roles ur
                  JOIN auth.roles role ON role.id = ur.role_id
                  WHERE ur.user_id = reg.user_id AND role.code = ANY(ARRAY['ADMIN','WAREHOUSE_OPERATOR'])
              )
            ON CONFLICT (event_key, registration_id) DO NOTHING;
            """, connection);
        deliverCmd.Parameters.AddWithValue("id", requestId);
        deliverCmd.Parameters.AddWithValue("now", now);
        await deliverCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureFinishedGoodsEmptyCollectionAsync(
        NpgsqlConnection connection,
        Guid requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var removeCmd = new NpgsqlCommand(
            """
            WITH already_removed AS (
                SELECT se.store_unit_id AS unit_id, se.store_location_id, sl.display_name AS location_name
                FROM ops.store_events se
                JOIN ops.store_locations sl ON sl.id = se.store_location_id
                WHERE se.transport_request_id = @id AND se.event_type = 'UNIT_REMOVED'
                LIMIT 1
            ),
            candidate AS (
                SELECT u.id AS unit_id, u.store_location_id, sl.display_name AS location_name
                FROM ops.store_reservations sr
                JOIN ops.store_units u ON u.id = sr.store_unit_id
                JOIN ops.store_locations sl ON sl.id = u.store_location_id
                WHERE sr.transport_request_id = @id
                  AND sr.reservation_type = 'PICKUP'
                  AND sr.released_at IS NULL
                  AND sr.store_location_id = u.store_location_id
                  AND u.load_state = 'EMPTY'
                  AND u.removed_at IS NULL
                  AND sl.is_enabled
                  AND NOT EXISTS (SELECT 1 FROM already_removed)
                LIMIT 1
            ),
            updated_unit AS (
                UPDATE ops.store_units u
                SET removed_at = @now,
                    removed_by = NULL,
                    removed_device_id = NULL,
                    removal_source = 'ROBOT',
                    row_version = u.row_version + 1
                FROM candidate c
                WHERE u.id = c.unit_id AND u.removed_at IS NULL
                RETURNING u.id, u.store_location_id, c.location_name
            ),
            inserted_event AS (
                INSERT INTO ops.store_events (
                    store_location_id, store_unit_id, event_type,
                    transport_request_id, occurred_at, data_json
                )
                SELECT u.store_location_id, u.id, 'UNIT_REMOVED',
                       @id, @now, '{"source": "SIMULATION_EMPTY_COLLECTED"}'::jsonb
                FROM updated_unit u
                RETURNING store_location_id, store_unit_id
            ),
            resolved AS (
                SELECT u.id AS unit_id, u.store_location_id, u.location_name FROM updated_unit u
                UNION ALL
                SELECT a.unit_id, a.store_location_id, a.location_name FROM already_removed a
            )
            INSERT INTO integration.notification_outbox
                (event_key, registration_id, notification_type, title, body, data_json)
            SELECT 'fg_empty:' || @id::text, reg.id, 'FINISHED_GOODS_COLLECTED',
                   'Boş Kasa Alındı · ' || r.location_name,
                   'Boş kasa ' || m.code || ' makinesine götürüldü. Talep no: ' || req.request_no,
                   jsonb_build_object(
                       'notificationType', 'FINISHED_GOODS_COLLECTED',
                       'unitId', r.unit_id::text,
                       'locationId', r.store_location_id::text,
                       'locationName', r.location_name,
                       'requestNo', req.request_no::text,
                       'machineCode', m.code
                   )
            FROM resolved r
            JOIN ops.transport_requests req ON req.id = @id
            JOIN master.machines m ON m.id = req.machine_id
            CROSS JOIN integration.device_notification_registrations reg
            JOIN auth.users usr ON usr.id = reg.user_id AND usr.is_active
            JOIN audit.client_devices d ON d.id = reg.device_id AND d.is_active
            WHERE reg.is_active
              AND EXISTS (
                  SELECT 1 FROM auth.user_roles ur
                  JOIN auth.roles role ON role.id = ur.role_id
                  WHERE ur.user_id = reg.user_id AND role.code = ANY(ARRAY['ADMIN','WAREHOUSE_OPERATOR'])
              )
            ON CONFLICT (event_key, registration_id) DO NOTHING;
            """, connection);
        removeCmd.Parameters.AddWithValue("id", requestId);
        removeCmd.Parameters.AddWithValue("now", now);
        await removeCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<int> ResetFreshProductionSlotsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
             WITH active_context AS (
                 SELECT session.id AS session_id,
                        session.product_group_id,
                        runtime.active_layout_id AS layout_id
                 FROM ops.production_sessions session
                 JOIN ops.machine_runtime_state runtime
                   ON runtime.machine_id = session.machine_id
                 WHERE session.ended_at IS NULL
                   AND session.status = 'ACTIVE'
                   AND runtime.active_layout_id IS NOT NULL
             ), reset_candidates AS (
                 SELECT DISTINCT state.layout_slot_id
                 FROM active_context context
                 JOIN master.layout_slots slot ON slot.layout_id = context.layout_id
                 JOIN ops.slot_runtime_state state ON state.layout_slot_id = slot.id
                 WHERE slot.is_enabled
                   AND slot.slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
                   AND EXISTS (
                       SELECT 1
                       FROM master.layout_slot_parts mapping
                       JOIN master.product_groups product_group
                         ON product_group.id = context.product_group_id
                       WHERE mapping.layout_slot_id = slot.id
                         AND (
                             mapping.part_id = product_group.main_part_id
                             OR EXISTS (
                                 SELECT 1
                                 FROM master.product_group_parts group_part
                                 WHERE group_part.product_group_id = product_group.id
                                   AND group_part.part_id = mapping.part_id
                                   AND group_part.is_active)))
                   AND NOT EXISTS (
                       SELECT 1
                       FROM ops.transport_requests active_request
                       WHERE active_request.status IN ('CREATED','VALIDATING','QUEUED','SENT_TO_RDS','RUNNING')
                         AND active_request.machine_slot_reservation_active
                         AND (active_request.source_slot_id = slot.id
                              OR active_request.destination_slot_id = slot.id))
                   AND NOT EXISTS (
                       SELECT 1
                       FROM ops.transport_requests current_session_request
                       WHERE current_session_request.production_session_id = context.session_id
                         AND current_session_request.started_at IS NOT NULL
                         AND (current_session_request.source_slot_id = slot.id
                              OR current_session_request.destination_slot_id = slot.id))
             )
             UPDATE ops.slot_runtime_state state
             SET occupancy_state = 'VACANT',
                 load_state = 'UNKNOWN',
                 current_part_id = NULL,
                 current_container_type_id = NULL,
                 row_version = state.row_version + 1,
                 updated_at = now()
             FROM reset_candidates candidate
             WHERE state.layout_slot_id = candidate.layout_slot_id
               AND (state.occupancy_state <> 'VACANT'
                    OR state.load_state <> 'UNKNOWN'
                    OR state.current_part_id IS NOT NULL
                    OR state.current_container_type_id IS NOT NULL)
             """,
            connection);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<int> SimulateConsumedSharedSlotPalletsAsync(
        NpgsqlConnection connection,
        DateTimeOffset completedBefore,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
             WITH consumed AS (
                 SELECT DISTINCT state.layout_slot_id
                 FROM ops.slot_runtime_state state
                 WHERE state.occupancy_state = 'OCCUPIED'
                   AND state.load_state = 'FULL'
                   AND state.current_part_id IS NOT NULL
                   AND EXISTS (
                       SELECT 1
                       FROM ops.transport_requests waiting
                       JOIN master.task_types waiting_type ON waiting_type.id = waiting.task_type_id
                       WHERE waiting_type.code = 'BRING_FULL'
                         AND waiting.status = 'QUEUED'
                         AND waiting.readiness_state = 'READY'
                         AND NOT waiting.machine_slot_reservation_active
                         AND waiting.destination_slot_id = state.layout_slot_id
                         AND EXISTS (
                             SELECT 1
                             FROM ops.transport_requests delivered
                             JOIN master.task_types delivered_type ON delivered_type.id = delivered.task_type_id
                             WHERE delivered_type.code = 'BRING_FULL'
                               AND delivered.status = 'COMPLETED'
                               AND delivered.completed_at <= @completed_before
                               AND delivered.production_session_id = waiting.production_session_id
                               AND delivered.destination_slot_id = state.layout_slot_id
                               AND delivered.part_id = state.current_part_id))
                   AND NOT EXISTS (
                       SELECT 1
                       FROM ops.transport_requests active_request
                       WHERE active_request.status IN ('CREATED','VALIDATING','QUEUED','SENT_TO_RDS','RUNNING')
                         AND active_request.machine_slot_reservation_active
                         AND (active_request.source_slot_id = state.layout_slot_id
                              OR active_request.destination_slot_id = state.layout_slot_id))
             )
             UPDATE ops.slot_runtime_state state
             SET load_state = 'EMPTY',
                 row_version = state.row_version + 1,
                 updated_at = now()
             FROM consumed
             WHERE state.layout_slot_id = consumed.layout_slot_id
               AND state.occupancy_state = 'OCCUPIED'
               AND state.load_state = 'FULL'
             """,
            connection);
        command.Parameters.AddWithValue("completed_before", completedBefore);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplenishEmptyBuffersAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var replenishCmd = new NpgsqlCommand(
            """
            UPDATE ops.slot_runtime_state rs
            SET occupancy_state = 'OCCUPIED',
                load_state = 'EMPTY',
                updated_at = now()
            FROM master.layout_slots ls
            WHERE rs.layout_slot_id = ls.id
              AND ls.slot_role = 'EMPTY_STORAGE'
              AND (rs.occupancy_state != 'OCCUPIED' OR rs.load_state != 'EMPTY')
              AND NOT EXISTS (
                  SELECT 1 FROM ops.transport_requests tr
                  WHERE tr.status IN ('WAITING_PREPARATION', 'QUEUED', 'SENT_TO_RDS', 'RUNNING')
                    AND (tr.source_slot_id = ls.id OR tr.destination_slot_id = ls.id)
              );
            """, connection);
        await replenishCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    internal sealed record DemoCandidateRequest(
        Guid Id,
        string RequestNo,
        string TaskType,
        string Status,
        string ReadinessState,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ReadyAt,
        int? RdsStatus,
        DateTimeOffset? SentAt,
        DateTimeOffset? StartedAt);
}
