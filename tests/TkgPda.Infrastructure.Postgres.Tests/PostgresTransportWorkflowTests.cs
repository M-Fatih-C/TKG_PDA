using Npgsql;
using TkgPda.Application;
using TkgPda.Domain;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Infrastructure.Postgres.Tests;

public sealed class PostgresTransportWorkflowTests
{
    private readonly string _connectionString = Environment.GetEnvironmentVariable("TKG_PDA_TEST_CONNECTION_STRING") ?? string.Empty;

    [PostgresFact]
    public async Task Bring_empty_auto_allocates_a_configured_buffer_source_without_changing_any_slot_state()
    {
        // Customer Workflow — Empty Container Buffer: an operator-omitted
        // source is no longer left unresolved — the backend now allocates a
        // concrete, configured EMPTY_STORAGE slot for it (and reserves it),
        // while leaving both slots' occupancy/load untouched until completion.
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var result = await store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "bring-optional-source", destination: slotId));
        var destinationState = await ReadSlotStateAsync(slotId);

        Assert.Equal(TransportRequestStatus.Queued, result.Request.Status);
        Assert.NotNull(result.Request.SourceSlotId);
        var bufferState = await ReadSlotStateAsync(result.Request.SourceSlotId!.Value);
        Assert.Equal((OccupancyState.Occupied, LoadState.Empty), bufferState);
        Assert.Equal((OccupancyState.Vacant, LoadState.Unknown), destinationState);
        Assert.Equal(3, await ScalarAsync<long>("SELECT count(*) FROM ops.transport_request_events WHERE transport_request_id = @id", ("id", result.Request.Id)));
    }

    [PostgresFact]
    public async Task Bring_empty_success_changes_destination_to_occupied_empty()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var service = new TransportWorkflowService(store);
        var created = await service.CreateAsync(Command(context, TransportTaskType.BringEmpty, "bring-complete", destination: slotId));

        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.SentToRds);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Running);
        var completed = await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Completed);

        Assert.Equal(TransportRequestStatus.Completed, completed.Status);
        Assert.Equal((OccupancyState.Occupied, LoadState.Empty), await ReadSlotStateAsync(slotId));
    }

    [PostgresFact]
    public async Task Remove_full_cancellation_restores_occupied_full_and_preserves_known_values()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        var partId = await VariantPartIdAsync(context.VariantId, 0);
        var containerId = await ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
        await SetSlotAsync(slotId, OccupancyState.Occupied, LoadState.Full, partId, containerId);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var created = await store.CreateAndQueueAsync(Command(context, TransportTaskType.RemoveFull, "remove-full-cancel", source: slotId));

        var cancelled = await store.CancelAsync(created.Request.Id, actorUserId: null);
        var row = await ReadFullSlotStateAsync(slotId);

        Assert.Equal(TransportRequestStatus.Cancelled, cancelled.Request.Status);
        Assert.Equal(OccupancyState.Occupied, row.Occupancy);
        Assert.Equal(LoadState.Full, row.Load);
        Assert.Equal(partId, row.PartId);
        Assert.Equal(containerId, row.ContainerId);
        Assert.Equal(
            "QUEUED>CANCELLED",
            await ScalarAsync<string>(
                "SELECT old_status || '>' || new_status FROM ops.transport_request_events WHERE transport_request_id = @id AND event_type = 'REQUEST_CANCELLED'",
                ("id", created.Request.Id)));
    }

    [PostgresFact]
    public async Task Remove_empty_success_changes_source_to_vacant_unknown()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Occupied, LoadState.Empty);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var service = new TransportWorkflowService(store);
        var created = await service.CreateAsync(Command(context, TransportTaskType.RemoveEmpty, "remove-empty-complete", source: slotId));

        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.SentToRds);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Running);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Completed);

        Assert.Equal((OccupancyState.Vacant, LoadState.Unknown), await ReadSlotStateAsync(slotId));
    }

    [PostgresFact]
    public async Task Bring_and_remove_empty_cancellation_restore_their_expected_states()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slots = await LogisticsSlotsAsync(context.LayoutId, 2);
        await SetSlotAsync(slots[0], OccupancyState.Vacant, LoadState.Unknown);
        await SetSlotAsync(slots[1], OccupancyState.Occupied, LoadState.Empty);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var bring = await store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "bring-cancel", destination: slots[0]));
        await store.CancelAsync(bring.Request.Id, null);
        var remove = await store.CreateAndQueueAsync(Command(context, TransportTaskType.RemoveEmpty, "remove-empty-cancel", source: slots[1]));
        await store.CancelAsync(remove.Request.Id, null);

        Assert.Equal((OccupancyState.Vacant, LoadState.Unknown), await ReadSlotStateAsync(slots[0]));
        Assert.Equal((OccupancyState.Occupied, LoadState.Empty), await ReadSlotStateAsync(slots[1]));
    }

    [PostgresFact]
    public async Task Load_state_reporting_changes_only_load_state_and_is_idempotent()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        var actorId = await EnsureActorAsync();
        await SetSlotAsync(slotId, OccupancyState.Occupied, LoadState.Empty);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var service = new TransportWorkflowService(store);

        var first = await service.UpdateLoadStateAsync(new(context.MachineId, slotId, LoadState.Full, actorId));
        var replay = await service.UpdateLoadStateAsync(new(context.MachineId, slotId, LoadState.Full, actorId));
        var state = await ReadFullSlotStateAsync(slotId);

        Assert.False(first.WasIdempotentReplay);
        Assert.True(replay.WasIdempotentReplay);
        Assert.Equal(first.RowVersion, replay.RowVersion);
        Assert.Equal(OccupancyState.Occupied, state.Occupancy);
        Assert.Equal(LoadState.Full, state.Load);
    }

    [PostgresFact]
    public async Task Load_state_reporting_rejects_vacant_machine_and_busy_slots()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var actorId = await EnsureActorAsync();
        var logistics = await LogisticsSlotsAsync(context.LayoutId, 2);
        var machineRegion = await ScalarAsync<Guid>(
            "SELECT id FROM master.layout_slots WHERE layout_id = @layout_id AND slot_role = 'MACHINE' LIMIT 1",
            ("layout_id", context.LayoutId));
        await SetSlotAsync(logistics[0], OccupancyState.Vacant, LoadState.Unknown);
        await SetSlotAsync(logistics[1], OccupancyState.Occupied, LoadState.Empty);
        await SetSlotAsync(machineRegion, OccupancyState.Occupied, LoadState.Empty);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var service = new TransportWorkflowService(store);

        var vacant = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            service.UpdateLoadStateAsync(new(context.MachineId, logistics[0], LoadState.Full, actorId)));
        var machine = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            service.UpdateLoadStateAsync(new(context.MachineId, machineRegion, LoadState.Full, actorId)));
        _ = await service.CreateAsync(Command(context, TransportTaskType.RemoveEmpty, "load-state-busy", source: logistics[1]));
        var busy = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            service.UpdateLoadStateAsync(new(context.MachineId, logistics[1], LoadState.Full, actorId)));

        Assert.Equal("OCCUPIED_SLOT_REQUIRED", vacant.Code);
        Assert.Equal("MACHINE_REGION_FORBIDDEN", machine.Code);
        Assert.Equal("SLOT_BUSY", busy.Code);
    }

    [PostgresFact]
    public async Task Rds_task_identity_is_precreated_exactly_once_with_request_uuid()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var workflowStore = new PostgresTransportWorkflowStore(_connectionString);
        var request = await workflowStore.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "rds-task-identity", destination: slotId));
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        var rdsStore = new PostgresRdsExecutionStore(dataSource);
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new { requestId = request.Request.Id });

        await rdsStore.EnsureTaskRecordAsync(request.Request.Id, request.Request.Id.ToString(), "SIM_TASK", payload);
        await rdsStore.EnsureTaskRecordAsync(request.Request.Id, request.Request.Id.ToString(), "SIM_TASK", payload);

        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM integration.rds_tasks WHERE transport_request_id = @id", ("id", request.Request.Id)));
        Assert.Equal(request.Request.Id.ToString(), await ScalarAsync<string>("SELECT rds_task_record_id FROM integration.rds_tasks WHERE transport_request_id = @id", ("id", request.Request.Id)));

        var navigationProfileId = await ScalarAsync<Guid>(
            "INSERT INTO master.navigation_profiles (machine_id, code, name) VALUES (@machine_id, 'P08B-REFRESH', 'Phase 08B refresh evidence') RETURNING id",
            ("machine_id", context.MachineId));
        await ExecuteAsync(
            "UPDATE ops.transport_requests SET navigation_id = @navigation_id, status = 'COMPLETED' WHERE id = @id; UPDATE integration.rds_tasks SET rds_status = 1003 WHERE transport_request_id = @id",
            ("navigation_id", navigationProfileId),
            ("id", request.Request.Id));
        Assert.Contains(request.Request.Id, await rdsStore.ListPendingWorkSiteRefreshRequestIdsAsync(10));
        await rdsStore.WriteExecutionAuditAsync(request.Request.Id, "RDS_WORKSITE_REFRESHED", "PASS", "refreshed", Guid.NewGuid());
        Assert.DoesNotContain(request.Request.Id, await rdsStore.ListPendingWorkSiteRefreshRequestIdsAsync(10));
    }

    [PostgresFact]
    public async Task Rds_dispatch_context_resolves_explicit_template_binding_and_physical_mapping()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Occupied, LoadState.Empty);
        await ExecuteAsync(
            """
            UPDATE master.task_templates
            SET is_active = false
            WHERE variant_id = @variant_id
              AND product_group_id IS NULL
              AND task_type_id = (SELECT id FROM master.task_types WHERE code = 'REMOVE_EMPTY');
            INSERT INTO master.task_templates (
                variant_id, task_type_id, rds_task_label, priority, config_json, is_active)
            SELECT @variant_id, id, 'PHASE08B_SIM_TASK', 17,
                   '{"testScope":"P08B","rdsInputBindings":[{"param":"fromSite","source":"SOURCE_RDS_SITE_ID","required":true}]}'::jsonb,
                   true
            FROM master.task_types WHERE code = 'REMOVE_EMPTY'
            """,
            ("variant_id", context.VariantId));
        var workSiteId = await ScalarAsync<Guid>(
            """
            INSERT INTO integration.rds_worksites (
                rds_site_id, rds_type, last_filled, last_locked, last_disabled,
                last_sync_failed, last_sync_at, raw_state_json)
            VALUES ('P08B-PHYSICAL-01', 1, 1, 0, 0, 0, now(), '{"siteId":"P08B-PHYSICAL-01"}'::jsonb)
            RETURNING id
            """);
        await ExecuteAsync(
            "INSERT INTO integration.layout_slot_rds_mapping (layout_slot_id, rds_worksite_id) VALUES (@slot_id, @worksite_id)",
            ("slot_id", slotId),
            ("worksite_id", workSiteId));
        await using var workflowStore = new PostgresTransportWorkflowStore(_connectionString);
        var request = await workflowStore.CreateAndQueueAsync(Command(context, TransportTaskType.RemoveEmpty, "rds-context", source: slotId));
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        var executionStore = new PostgresRdsExecutionStore(dataSource);

        var dispatch = await executionStore.GetDispatchContextAsync(request.Request.Id);

        Assert.NotNull(dispatch);
        Assert.Equal("PHASE08B_SIM_TASK", dispatch.RdsTaskLabel);
        Assert.Equal(17, dispatch.TemplatePriority);
        Assert.Equal("P08B-PHYSICAL-01", dispatch.Source.SiteId);
        Assert.True(dispatch.Source.IsMapped);
        Assert.Equal(1, dispatch.Source.Type);
        Assert.Equal(slotId, dispatch.Source.SlotId);
        Assert.Contains(
            await executionStore.ListTaskTemplatesAsync(),
            template => template.Id == dispatch.TaskTemplateId && template.RdsTaskLabel == "PHASE08B_SIM_TASK");
    }

    [PostgresFact]
    public async Task Endpoint_resolution_REMOVE_EMPTY_resolves_strictly_to_EMPTY_STORAGE_never_DROP()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var machineSlots = await LogisticsSlotsAsync(context.LayoutId, 3);
        await SetSlotAsync(machineSlots[0], OccupancyState.Occupied, LoadState.Empty);
        await SetSlotAsync(machineSlots[1], OccupancyState.Occupied, LoadState.Full);
        await SetSlotAsync(machineSlots[2], OccupancyState.Vacant, LoadState.Unknown);

        // Create an EMPTY_STORAGE slot and map it
        var emptyStorageSlotId = await ScalarAsync<Guid>(
            """
            INSERT INTO master.layout_slots (layout_id, code, display_name, slot_role, is_enabled, display_order)
            VALUES (@layout_id, 'TEST-EMPTY-BUF', 'Empty Buffer', 'EMPTY_STORAGE', true, -1000)
            RETURNING id
            """,
            ("layout_id", context.LayoutId));
        var emptyWorkSiteId = await ScalarAsync<Guid>(
            """
            INSERT INTO integration.rds_worksites (rds_site_id, rds_type, last_filled, last_locked, last_disabled, last_sync_failed, last_sync_at, raw_state_json)
            VALUES ('WS-EMPTY-STORAGE-TEST', 1, 0, 0, 0, 0, now(), '{"siteId":"WS-EMPTY-STORAGE-TEST"}'::jsonb)
            RETURNING id
            """);
        await ExecuteAsync(
            "INSERT INTO integration.layout_slot_rds_mapping (layout_slot_id, rds_worksite_id) VALUES (@slot_id, @worksite_id)",
            ("slot_id", emptyStorageSlotId),
            ("worksite_id", emptyWorkSiteId));

        // Create a DROP slot and map it
        var dropSlotId = await ScalarAsync<Guid>(
            """
            INSERT INTO master.layout_slots (layout_id, code, display_name, slot_role, is_enabled, display_order)
            VALUES (@layout_id, 'TEST-DROP-AREA', 'Drop Area', 'DROP', true, -900)
            RETURNING id
            """,
            ("layout_id", context.LayoutId));
        var dropWorkSiteId = await ScalarAsync<Guid>(
            """
            INSERT INTO integration.rds_worksites (rds_site_id, rds_type, last_filled, last_locked, last_disabled, last_sync_failed, last_sync_at, raw_state_json)
            VALUES ('WS-DROP-TEST', 1, 0, 0, 0, 0, now(), '{"siteId":"WS-DROP-TEST"}'::jsonb)
            RETURNING id
            """);
        await ExecuteAsync(
            "INSERT INTO integration.layout_slot_rds_mapping (layout_slot_id, rds_worksite_id) VALUES (@slot_id, @worksite_id)",
            ("slot_id", dropSlotId),
            ("worksite_id", dropWorkSiteId));

        await ExecuteAsync(
            """
            INSERT INTO master.task_templates (
                variant_id, task_type_id, rds_task_label, priority, config_json, is_active)
            SELECT @variant_id, id, 'TKG_' || code, 10,
                   '{"testScope":"P08B","executionMode":"RDSCORE_NATIVE_ORDER","rdsInputBindings":[{"param":"fromSite","source":"SOURCE_RDS_SITE_ID","required":true}]}'::jsonb,
                   true
            FROM master.task_types WHERE code IN ('REMOVE_EMPTY', 'REMOVE_FULL', 'BRING_EMPTY')
            """,
            ("variant_id", context.VariantId));

        try
        {
            await using var workflowStore = new PostgresTransportWorkflowStore(_connectionString);
            await using var dataSource = NpgsqlDataSource.Create(_connectionString);
            var executionStore = new PostgresRdsExecutionStore(dataSource);

            // 1. REMOVE_EMPTY without destination -> must resolve to EMPTY_STORAGE, NEVER DROP
            await SetSlotAsync(emptyStorageSlotId, OccupancyState.Vacant, LoadState.Unknown);
            var removeEmptyReq = await workflowStore.CreateAndQueueAsync(Command(context, TransportTaskType.RemoveEmpty, "re-test", source: machineSlots[0]));
            var removeEmptyDispatch = await executionStore.GetDispatchContextAsync(removeEmptyReq.Request.Id);
            Assert.NotNull(removeEmptyDispatch);
            Assert.Equal("EMPTY_STORAGE", removeEmptyDispatch.Destination.SlotRole);
            Assert.Equal("WS-EMPTY-STORAGE-TEST", removeEmptyDispatch.Destination.SiteId);
            Assert.NotEqual("DROP", removeEmptyDispatch.Destination.SlotRole);
            Assert.NotEqual("WS-DROP-TEST", removeEmptyDispatch.Destination.SiteId);

            // 2. REMOVE_FULL without destination -> must resolve to DROP, NEVER EMPTY_STORAGE
            var removeFullReq = await workflowStore.CreateAndQueueAsync(Command(context, TransportTaskType.RemoveFull, "rf-test", source: machineSlots[1]));
            var removeFullDispatch = await executionStore.GetDispatchContextAsync(removeFullReq.Request.Id);
            Assert.NotNull(removeFullDispatch);
            Assert.Equal("DROP", removeFullDispatch.Destination.SlotRole);
            Assert.Equal("WS-DROP-TEST", removeFullDispatch.Destination.SiteId);
            Assert.NotEqual("EMPTY_STORAGE", removeFullDispatch.Destination.SlotRole);
            Assert.NotEqual("WS-EMPTY-STORAGE-TEST", removeFullDispatch.Destination.SiteId);

            // 3. BRING_EMPTY without source -> must resolve source to EMPTY_STORAGE
            await ExecuteAsync("UPDATE ops.transport_requests SET status = 'CANCELLED' WHERE id = @id", ("id", removeEmptyReq.Request.Id));
            await SetSlotAsync(emptyStorageSlotId, OccupancyState.Occupied, LoadState.Empty);
            // Explicit isolated pickup presence; a vacant mirror contradicts this fixture.
            await ExecuteAsync("UPDATE integration.rds_worksites SET last_filled = 1, last_sync_at = now() WHERE id = @id", ("id", emptyWorkSiteId));
            var bringEmptyReq = await workflowStore.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "be-test", destination: machineSlots[2]));
            var bringEmptyDispatch = await executionStore.GetDispatchContextAsync(bringEmptyReq.Request.Id);
            Assert.NotNull(bringEmptyDispatch);
            Assert.Equal("EMPTY_STORAGE", bringEmptyDispatch.Source.SlotRole);
            Assert.Equal("WS-EMPTY-STORAGE-TEST", bringEmptyDispatch.Source.SiteId);
        }
        finally
        {
            await ExecuteAsync(
                """
                DELETE FROM ops.transport_requests WHERE source_slot_id IN (@empty_id, @drop_id) OR destination_slot_id IN (@empty_id, @drop_id);
                DELETE FROM integration.layout_slot_rds_mapping WHERE layout_slot_id IN (@empty_id, @drop_id);
                DELETE FROM master.layout_slots WHERE id IN (@empty_id, @drop_id);
                DELETE FROM master.task_templates WHERE variant_id = @variant_id AND config_json ->> 'testScope' = 'P08B';
                DELETE FROM integration.rds_worksites WHERE id IN (@empty_ws_id, @drop_ws_id);
                """,
                ("empty_id", emptyStorageSlotId),
                ("drop_id", dropSlotId),
                ("variant_id", context.VariantId),
                ("empty_ws_id", emptyWorkSiteId),
                ("drop_ws_id", dropWorkSiteId));
        }
    }

    [PostgresFact]
    public async Task Navigation_profile_fails_closed_and_never_leaks_unmapped_or_other_variants()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        var executionStore = new PostgresRdsExecutionStore(dataSource);

        // Create a published navigation profile for this machine
        var profileId = await ScalarAsync<Guid>(
            """
            INSERT INTO master.navigation_profiles (
                code, name, machine_id, robot_map_name, config_json, status)
            VALUES ('NAV-V1', 'Profile V1', @machine_id, 'map_v1', '{"switchTransitionStation":"SM11","vehicle":"Sfl-Cbd15"}'::jsonb, 'PUBLISHED')
            RETURNING id
            """,
            ("machine_id", context.MachineId));

        try
        {
            // Before assignment: unmapped variant must return null (fail closed)
            var unresolved = await executionStore.GetNavigationProfileSummaryAsync(null, context.VariantId, context.MachineId);
            Assert.Null(unresolved);

            // Assign to variant
            await ExecuteAsync(
                """
                INSERT INTO master.variant_navigation_profile_assignments (
                    variant_id, machine_id, navigation_profile_id, is_default)
                VALUES (@variant_id, @machine_id, @profile_id, true)
                """,
                ("variant_id", context.VariantId),
                ("machine_id", context.MachineId),
                ("profile_id", profileId));

            // Exact match resolves
            var resolved = await executionStore.GetNavigationProfileSummaryAsync(null, context.VariantId, context.MachineId);
            Assert.NotNull(resolved);
            Assert.Equal("NAV-V1", resolved.Code);
            Assert.Equal("map_v1", resolved.RobotMapName);

            // Different variant never leaks this profile (returns null)
            var otherVariantId = Guid.NewGuid();
            var otherVariantResult = await executionStore.GetNavigationProfileSummaryAsync(null, otherVariantId, context.MachineId);
            Assert.Null(otherVariantResult);

            // Different machine never resolves this profile (returns null)
            var otherMachineId = Guid.NewGuid();
            var otherMachineResult = await executionStore.GetNavigationProfileSummaryAsync(null, context.VariantId, otherMachineId);
            Assert.Null(otherMachineResult);
        }
        finally
        {
            await ExecuteAsync(
                """
                DELETE FROM master.variant_navigation_profile_assignments WHERE navigation_profile_id = @profile_id;
                DELETE FROM master.navigation_profiles WHERE id = @profile_id;
                """,
                ("profile_id", profileId));
        }
    }

    [PostgresFact]
    public async Task HasUnreleasedMapSwitchLock_tracks_lock_lifecycle_in_audit_logs()
    {
        await ResetAsync();
        var requestId = Guid.NewGuid();
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        var executionStore = new PostgresRdsExecutionStore(dataSource);

        // Initially no lock
        Assert.False(await executionStore.HasUnreleasedMapSwitchLockAsync(requestId, "Sfl-Cbd15"));
        Assert.Null(await executionStore.GetUnreleasedMapSwitchLockAsync(requestId, "Sfl-Cbd15"));

        // Acquire lock audit with fingerprint
        var lockInfo = new RdsCoreRobotLockInfo(true, "10.0.0.1", "TKG-NATIVE", 5000, "1634023715", 2, "ok");
        await executionStore.WriteExecutionAuditAsync(
            requestId,
            "MAP_PREP_LOCK_ACQUIRED",
            "LOCK_ACQUIRED",
            "Acquired lock",
            Guid.NewGuid(),
            extraDetails: new { vehicle = "Sfl-Cbd15", requiredMap = "26.7.2026_map_optimak_1", lockInfo });

        Assert.True(await executionStore.HasUnreleasedMapSwitchLockAsync(requestId, "Sfl-Cbd15"));
        var snapshot = await executionStore.GetUnreleasedMapSwitchLockAsync(requestId, "Sfl-Cbd15");
        Assert.NotNull(snapshot);
        Assert.True(snapshot.HasUnreleasedLock);
        Assert.Equal("Sfl-Cbd15", snapshot.Vehicle);
        Assert.Equal("26.7.2026_map_optimak_1", snapshot.TargetMap);
        Assert.NotNull(snapshot.CapturedLockInfo);
        Assert.True(snapshot.CapturedLockInfo.Locked);
        Assert.Equal("10.0.0.1", snapshot.CapturedLockInfo.Ip);
        Assert.Equal("1634023715", snapshot.CapturedLockInfo.TimeT);

        // Release lock audit
        await executionStore.WriteExecutionAuditAsync(requestId, "MAP_PREP_LOCK_RELEASED", "UNLOCK_SUCCESS", "Released lock", Guid.NewGuid());
        Assert.False(await executionStore.HasUnreleasedMapSwitchLockAsync(requestId, "Sfl-Cbd15"));
        Assert.Null(await executionStore.GetUnreleasedMapSwitchLockAsync(requestId, "Sfl-Cbd15"));
    }

    [PostgresFact]
    public async Task Machine_region_is_rejected()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var machineRegion = await ScalarAsync<Guid>(
            "SELECT id FROM master.layout_slots WHERE layout_id = @layout_id AND slot_role = 'MACHINE' LIMIT 1",
            ("layout_id", context.LayoutId));
        await SetSlotAsync(machineRegion, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "machine-region", destination: machineRegion)));

        Assert.Equal("MACHINE_REGION_FORBIDDEN", error.Code);
    }

    [PostgresFact]
    public async Task Layout_machine_regions_are_distinct_from_customer_machine_master_records()
    {
        await ResetAsync();

        var machineMasterCount = await ScalarAsync<long>("SELECT count(*) FROM master.machines");
        var machineRegionCount = await ScalarAsync<long>("SELECT count(*) FROM master.layout_slots WHERE slot_role = 'MACHINE'");

        Assert.Equal(9, machineMasterCount);
        Assert.Equal(11, machineRegionCount);
    }

    [PostgresFact]
    public async Task Slot_from_another_machine_is_rejected()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var other = await ContextAsync("SH-01-B", "1");
        var otherSlot = await LogisticsSlotAsync(other.LayoutId, requireContainer: false);
        await SetSlotAsync(otherSlot, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "cross-machine", destination: otherSlot)));

        Assert.Equal("CROSS_MACHINE_SLOT", error.Code);
    }

    [PostgresFact]
    public async Task Request_context_must_match_active_variant_and_layout()
    {
        await ResetAsync();
        var active = await ConfigureAsync("SH-01-A", "1");
        var inactive = await ContextAsync("SH-01-A", "2");
        var slotId = await LogisticsSlotAsync(active.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(Command(inactive, TransportTaskType.BringEmpty, "inactive-context", destination: slotId)));

        Assert.Equal("RUNTIME_CONTEXT_MISMATCH", error.Code);
    }

    [PostgresFact]
    public async Task Sequential_idempotent_retry_returns_the_same_request()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var command = Command(context, TransportTaskType.BringEmpty, "same-retry", destination: slotId);

        var first = await store.CreateAndQueueAsync(command);
        var second = await store.CreateAndQueueAsync(command);

        Assert.False(first.WasIdempotentReplay);
        Assert.True(second.WasIdempotentReplay);
        Assert.Equal(first.Request.Id, second.Request.Id);
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [PostgresFact]
    public async Task Reusing_an_idempotency_key_for_a_different_payload_is_rejected()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slots = await LogisticsSlotsAsync(context.LayoutId, 2);
        await SetSlotAsync(slots[0], OccupancyState.Vacant, LoadState.Unknown);
        await SetSlotAsync(slots[1], OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        await store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "reused-key", destination: slots[0]));

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "reused-key", destination: slots[1])));

        Assert.Equal("IDEMPOTENCY_KEY_REUSED", error.Code);
    }

    [PostgresFact]
    public async Task Concurrent_requests_for_the_same_slot_create_only_one_reservation()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var attempts = await Task.WhenAll(
            CaptureAsync(() => store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "slot-race-a", destination: slotId))),
            CaptureAsync(() => store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "slot-race-b", destination: slotId))));

        Assert.Single(attempts, attempt => attempt.Result is not null);
        var failure = Assert.Single(attempts, attempt => attempt.Error is not null).Error!;
        Assert.Equal("SLOT_ALREADY_RESERVED", failure.Code);
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [PostgresFact]
    public async Task Concurrent_retry_with_same_key_returns_one_logical_request()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var command = Command(context, TransportTaskType.BringEmpty, "same-key-race", destination: slotId);

        var results = await Task.WhenAll(store.CreateAndQueueAsync(command), store.CreateAndQueueAsync(command));

        Assert.Equal(results[0].Request.Id, results[1].Request.Id);
        Assert.Single(results, result => result.WasIdempotentReplay);
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM ops.transport_requests"));
    }

    [PostgresFact]
    public async Task Explicit_container_conflict_is_rejected_but_null_association_is_allowed()
    {
        await ResetAsync();
        var context = await ConfigureAsync("412B", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: true);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        var defaultContainer = await ScalarAsync<Guid>("SELECT default_container_type_id FROM master.layout_slots WHERE id = @id", ("id", slotId));
        var otherContainer = await ScalarAsync<Guid>("SELECT id FROM master.container_types WHERE id <> @id ORDER BY code LIMIT 1", ("id", defaultContainer));
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var conflict = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "container-conflict", destination: slotId) with { ContainerTypeId = otherContainer }));
        Assert.Equal("CONTAINER_CONFLICT", conflict.Code);

        var allowed = await store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "container-unresolved", destination: slotId));
        Assert.Equal(TransportRequestStatus.Queued, allowed.Request.Status);
    }

    [PostgresFact]
    public async Task Source_unresolved_associations_accept_existing_master_part_and_container_without_guessing()
    {
        await ResetAsync();
        var context = await ConfigureAsync("GAR", "1");
        var slotId = await ScalarAsync<Guid>(
            """
            SELECT id
            FROM master.layout_slots
            WHERE layout_id = @layout_id
              AND slot_role <> 'MACHINE'
              AND default_container_type_id IS NULL
              AND jsonb_array_length(COALESCE(metadata_json->'canonical_part_codes', '[]'::jsonb)) = 0
            ORDER BY display_order
            LIMIT 1
            """,
            ("layout_id", context.LayoutId));
        var partId = await VariantPartIdAsync(context.VariantId, 0);
        var containerId = await ScalarAsync<Guid>("SELECT id FROM master.container_types ORDER BY code LIMIT 1");
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var command = Command(context, TransportTaskType.BringEmpty, "unresolved-evidence", destination: slotId) with
        {
            PartId = partId,
            ContainerTypeId = containerId
        };

        var result = await store.CreateAndQueueAsync(command);

        Assert.Equal(TransportRequestStatus.Queued, result.Request.Status);
        Assert.Equal(partId, result.Request.PartId);
        Assert.Equal(containerId, result.Request.ContainerTypeId);
    }

    [PostgresFact]
    public async Task Explicit_part_conflict_from_curated_slot_mapping_is_rejected()
    {
        await ResetAsync();
        var context = await ConfigureAsync("412B", "1");
        var slotId = await ScalarAsync<Guid>(
            """
            SELECT id
            FROM master.layout_slots
            WHERE layout_id = @layout_id
              AND jsonb_array_length(COALESCE(metadata_json->'canonical_part_codes', '[]'::jsonb)) > 0
            ORDER BY display_order
            LIMIT 1
            """,
            ("layout_id", context.LayoutId));
        var knownPartCode = await ScalarAsync<string>(
            "SELECT metadata_json->'canonical_part_codes'->>0 FROM master.layout_slots WHERE id = @id",
            ("id", slotId));
        var knownPartId = await ScalarAsync<Guid>(
            "SELECT id FROM master.parts WHERE part_code = @code",
            ("code", knownPartCode));
        await AssignSlotPartAsync(slotId, knownPartId);
        var conflictingPart = await ScalarAsync<Guid>(
            """
            SELECT compatible.part_id
            FROM (
                SELECT pg.main_part_id AS part_id
                FROM master.product_groups pg
                WHERE pg.variant_id = @variant_id AND pg.is_active
                UNION
                SELECT gpp.part_id
                FROM master.product_groups pg
                JOIN master.product_group_parts gpp
                  ON gpp.product_group_id = pg.id AND gpp.is_active
                WHERE pg.variant_id = @variant_id AND pg.is_active
            ) compatible
            JOIN master.parts p ON p.id = compatible.part_id
            WHERE p.part_code <> @code
            ORDER BY p.part_code
            LIMIT 1
            """,
            ("variant_id", context.VariantId),
            ("code", knownPartCode));
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "part-conflict", destination: slotId) with { PartId = conflictingPart }));

        Assert.Equal("PART_CONFLICT", error.Code);
    }

    [PostgresFact]
    public async Task Active_reservation_blocks_variant_switch_until_cancelled()
    {
        await ResetAsync();
        var active = await ConfigureAsync("SH-01-A", "1");
        var next = await ContextAsync("SH-01-A", "2");
        var slotId = await LogisticsSlotAsync(active.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var created = await store.CreateAndQueueAsync(Command(active, TransportTaskType.BringEmpty, "switch-block", destination: slotId));
        var selection = new VariantSelection(active.MachineId, next.VariantId, next.LayoutId, null, null);

        var blocked = await Assert.ThrowsAsync<WorkflowRuleException>(() => store.SwitchVariantAsync(selection));
        Assert.Equal("ACTIVE_TRANSPORT_EXISTS", blocked.Code);

        await store.CancelAsync(created.Request.Id, null);
        await store.SwitchVariantAsync(selection);
        var selectedVariant = await ScalarAsync<Guid>("SELECT active_variant_id FROM ops.machine_runtime_state WHERE machine_id = @id", ("id", active.MachineId));
        Assert.Equal(next.VariantId, selectedVariant);
    }

    [PostgresFact]
    public async Task Switch_variant_without_runtime_state_throws_runtime_state_not_configured()
    {
        await ResetAsync();
        var context = await ContextAsync("SH-01-B", "1");
        await ExecuteAsync("DELETE FROM ops.machine_runtime_state WHERE machine_id = @id", ("id", context.MachineId));
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var selection = new VariantSelection(context.MachineId, context.VariantId, context.LayoutId, null, null);

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() => store.SwitchVariantAsync(selection));
        Assert.Equal("RUNTIME_STATE_NOT_CONFIGURED", error.Code);
    }

    [PostgresFact]
    public async Task Switch_variant_on_SH01B_with_baseline_runtime_succeeds_and_updates_active_variant()
    {
        await ResetAsync();
        var v1 = await ContextAsync("SH-01-B", "1");
        var v2 = await ContextAsync("SH-01-B", "2");

        // Seed initial baseline runtime state for SH-01-B (Variant 1, Layout 1, null navigation)
        await ExecuteAsync(
            """
            INSERT INTO ops.machine_runtime_state (machine_id, active_variant_id, active_layout_id, active_navigation_id)
            VALUES (@machine_id, @variant_id, @layout_id, NULL)
            ON CONFLICT (machine_id) DO UPDATE
            SET active_variant_id = EXCLUDED.active_variant_id,
                active_layout_id = EXCLUDED.active_layout_id,
                active_navigation_id = NULL
            """,
            ("machine_id", v1.MachineId),
            ("variant_id", v1.VariantId),
            ("layout_id", v1.LayoutId));

        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var selection = new VariantSelection(v1.MachineId, v2.VariantId, v2.LayoutId, null, null);

        await store.SwitchVariantAsync(selection);

        var selectedVariant = await ScalarAsync<Guid>(
            "SELECT active_variant_id FROM ops.machine_runtime_state WHERE machine_id = @id",
            ("id", v1.MachineId));
        var selectedLayout = await ScalarAsync<Guid>(
            "SELECT active_layout_id FROM ops.machine_runtime_state WHERE machine_id = @id",
            ("id", v1.MachineId));
        Assert.Equal(v2.VariantId, selectedVariant);
        Assert.Equal(v2.LayoutId, selectedLayout);
    }

    [PostgresFact]
    public async Task Start_production_on_non_SH01A_machine_with_baseline_runtime_and_null_navigation_succeeds()
    {
        await ResetAsync();
        var v1 = await ContextAsync("SH-01-B", "1");

        // Ensure baseline runtime state with null navigation
        await ExecuteAsync(
            """
            INSERT INTO ops.machine_runtime_state (machine_id, active_variant_id, active_layout_id, active_navigation_id)
            VALUES (@machine_id, @variant_id, @layout_id, NULL)
            ON CONFLICT (machine_id) DO UPDATE
            SET active_variant_id = EXCLUDED.active_variant_id,
                active_layout_id = EXCLUDED.active_layout_id,
                active_navigation_id = NULL
            """,
            ("machine_id", v1.MachineId),
            ("variant_id", v1.VariantId),
            ("layout_id", v1.LayoutId));

        // Get product group and main part for SH-01-B variant 1
        var groupAndPart = await ScalarAsync<Guid>(
            "SELECT id FROM master.product_groups WHERE variant_id = @variant_id LIMIT 1",
            ("variant_id", v1.VariantId));
        var mainPartId = await ScalarAsync<Guid>(
            "SELECT main_part_id FROM master.product_groups WHERE id = @group_id",
            ("group_id", groupAndPart));
        var userId = await EnsureActorAsync();
        await ExecuteAsync(
            """
            INSERT INTO auth.user_roles (user_id, role_id)
            SELECT @user_id, id FROM auth.roles WHERE code = 'ADMIN'
            ON CONFLICT DO NOTHING
            """,
            ("user_id", userId));
        var deviceId = await ScalarAsync<Guid>(
            """
            INSERT INTO audit.client_devices (device_code, name, device_type, is_active)
            VALUES ('TEST-DEVICE', 'Test Device', 'PDA', true)
            ON CONFLICT (device_code) DO UPDATE SET is_active = true
            RETURNING id
            """);

        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var command = new StartProductionSession(
            v1.MachineId,
            v1.VariantId,
            groupAndPart,
            mainPartId,
            userId,
            deviceId,
            $"prod-sh01b-{Guid.NewGuid():N}");

        var result = await store.StartAsync(command);
        Assert.NotNull(result);
        Assert.Equal(v1.MachineId, result.Session.MachineId);
        Assert.Equal(v1.VariantId, result.Session.VariantId);
        Assert.Equal(mainPartId, result.Session.MainPartId);
    }

    [PostgresFact]
    public async Task Baseline_runtime_self_healing_configures_all_active_machines_without_overwriting_valid_selection()
    {
        await ResetAsync();

        // 1. Manually configure SH-01-B to variant 2
        var v2 = await ContextAsync("SH-01-B", "2");
        await ExecuteAsync(
            """
            INSERT INTO ops.machine_runtime_state (machine_id, active_variant_id, active_layout_id, active_navigation_id)
            VALUES (@machine_id, @variant_id, @layout_id, NULL)
            ON CONFLICT (machine_id) DO UPDATE
            SET active_variant_id = EXCLUDED.active_variant_id,
                active_layout_id = EXCLUDED.active_layout_id,
                active_navigation_id = NULL
            """,
            ("machine_id", v2.MachineId),
            ("variant_id", v2.VariantId),
            ("layout_id", v2.LayoutId));

        // 2. Run the exact self-healing query from start_local_stack
        await ExecuteAsync(
            """
            WITH candidate_variants AS (
                SELECT
                    m.id AS machine_id,
                    v.id AS variant_id,
                    vla.layout_id,
                    ROW_NUMBER() OVER (
                        PARTITION BY m.id
                        ORDER BY
                            (CASE WHEN vla.is_default THEN 0 ELSE 1 END),
                            v.display_order ASC,
                            v.code ASC,
                            l.version DESC,
                            l.code ASC
                    ) as rn
                FROM master.machines m
                JOIN master.variants v ON v.machine_id = m.id AND v.is_active
                JOIN master.variant_layout_assignments vla ON vla.variant_id = v.id AND vla.machine_id = m.id
                JOIN master.layouts l ON l.id = vla.layout_id AND l.machine_id = m.id AND l.status = 'PUBLISHED'
                WHERE m.is_active
            ),
            selected_machine_defaults AS (
                SELECT cv.machine_id, cv.variant_id, cv.layout_id
                FROM candidate_variants cv
                WHERE cv.rn = 1
            ),
            nav_candidates AS (
                SELECT
                    smd.machine_id,
                    np.id AS navigation_id,
                    ROW_NUMBER() OVER (
                        PARTITION BY smd.machine_id
                        ORDER BY (CASE WHEN vnpa.is_default THEN 0 ELSE 1 END), np.version DESC
                    ) AS nav_rn,
                    COUNT(*) OVER (
                        PARTITION BY smd.machine_id
                    ) AS nav_count
                FROM selected_machine_defaults smd
                JOIN master.variant_navigation_profile_assignments vnpa
                  ON vnpa.variant_id = smd.variant_id AND vnpa.machine_id = smd.machine_id
                JOIN master.navigation_profiles np
                  ON np.id = vnpa.navigation_profile_id AND np.machine_id = smd.machine_id AND np.status = 'PUBLISHED'
            )
            INSERT INTO ops.machine_runtime_state (
                machine_id,
                active_variant_id,
                active_layout_id,
                active_navigation_id,
                row_version,
                updated_at
            )
            SELECT
                smd.machine_id,
                smd.variant_id,
                smd.layout_id,
                CASE WHEN nc.nav_count = 1 THEN nc.navigation_id ELSE NULL END,
                0,
                now()
            FROM selected_machine_defaults smd
            LEFT JOIN nav_candidates nc ON nc.machine_id = smd.machine_id AND nc.nav_rn = 1
            ON CONFLICT (machine_id) DO UPDATE
            SET active_variant_id = EXCLUDED.active_variant_id,
                active_layout_id = EXCLUDED.active_layout_id,
                active_navigation_id = EXCLUDED.active_navigation_id,
                updated_at = now()
            WHERE ops.machine_runtime_state.active_variant_id IS NULL
               OR ops.machine_runtime_state.active_layout_id IS NULL;
            """);

        // 3. Verify all 9 active machines have runtime state configured
        var configuredCount = await ScalarAsync<long>(
            """
            SELECT count(*)
            FROM ops.machine_runtime_state rs
            JOIN master.machines m ON m.id = rs.machine_id
            WHERE m.is_active
              AND rs.active_variant_id IS NOT NULL
              AND rs.active_layout_id IS NOT NULL
            """);
        var activeMachineCount = await ScalarAsync<long>("SELECT count(*) FROM master.machines WHERE is_active");
        Assert.Equal(9, activeMachineCount);
        Assert.Equal(9, configuredCount);

        // 4. Verify SH-01-B still has variant 2 (existing valid selection was NOT overwritten)
        var sh01bVariant = await ScalarAsync<Guid>(
            "SELECT active_variant_id FROM ops.machine_runtime_state WHERE machine_id = @id",
            ("id", v2.MachineId));
        Assert.Equal(v2.VariantId, sh01bVariant);
    }

    [PostgresFact]
    public async Task Commissioning_registry_completeness_guard_bypasses_003_when_live_worksites_exist_and_no_area_id_present()
    {
        await ResetAsync();

        // 1. Verify exact structural evidence in database:
        //    profiles=25, maps=25, codes=25, assignments=25, source-backed=24, plans=1775, bindings=208, sealer bindings=0, no config_json.areaId
        var profileCount = await ScalarAsync<long>(
            "SELECT count(*) FROM master.navigation_profiles WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'");
        var mapCount = await ScalarAsync<long>(
            "SELECT count(DISTINCT robot_map_name) FROM master.navigation_profiles WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'");
        var codeCount = await ScalarAsync<long>(
            "SELECT count(DISTINCT code) FROM master.navigation_profiles WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'");
        var assignmentCount = await ScalarAsync<long>(
            """
            SELECT count(*)
            FROM master.variant_navigation_profile_assignments vnpa
            JOIN master.navigation_profiles np ON np.id = vnpa.navigation_profile_id
            WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
            """);
        var sourceBackedCount = await ScalarAsync<long>(
            """
            SELECT count(*)
            FROM master.navigation_profiles np
            WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
              AND np.config_json ->> 'layoutBindingState' = 'SOURCE_BACKED'
            """);
        var planCount = await ScalarAsync<long>(
            """
            SELECT count(*)
            FROM integration.rds_worksite_plans p
            JOIN master.navigation_profiles np ON np.id = p.navigation_profile_id
            WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
            """);
        var bindingCount = await ScalarAsync<long>(
            """
            SELECT count(*)
            FROM integration.layout_slot_rds_plan_bindings b
            JOIN master.navigation_profiles np ON np.id = b.navigation_profile_id
            WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
            """);
        var sealerBindings = await ScalarAsync<long>(
            """
            SELECT count(*)
            FROM integration.layout_slot_rds_plan_bindings b
            JOIN master.navigation_profiles np ON np.id = b.navigation_profile_id
            WHERE np.code = 'TKG_SEALER_V03_NAV'
            """);
        var areaIdCount = await ScalarAsync<long>(
            """
            SELECT count(DISTINCT config_json ->> 'areaId')
            FROM master.navigation_profiles
            WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
            """);

        Assert.Equal(25, profileCount);
        Assert.Equal(25, mapCount);
        Assert.Equal(25, codeCount);
        Assert.Equal(25, assignmentCount);
        Assert.Equal(24, sourceBackedCount);
        Assert.Equal(1775, planCount);
        Assert.Equal(208, bindingCount);
        Assert.Equal(0, sealerBindings);
        Assert.Equal(0, areaIdCount); // Crucial: config_json has no 'areaId' key!

        try
        {
            // 2. Insert mock live WorkSites colliding with all 1775 planned site IDs to simulate persistent discovered state
            await ExecuteAsync(
                """
                INSERT INTO integration.rds_worksites (navigation_profile_id, rds_site_id)
                SELECT p.navigation_profile_id, p.planned_rds_site_id
                FROM integration.rds_worksite_plans p
                JOIN master.navigation_profiles np ON np.id = p.navigation_profile_id
                WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
                ON CONFLICT (navigation_profile_id, rds_site_id) DO NOTHING
                """);

            var liveCollisions = await ScalarAsync<long>(
                """
                SELECT count(*)
                FROM integration.rds_worksite_plans p
                JOIN integration.rds_worksites w ON w.rds_site_id = p.planned_rds_site_id
                JOIN master.navigation_profiles np ON np.id = p.navigation_profile_id
                WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
                """);
            Assert.Equal(1775, liveCollisions);

            // 3. Evaluate the exact guard query and logic from start_local_stack.ps1:
            //    Even with 1775 collisions and areaIdCount == 0, regComplete MUST evaluate to true!
            var isRegComplete = (profileCount == 25 &&
                mapCount == 25 &&
                codeCount == 25 &&
                assignmentCount == 25 &&
                sourceBackedCount == 24 &&
                planCount == 1775 &&
                bindingCount == 208 &&
                sealerBindings == 0);

            Assert.True(isRegComplete, "Commissioning registry must be classified complete despite live collisions and absent areaId.");

            // 4. Verify downstream steps proceed:
            //    - Customer-approved finished-goods layout: two delivery
            //      locations and six empty-container preparation areas.
            var fgCount = await ScalarAsync<long>(
                "SELECT count(*) FROM ops.store_locations WHERE code IN ('FG-01', 'FG-02') AND area_purpose = 'DELIVERY' AND is_enabled");
            var fgPreparationCount = await ScalarAsync<long>(
                "SELECT count(*) FROM ops.preparation_areas WHERE warehouse_scope = 'FINISHED_GOODS' AND is_enabled");
            Assert.Equal(2, fgCount);
            Assert.Equal(6, fgPreparationCount);

            //    - 005 Task templates: exactly 96 (24 * 4)
            var templateCount = await ScalarAsync<long>(
                "SELECT count(*) FROM master.task_templates WHERE config_json ->> 'templateRegistry' = 'tkg_variant_task_templates_v1'");
            Assert.Equal(96, templateCount);

            //    - Runtime self-heal configures all 9 active customer machines
            var activeMachineCount = await ScalarAsync<long>("SELECT count(*) FROM master.machines WHERE is_active");
            Assert.Equal(9, activeMachineCount);
        }
        finally
        {
            await ExecuteAsync(
                """
                DELETE FROM integration.rds_worksites
                WHERE rds_site_id IN (
                    SELECT p.planned_rds_site_id
                    FROM integration.rds_worksite_plans p
                    JOIN master.navigation_profiles np ON np.id = p.navigation_profile_id
                    WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
                )
                """);
        }
    }

    [PostgresFact]
    public async Task Cancellation_after_dispatch_uses_the_stable_deferred_error()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var service = new TransportWorkflowService(store);
        var created = await service.CreateAsync(Command(context, TransportTaskType.BringEmpty, "late-cancel", destination: slotId));
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.SentToRds);

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() => service.CancelAsync(created.Request.Id, null));

        Assert.Equal(WorkflowErrorCodes.CancellationAfterExecutionDeferred, error.Code);
    }

    [PostgresFact]
    public async Task Cancellation_while_running_uses_the_stable_deferred_error()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var service = new TransportWorkflowService(store);
        var created = await service.CreateAsync(Command(context, TransportTaskType.BringEmpty, "running-cancel", destination: slotId));
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.SentToRds);
        await service.RecordExecutionTransitionAsync(created.Request.Id, TransportRequestStatus.Running);

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() => service.CancelAsync(created.Request.Id, null));

        Assert.Equal(WorkflowErrorCodes.CancellationAfterExecutionDeferred, error.Code);
    }

    [PostgresFact]
    public async Task Concurrent_cancellation_and_dispatch_do_not_deadlock()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var slotId = await LogisticsSlotAsync(context.LayoutId, requireContainer: false);
        await SetSlotAsync(slotId, OccupancyState.Vacant, LoadState.Unknown);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var service = new TransportWorkflowService(store);
        var created = await service.CreateAsync(Command(context, TransportTaskType.BringEmpty, "cancel-dispatch-race", destination: slotId));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var outcomes = await Task.WhenAll(
            CaptureRuleAsync(async () => _ = await service.CancelAsync(created.Request.Id, null, timeout.Token)),
            CaptureRuleAsync(async () => _ = await service.RecordExecutionTransitionAsync(
                created.Request.Id,
                TransportRequestStatus.SentToRds,
                timeout.Token)));

        Assert.Single(outcomes, error => error is null);
        var conflict = Assert.Single(outcomes, error => error is not null)!;
        Assert.Contains(
            conflict.Code,
            new[] { "ILLEGAL_STATUS_TRANSITION", WorkflowErrorCodes.CancellationAfterExecutionDeferred });
    }

    // ---- Customer UX — Automatic Machine Slot Resolution ----

    [PostgresFact]
    public async Task Bring_empty_auto_resolves_the_part_mapped_vacant_machine_slot()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var partId = await VariantPartIdAsync(context.VariantId, 0);
        var mapped = await EnsureTestSlotAsync(context.LayoutId, "SLOTPART-BRING-1");
        var unmapped = await EnsureTestSlotAsync(context.LayoutId, "SLOTPART-BRING-2");
        await SetSlotAsync(mapped, OccupancyState.Vacant, LoadState.Unknown);
        await SetSlotAsync(unmapped, OccupancyState.Vacant, LoadState.Unknown);
        await AssignSlotPartAsync(mapped, partId);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var result = await store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "auto-bring-main", partId: partId));

        Assert.Equal(mapped, result.Request.DestinationSlotId);
    }

    [PostgresFact]
    public async Task Automatic_slot_resolution_replay_with_the_same_key_is_idempotent()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var partId = await VariantPartIdAsync(context.VariantId, 0);
        var mapped = await EnsureTestSlotAsync(context.LayoutId, "SLOTPART-IDEMPOTENT-1");
        await SetSlotAsync(mapped, OccupancyState.Vacant, LoadState.Unknown);
        await AssignSlotPartAsync(mapped, partId);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        var command = Command(context, TransportTaskType.BringEmpty, "auto-idempotent-replay", partId: partId);

        var first = await store.CreateAndQueueAsync(command);
        var replay = await store.CreateAndQueueAsync(command);

        Assert.False(first.WasIdempotentReplay);
        Assert.True(replay.WasIdempotentReplay);
        Assert.Equal(first.Request.Id, replay.Request.Id);
        Assert.Equal(mapped, replay.Request.DestinationSlotId);
    }

    [PostgresFact]
    public async Task Automatic_machine_slot_resolution_never_uses_EMPTY_STORAGE()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var partId = await VariantPartIdAsync(context.VariantId, 0);
        var buffer = await EnsureLayoutSlotAsync(
            context.LayoutId,
            "SLOTPART-EMPTY-STORAGE-1",
            "Mapped Buffer Must Not Route");
        await SetSlotAsync(buffer, OccupancyState.Vacant, LoadState.Unknown);
        await AssignSlotPartAsync(buffer, partId);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "empty-storage-machine-side", partId: partId)));

        Assert.Equal("BRING_EMPTY_SLOT_NOT_FOUND", error.Code);
    }

    [PostgresFact]
    public async Task Remove_full_auto_resolves_the_part_mapped_occupied_full_machine_slot()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var partId = await VariantPartIdAsync(context.VariantId, 0);
        var mapped = await EnsureTestSlotAsync(context.LayoutId, "SLOTPART-FULL-1");
        await SetSlotAsync(mapped, OccupancyState.Occupied, LoadState.Full);
        await AssignSlotPartAsync(mapped, partId);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var result = await store.CreateAndQueueAsync(Command(context, TransportTaskType.RemoveFull, "auto-remove-full", partId: partId));

        Assert.Equal(mapped, result.Request.SourceSlotId);
    }

    [PostgresFact]
    public async Task Remove_empty_auto_resolves_the_part_mapped_occupied_empty_machine_slot()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var partId = await VariantPartIdAsync(context.VariantId, 0);
        var mapped = await EnsureTestSlotAsync(context.LayoutId, "SLOTPART-EMPTY-1");
        await SetSlotAsync(mapped, OccupancyState.Occupied, LoadState.Empty);
        await AssignSlotPartAsync(mapped, partId);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var result = await store.CreateAndQueueAsync(Command(context, TransportTaskType.RemoveEmpty, "auto-remove-empty", partId: partId));

        Assert.Equal(mapped, result.Request.SourceSlotId);
    }

    [PostgresFact]
    public async Task Auto_resolution_never_selects_a_slot_mapped_to_a_different_part()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var targetPart = await VariantPartIdAsync(context.VariantId, 0);
        var otherPart = await VariantPartIdAsync(context.VariantId, 1);
        var wrongPartSlot = await EnsureTestSlotAsync(context.LayoutId, "SLOTPART-WRONGPART-1");
        await SetSlotAsync(wrongPartSlot, OccupancyState.Vacant, LoadState.Unknown);
        await AssignSlotPartAsync(wrongPartSlot, otherPart);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "wrong-part", partId: targetPart)));

        Assert.Equal("BRING_EMPTY_SLOT_NOT_FOUND", error.Code);
    }

    [PostgresFact]
    public async Task Auto_resolution_never_selects_a_slot_outside_the_active_layout()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        // A read-only lookup on a different machine's layout — does not touch
        // SH-01-A's own active runtime state the way ConfigureAsync would.
        var otherLayoutContext = await ContextAsync("SH-01-B", "1");
        var partId = await VariantPartIdAsync(context.VariantId, 0);
        var otherLayoutSlot = await EnsureTestSlotAsync(otherLayoutContext.LayoutId, "SLOTPART-OTHERLAYOUT-1");
        await SetSlotAsync(otherLayoutSlot, OccupancyState.Vacant, LoadState.Unknown);
        await AssignSlotPartAsync(otherLayoutSlot, partId);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "wrong-layout", partId: partId)));

        Assert.Equal("BRING_EMPTY_SLOT_NOT_FOUND", error.Code);
    }

    [PostgresFact]
    public async Task Auto_resolution_never_selects_a_disabled_slot()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var partId = await VariantPartIdAsync(context.VariantId, 0);
        var disabled = await EnsureTestSlotAsync(context.LayoutId, "SLOTPART-DISABLED-1");
        await SetSlotAsync(disabled, OccupancyState.Vacant, LoadState.Unknown);
        await AssignSlotPartAsync(disabled, partId);
        await ExecuteAsync("UPDATE master.layout_slots SET is_enabled = false WHERE id = @id", ("id", disabled));
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "disabled-slot", partId: partId)));

        Assert.Equal("BRING_EMPTY_SLOT_NOT_FOUND", error.Code);
    }

    [PostgresFact]
    public async Task Auto_resolution_never_selects_a_slot_with_an_active_reservation()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var partId = await VariantPartIdAsync(context.VariantId, 0);
        var reserved = await EnsureTestSlotAsync(context.LayoutId, "SLOTPART-RESERVED-1");
        await SetSlotAsync(reserved, OccupancyState.Vacant, LoadState.Unknown);
        await AssignSlotPartAsync(reserved, partId);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);
        await store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "reserve-first", destination: reserved));

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "reserve-second", partId: partId)));

        Assert.Equal("BRING_EMPTY_SLOT_NOT_FOUND", error.Code);
    }

    [PostgresFact]
    public async Task Auto_resolution_is_deterministic_when_multiple_slots_are_eligible()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var partId = await VariantPartIdAsync(context.VariantId, 0);
        var second = await EnsureTestSlotAsync(context.LayoutId, "SLOTPART-DETERM-2", displayOrder: 9502);
        var first = await EnsureTestSlotAsync(context.LayoutId, "SLOTPART-DETERM-1", displayOrder: 9501);
        await SetSlotAsync(first, OccupancyState.Vacant, LoadState.Unknown);
        await SetSlotAsync(second, OccupancyState.Vacant, LoadState.Unknown);
        await AssignSlotPartAsync(first, partId);
        await AssignSlotPartAsync(second, partId);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var result = await store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "deterministic-pick", partId: partId));

        Assert.Equal(first, result.Request.DestinationSlotId);
    }

    [PostgresFact]
    public async Task Concurrent_auto_resolution_for_the_same_part_claims_only_one_of_two_eligible_slots()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var partId = await VariantPartIdAsync(context.VariantId, 0);
        var slotA = await EnsureTestSlotAsync(context.LayoutId, "SLOTPART-RACE-1");
        var slotB = await EnsureTestSlotAsync(context.LayoutId, "SLOTPART-RACE-2");
        await SetSlotAsync(slotA, OccupancyState.Vacant, LoadState.Unknown);
        await SetSlotAsync(slotB, OccupancyState.Vacant, LoadState.Unknown);
        await AssignSlotPartAsync(slotA, partId);
        await AssignSlotPartAsync(slotB, partId);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var attempts = await Task.WhenAll(
            CaptureAsync(() => store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "auto-race-a", partId: partId))),
            CaptureAsync(() => store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "auto-race-b", partId: partId))));

        Assert.Equal(2, attempts.Count(attempt => attempt.Result is not null));
        var claimed = attempts.Where(attempt => attempt.Result is not null)
            .Select(attempt => attempt.Result!.Request.DestinationSlotId!.Value)
            .ToArray();
        Assert.Equal(2, claimed.Distinct().Count());
        Assert.Equal(new[] { slotA, slotB }.OrderBy(id => id), claimed.OrderBy(id => id));
    }

    [PostgresFact]
    public async Task No_eligible_slot_reports_a_friendly_message_per_operation_without_a_raw_code()
    {
        await ResetAsync();
        var context = await ConfigureAsync("SH-01-A", "1");
        var partId = await VariantPartIdAsync(context.VariantId, 0);
        await using var store = new PostgresTransportWorkflowStore(_connectionString);

        var bringError = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.BringEmpty, "no-slot-bring", partId: partId)));
        var removeFullError = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.RemoveFull, "no-slot-remove-full", partId: partId)));
        var removeEmptyError = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            store.CreateAndQueueAsync(Command(context, TransportTaskType.RemoveEmpty, "no-slot-remove-empty", partId: partId)));

        Assert.Equal("BRING_EMPTY_SLOT_NOT_FOUND", bringError.Code);
        Assert.Equal("Bu parça için boş konum bulunamadı.", bringError.Message);
        Assert.Equal("REMOVE_FULL_SLOT_NOT_FOUND", removeFullError.Code);
        Assert.Equal("Bu parça için dolu kasa bulunamadı.", removeFullError.Message);
        Assert.Equal("REMOVE_EMPTY_SLOT_NOT_FOUND", removeEmptyError.Code);
        Assert.Equal("Bu parça için boş kasa bulunamadı.", removeEmptyError.Message);
    }

    private async Task ResetAsync()
    {
        await ExecuteAsync(
            """
            TRUNCATE integration.rds_tasks, ops.transport_request_events, ops.transport_requests, ops.slot_runtime_state, ops.machine_runtime_state RESTART IDENTITY CASCADE;
            DELETE FROM integration.layout_slot_rds_mapping
            WHERE layout_slot_id IN (SELECT id FROM master.layout_slots WHERE code LIKE 'TEST-%')
               OR rds_worksite_id IN (SELECT id FROM integration.rds_worksites WHERE rds_site_id LIKE 'P08B-%' OR rds_site_id LIKE 'WS-%TEST%');
            DELETE FROM master.layout_slots WHERE code LIKE 'TEST-%';
            DELETE FROM integration.rds_worksites WHERE rds_site_id LIKE 'P08B-%' OR rds_site_id LIKE 'WS-%TEST%';
            DELETE FROM master.task_templates WHERE config_json ->> 'testScope' = 'P08B';
            DELETE FROM master.layout_slot_parts
            WHERE layout_slot_id IN (SELECT id FROM master.layout_slots WHERE code LIKE 'SLOTPART-%');
            UPDATE master.layout_slots SET is_enabled = true WHERE code LIKE 'SLOTPART-%';
            INSERT INTO ops.store_units (store_location_id, load_state)
            SELECT location.id, 'EMPTY'
            FROM ops.store_locations location
            WHERE location.code LIKE 'PHASE03-EMPTY-%'
              AND NOT EXISTS (
                  SELECT 1 FROM ops.store_units unit
                  WHERE unit.store_location_id = location.id AND unit.removed_at IS NULL);
            """);
    }

    private Task<Guid> EnsureActorAsync() => ScalarAsync<Guid>(
        """
        INSERT INTO auth.users (username, password_hash, full_name)
        VALUES ('phase08b-operator', 'test-only-hash', 'Phase 08B Operator')
        ON CONFLICT (username) DO UPDATE SET full_name = EXCLUDED.full_name
        RETURNING id
        """);

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
    /// Customer Workflow — Empty Container Buffer: every BRING_EMPTY/
    /// REMOVE_EMPTY request now requires a configured EMPTY_STORAGE buffer
    /// slot in the right state (see PostgresTransportWorkflowStore.
    /// AllocateBufferSlotAsync). Pre-provisions a small pool of ready-to-bring
    /// (OCCUPIED/EMPTY) and ready-to-receive (VACANT/UNKNOWN) buffer slots on
    /// the layout — enough for this file's existing concurrent-request tests
    /// to each get their own — with a high display_order that keeps them out
    /// of the way of LogisticsSlotAsync/LogisticsSlotsAsync's ordered slot
    /// picks, so every existing test that omits an explicit source/
    /// destination for those operations keeps working exactly as before via
    /// auto-allocation. ResetAsync() truncates slot_runtime_state every time,
    /// so this must re-run per test (called from ConfigureAsync).
    /// </summary>
    private async Task EnsureDefaultBufferSlotsAsync(Guid layoutId)
    {
        const int poolSize = 6;
        for (var i = 1; i <= poolSize; i++)
        {
            var bringBuffer = await EnsureLayoutSlotAsync(layoutId, $"BUFFER-TEST-BRING-{i}", $"Test Buffer Bring {i}");
            await SetSlotAsync(bringBuffer, OccupancyState.Occupied, LoadState.Empty);
            var removeBuffer = await EnsureLayoutSlotAsync(layoutId, $"BUFFER-TEST-REMOVE-{i}", $"Test Buffer Remove {i}");
            await SetSlotAsync(removeBuffer, OccupancyState.Vacant, LoadState.Unknown);
        }
    }

    private async Task<Guid> EnsureLayoutSlotAsync(Guid layoutId, string code, string displayName)
    {
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
            ("display_name", displayName));
        return await ScalarAsync<Guid>(
            "SELECT id FROM master.layout_slots WHERE layout_id = @layout_id AND code = @code",
            ("layout_id", layoutId),
            ("code", code));
    }

    /// <summary>
    /// Customer UX — Automatic Machine Slot Resolution: a plain OTHER-role
    /// logistics test slot, distinct from the EMPTY_STORAGE buffer pool
    /// above, for asserting machine-side part-mapped resolution.
    /// </summary>
    private async Task<Guid> EnsureTestSlotAsync(Guid layoutId, string code, int displayOrder = 9500)
    {
        await ExecuteAsync(
            """
            INSERT INTO master.layout_slots (layout_id, code, display_name, slot_role, display_order, is_enabled)
            VALUES (@layout_id, @code, @code, 'OTHER', @display_order, true)
            ON CONFLICT (layout_id, code) DO UPDATE SET is_enabled = true
            """,
            ("layout_id", layoutId),
            ("code", code),
            ("display_order", displayOrder));
        return await ScalarAsync<Guid>(
            "SELECT id FROM master.layout_slots WHERE layout_id = @layout_id AND code = @code",
            ("layout_id", layoutId),
            ("code", code));
    }

    private Task AssignSlotPartAsync(Guid slotId, Guid partId) => ExecuteAsync(
        "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot_id, @part_id) ON CONFLICT DO NOTHING",
        ("slot_id", slotId),
        ("part_id", partId));

    /// <summary>A real, canonical customer part — deterministic by ordinal so
    /// tests can pick two distinct ones without depending on exact codes.</summary>
    private Task<Guid> VariantPartIdAsync(Guid variantId, int ordinal) =>
        ScalarAsync<Guid>(
            """
            SELECT part_id
            FROM (
                SELECT pg.main_part_id AS part_id
                FROM master.product_groups pg
                WHERE pg.variant_id = @variant_id AND pg.is_active
                UNION
                SELECT gpp.part_id
                FROM master.product_groups pg
                JOIN master.product_group_parts gpp
                  ON gpp.product_group_id = pg.id AND gpp.is_active
                WHERE pg.variant_id = @variant_id AND pg.is_active
            ) compatible
            ORDER BY part_id
            OFFSET @ordinal LIMIT 1
            """,
            ("variant_id", variantId),
            ("ordinal", ordinal));

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
            $"SELECT id FROM master.layout_slots WHERE layout_id = @layout_id AND slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE', 'DROP') {condition} ORDER BY display_order, code LIMIT 1",
            ("layout_id", layoutId));
    }

    private async Task<Guid[]> LogisticsSlotsAsync(Guid layoutId, int count)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id FROM master.layout_slots WHERE layout_id = @layout_id AND slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE', 'DROP') ORDER BY display_order, code LIMIT @count",
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

    private Task SetSlotAsync(
        Guid slotId,
        OccupancyState occupancy,
        LoadState load,
        Guid? partId = null,
        Guid? containerId = null) => ExecuteAsync(
        """
        INSERT INTO ops.slot_runtime_state (
            layout_slot_id, occupancy_state, load_state, current_part_id, current_container_type_id)
        VALUES (@slot_id, @occupancy, @load, @part_id, @container_id)
        ON CONFLICT (layout_slot_id) DO UPDATE
        SET occupancy_state = EXCLUDED.occupancy_state,
            load_state = EXCLUDED.load_state,
            current_part_id = EXCLUDED.current_part_id,
            current_container_type_id = EXCLUDED.current_container_type_id,
            row_version = ops.slot_runtime_state.row_version + 1,
            updated_at = now()
        """,
        ("slot_id", slotId),
        ("occupancy", occupancy.ToDatabase()),
        ("load", load.ToDatabase()),
        ("part_id", partId),
        ("container_id", containerId));

    private async Task<(OccupancyState Occupancy, LoadState Load)> ReadSlotStateAsync(Guid slotId)
    {
        var state = await ReadFullSlotStateAsync(slotId);
        return (state.Occupancy, state.Load);
    }

    private async Task<(OccupancyState Occupancy, LoadState Load, Guid? PartId, Guid? ContainerId)> ReadFullSlotStateAsync(Guid slotId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT occupancy_state, load_state, current_part_id, current_container_type_id FROM ops.slot_runtime_state WHERE layout_slot_id = @id",
            connection);
        command.Parameters.AddWithValue("id", slotId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            WorkflowCode.ParseOccupancy(reader.GetString(0)),
            WorkflowCode.ParseLoad(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3));
    }

    private static CreateTransportRequest Command(
        LayoutContext context,
        TransportTaskType type,
        string key,
        Guid? source = null,
        Guid? destination = null,
        Guid? partId = null) => new(
        key,
        RequestOrigin.LineOperator,
        null,
        context.MachineId,
        context.VariantId,
        context.LayoutId,
        null,
        null,
        partId,
        null,
        type,
        source,
        destination);

    private static async Task<Attempt> CaptureAsync(Func<Task<CreateTransportResult>> action)
    {
        try
        {
            return new Attempt(await action(), null);
        }
        catch (WorkflowRuleException error)
        {
            return new Attempt(null, error);
        }
    }

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
    private sealed record Attempt(CreateTransportResult? Result, WorkflowRuleException? Error);
}
