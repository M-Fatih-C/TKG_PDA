using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Infrastructure.Postgres;

public sealed class PostgresRdsExecutionStore(NpgsqlDataSource dataSource, RdsPhysicalAvailabilityPolicy? physicalPolicy = null) : IRdsExecutionStore
{
    public async Task<string?> ReadPhysicalDispatchBlockAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT r.navigation_id,r.destination_business_code,r.source_business_code,tt.code
            FROM ops.transport_requests r JOIN master.task_types tt ON tt.id=r.task_type_id WHERE r.id=@id
            """,connection);
        command.Parameters.AddWithValue("id",requestId);
        Guid? nav = null; string? destinationBusinessCode = null; string? sourceBusinessCode = null; string? operation = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken))
            { nav=reader.IsDBNull(0) ? null : reader.GetGuid(0); destinationBusinessCode=Text(reader,1); sourceBusinessCode=Text(reader,2); operation=Text(reader,3); }
        if (nav is not { } profileId) return null;
        var hasPreparedSource = false;
        if (operation == "BRING_EMPTY")
        {
            await using var prepared = new NpgsqlCommand("""
                SELECT 1 FROM ops.preparation_pallets
                WHERE transport_request_id = @id AND released_at IS NULL AND ready_at IS NOT NULL
                LIMIT 1
                """, connection);
            prepared.Parameters.AddWithValue("id", requestId);
            hasPreparedSource = await prepared.ExecuteScalarAsync(cancellationToken) is not null;
        }
        if (hasPreparedSource)
            return await ReadPreparationPhysicalBlockAsync(connection, requestId, cancellationToken);

        var businessCode = operation == "BRING_EMPTY" ? sourceBusinessCode : destinationBusinessCode;
        if (businessCode is null) return null;
        var endpointClass=operation=="BRING_EMPTY" ? "EMPTY_SOURCE"
            : operation=="REMOVE_EMPTY" ? "EMPTY_RETURN"
            : businessCode.StartsWith("FG-",StringComparison.Ordinal) ? "FINISHED_GOODS" : "NON_FINISHED_RETURN";
        var observations=await PostgresTransportWorkflowStore.ReadSharedCandidatesAsync(connection,null,profileId,endpointClass,cancellationToken);
        var candidate=observations.SingleOrDefault(c=>c.ProfileId==profileId && c.BusinessCode==businessCode);
        if (candidate is null) return "RDS_WORKSITE_NOT_MAPPED";
        if (!candidate.StoreEnabled) return endpointClass == "EMPTY_SOURCE"
            ? "FINISHED_GOODS_NO_EMPTY_CONTAINER"
            : "FINISHED_GOODS_DESTINATION_NOT_CONFIGURED";
        var policy=physicalPolicy ?? new RdsPhysicalAvailabilityPolicy();
        if (endpointClass == "EMPTY_SOURCE")
        {
            var state = policy.State(candidate.Evidence);
            var sourceAvailable = policy.IsSimulationMode ? state == "AVAILABLE" : state == "OCCUPIED";
            if (!sourceAvailable)
                return !policy.IsSimulationMode && state == "AVAILABLE"
                    ? "RDS_WORKSITE_OCCUPANCY_CONFLICT"
                    : RdsPhysicalAvailabilityPolicy.ErrorCode(state) ?? "RDS_WORKSITE_STATE_UNKNOWN";
            if (!policy.IsSimulationMode && observations.Any(other => other.BusinessCode == businessCode
                && other.ProfileId != profileId && policy.State(other.Evidence) == "AVAILABLE"))
                return "RDS_WORKSITE_OCCUPANCY_CONFLICT";

            await using var reservation = new NpgsqlCommand("""
                SELECT 1
                FROM ops.store_reservations sr
                JOIN ops.store_units u ON u.id = sr.store_unit_id
                JOIN ops.store_locations sl ON sl.id = sr.store_location_id AND sl.id = u.store_location_id
                JOIN integration.profile_shared_endpoint_bindings b
                  ON b.navigation_profile_id = @profile
                 AND b.endpoint_class = 'EMPTY_SOURCE'
                 AND b.business_code = @code
                 AND (b.store_location_id = sl.id OR (b.store_location_id IS NULL AND b.business_code = sl.code))
                WHERE sr.transport_request_id = @request
                  AND sr.reservation_type = 'PICKUP'
                  AND sr.released_at IS NULL
                  AND u.load_state = 'EMPTY'
                  AND u.removed_at IS NULL
                """, connection);
            reservation.Parameters.AddWithValue("profile", profileId);
            reservation.Parameters.AddWithValue("code", businessCode);
            reservation.Parameters.AddWithValue("request", requestId);
            return await reservation.ExecuteScalarAsync(cancellationToken) is null
                ? "STORE_PICKUP_RESERVATION_MISSING"
                : null;
        }
        return PostgresTransportWorkflowStore.SharedPhysicalAvailability(candidate,observations,policy) ? null
            : RdsPhysicalAvailabilityPolicy.ErrorCode(policy.State(candidate.Evidence)) ?? "RDS_WORKSITE_OCCUPANCY_CONFLICT";
    }

    private const string RequestSelect =
        """
        SELECT r.id, r.request_no, COALESCE(r.idempotency_key, ''), r.status, tt.code, r.request_origin,
               r.requested_by, r.machine_id, r.variant_id, r.layout_id, r.navigation_id,
               r.product_group_id, r.part_id, r.container_type_id, r.source_slot_id,
               r.destination_slot_id, r.priority, r.readiness_state, r.ready_at
        FROM ops.transport_requests r
        JOIN master.task_types tt ON tt.id = r.task_type_id
        """;

    private const string TaskProjectionSelect =
        """
        SELECT rt.transport_request_id, rt.rds_task_record_id, rt.rds_task_label,
               rt.rds_status, rt.agv_id, rt.out_order_no, rt.ended_reason,
               rt.request_payload_json::text, rt.response_payload_json::text,
               rt.last_status_json::text,
               (rt.response_payload_json ? 'cancellationRequestedAt') AS cancellation_requested,
               tr.status
        FROM integration.rds_tasks rt
        JOIN ops.transport_requests tr ON tr.id = rt.transport_request_id
        """;

    public async Task<IReadOnlyList<Guid>> ListQueuedRequestIdsAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT id
            FROM ops.transport_requests
            WHERE status = 'QUEUED'
              AND readiness_state <> 'WAITING_PREPARATION'
              AND machine_slot_reservation_active
              AND store_endpoint_reservation_active
              AND NOT EXISTS (
                  SELECT 1
                  FROM ops.transport_request_dependencies d
                  JOIN ops.transport_requests pred ON pred.id = d.predecessor_request_id
                  WHERE d.successor_request_id = ops.transport_requests.id
                    AND pred.status <> 'COMPLETED')
            ORDER BY priority, queued_at, id
            LIMIT @limit
            """,
            connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        return ids;
    }

    public async Task<IReadOnlyList<RdsTaskProjection>> ListActiveTasksAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            TaskProjectionSelect +
            " WHERE tr.status IN ('SENT_TO_RDS','RUNNING') ORDER BY rt.last_sync_at NULLS FIRST, rt.id LIMIT @limit",
            connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        var rows = new List<RdsTaskProjection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadTaskProjection(reader));
        return rows;
    }

    public async Task<IReadOnlyList<Guid>> ListPendingWorkSiteRefreshRequestIdsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT rt.transport_request_id
            FROM integration.rds_tasks rt
            JOIN ops.transport_requests tr ON tr.id = rt.transport_request_id
            WHERE rt.rds_status IN (1001, 1003, 1004, 1005, 1006)
              AND tr.navigation_id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM audit.audit_logs a
                  WHERE a.entity_type = 'TRANSPORT_REQUEST'
                    AND a.entity_id = rt.transport_request_id::text
                    AND a.action IN ('RDS_WORKSITE_REFRESHED', 'RDS_WORKSITE_REFRESH_SKIPPED'))
            ORDER BY rt.last_sync_at NULLS FIRST, rt.transport_request_id
            LIMIT @limit
            """,
            connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        return ids;
    }

    public async Task<IRdsExecutionLease?> TryAcquireRequestLeaseAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_try_advisory_lock(hashtextextended(@key, 0))",
                connection);
            command.Parameters.AddWithValue("key", $"tkg:rds:{requestId:D}");
            var acquired = (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
            if (acquired)
            {
                return new PostgresRdsExecutionLease(connection, requestId);
            }

            // A contended lease previously returned null without disposing, so the
            // connection was never returned to the pool.
            await connection.DisposeAsync();
            return null;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Robot-scoped sibling of <see cref="TryAcquireRequestLeaseAsync"/>, using the
    /// same proven primitive: a SESSION-level advisory lock held on a dedicated
    /// connection for the whole critical section, so the lock outlives any transaction
    /// inside it and is still held while the RDS call is made. A transaction-scoped
    /// advisory lock would be released at commit, before the mutation.
    ///
    /// The key string carries its own namespace prefix ("tkg:robot:" rather than
    /// "tkg:rds:"), so a robot key can never collide with a request key even if a
    /// robot were somehow named like a GUID.
    /// </summary>
    public async Task<IRdsExecutionLease?> TryAcquireRobotLeaseAsync(string robotLeaseKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(robotLeaseKey))
        {
            // Fail closed: an unidentifiable robot must not silently proceed unlocked.
            return null;
        }

        var key = $"tkg:robot:{robotLeaseKey.Trim().ToLowerInvariant()}";
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_try_advisory_lock(hashtextextended(@key, 0))",
                connection);
            command.Parameters.AddWithValue("key", key);
            var acquired = (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
            if (acquired)
            {
                return new PostgresRdsAdvisoryLease(connection, key);
            }

            await connection.DisposeAsync();
            return null;
        }
        catch
        {
            // Includes cancellation: the connection is returned to the pool without a
            // dangling session lock.
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<RdsDispatchContext?> GetDispatchContextAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT r.id, r.request_no, COALESCE(r.idempotency_key, ''), r.status, tt.code, r.request_origin,
                   r.requested_by, r.machine_id, r.variant_id, r.layout_id, r.navigation_id,
                   r.product_group_id, r.part_id, r.container_type_id, r.source_slot_id,
                   r.destination_slot_id, r.priority, r.readiness_state, r.ready_at,
                   m.code, v.code, main_part.part_code, p.part_code, ct.code,
                   template.id, template.rds_task_label, template.priority, template.config_json::text,
                   COALESCE(src.slot_id, prep.slot_id), COALESCE(src.slot_code, prep.slot_code),
                   COALESCE(src.slot_group_code, prep.slot_group_code), COALESCE(src.slot_role, prep.slot_role),
                   COALESCE(src.site_id, prep.site_id), COALESCE(src.rds_type, prep.rds_type),
                   COALESCE(src.last_filled, prep.last_filled), COALESCE(src.last_locked, prep.last_locked),
                   COALESCE(src.last_disabled, prep.last_disabled),
                   COALESCE(src.last_sync_failed, prep.last_sync_failed),
                   COALESCE(src.last_sync_at, prep.last_sync_at),
                   dst.slot_id, dst.slot_code, dst.slot_group_code, dst.slot_role, dst.site_id,
                   dst.rds_type, dst.last_filled, dst.last_locked, dst.last_disabled,
                   dst.last_sync_failed, dst.last_sync_at
            FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            JOIN master.machines m ON m.id = r.machine_id
            JOIN master.variants v ON v.id = r.variant_id
            LEFT JOIN master.product_groups pg ON pg.id = r.product_group_id
            LEFT JOIN master.parts main_part ON main_part.id = pg.main_part_id
            LEFT JOIN master.parts p ON p.id = r.part_id
            LEFT JOIN master.container_types ct ON ct.id = r.container_type_id
            LEFT JOIN LATERAL (
                SELECT t.*
                FROM master.task_templates t
                WHERE t.variant_id = r.variant_id
                  AND t.task_type_id = r.task_type_id
                  AND t.is_active
                  AND (t.product_group_id = r.product_group_id OR t.product_group_id IS NULL)
                ORDER BY (t.product_group_id = r.product_group_id) DESC NULLS LAST, t.id
                LIMIT 1
            ) template ON true
            LEFT JOIN LATERAL (
                SELECT ls.id AS slot_id, ls.code AS slot_code, sg.code AS slot_group_code,
                       ls.slot_role, rw.rds_site_id AS site_id, rw.rds_type, rw.last_filled,
                       rw.last_locked, rw.last_disabled, rw.last_sync_failed, rw.last_sync_at
                FROM master.layout_slots ls
                LEFT JOIN master.slot_group_members sgm ON sgm.layout_slot_id = ls.id
                LEFT JOIN master.slot_groups sg ON sg.id = sgm.slot_group_id
                LEFT JOIN integration.rds_worksites rw
                    ON rw.id = integration.resolve_slot_worksite(ls.id, r.navigation_id)
                WHERE NOT (tt.code = 'BRING_EMPTY' AND EXISTS (
                          SELECT 1 FROM ops.preparation_pallets prepared
                          WHERE prepared.transport_request_id = r.id
                            AND prepared.released_at IS NULL AND prepared.ready_at IS NOT NULL))
                  AND (
                       (r.source_slot_id IS NOT NULL AND ls.id = r.source_slot_id)
                    OR (r.source_slot_id IS NULL AND template.source_slot_group_id IS NOT NULL
                        AND sgm.slot_group_id = template.source_slot_group_id AND ls.layout_id = r.layout_id)
                    OR (r.source_slot_id IS NULL AND template.source_slot_group_id IS NULL
                        AND ls.layout_id = r.layout_id AND ls.is_enabled
                        AND tt.code = 'BRING_EMPTY' AND ls.slot_role = 'EMPTY_STORAGE')
                  )
                ORDER BY CASE WHEN ls.id = r.source_slot_id THEN 0 ELSE 1 END,
                         sgm.priority, ls.display_order, ls.id
                LIMIT 1
            ) src ON true
            LEFT JOIN LATERAL (
                SELECT slot_id, slot_code, slot_group_code, slot_role, site_id, rds_type,
                       last_filled, last_locked, last_disabled, last_sync_failed, last_sync_at
                FROM (
                    SELECT NULL::uuid AS slot_id, pa.display_name AS slot_code,
                           NULL::varchar AS slot_group_code, 'SHARED'::varchar AS slot_role,
                           rw.rds_site_id AS site_id, rw.rds_type, rw.last_filled,
                           rw.last_locked, rw.last_disabled, rw.last_sync_failed, rw.last_sync_at
                    FROM ops.preparation_pallets pp
                    JOIN ops.preparation_areas pa ON pa.id = pp.preparation_area_id AND pa.is_enabled
                    JOIN integration.preparation_area_rds_mapping pm ON pm.preparation_area_id = pa.id
                    JOIN integration.rds_worksites rw ON rw.id = pm.rds_worksite_id
                                                     AND rw.navigation_profile_id = r.navigation_id
                    WHERE tt.code IN ('BRING_FULL', 'BRING_EMPTY')
                      AND pp.transport_request_id = r.id
                      AND pp.released_at IS NULL
                      AND pp.ready_at IS NOT NULL
                    UNION ALL
                    SELECT NULL::uuid, b.business_code, NULL::varchar, 'SHARED'::varchar,
                           rw.rds_site_id, rw.rds_type, rw.last_filled,
                           rw.last_locked, rw.last_disabled, rw.last_sync_failed, rw.last_sync_at
                    FROM integration.profile_shared_endpoint_bindings b
                    JOIN integration.rds_worksites rw ON rw.id = b.rds_worksite_id
                                                     AND rw.navigation_profile_id = r.navigation_id
                    WHERE tt.code = 'BRING_EMPTY'
                      AND r.source_slot_id IS NULL
                      AND b.navigation_profile_id = r.navigation_id
                      AND b.endpoint_class = 'EMPTY_SOURCE'
                      AND (r.source_business_code IS NULL OR b.business_code = r.source_business_code)
                ) shared_source
                ORDER BY CASE WHEN r.source_business_code IS NOT NULL AND slot_code = r.source_business_code THEN 0 ELSE 1 END, slot_code
                LIMIT 1
            ) prep ON true
            LEFT JOIN LATERAL (
                SELECT slot_id, slot_code, slot_group_code, slot_role, site_id, rds_type,
                       last_filled, last_locked, last_disabled, last_sync_failed, last_sync_at
                FROM (
                    SELECT ls.id AS slot_id, ls.code AS slot_code, sg.code AS slot_group_code,
                           ls.slot_role, rw.rds_site_id AS site_id, rw.rds_type, rw.last_filled,
                           rw.last_locked, rw.last_disabled, rw.last_sync_failed, rw.last_sync_at,
                           CASE WHEN ls.id = r.destination_slot_id THEN 0 ELSE 1 END AS preference,
                           sgm.priority AS member_priority, ls.display_order, ls.id AS sort_id
                    FROM master.layout_slots ls
                    LEFT JOIN master.slot_group_members sgm ON sgm.layout_slot_id = ls.id
                    LEFT JOIN master.slot_groups sg ON sg.id = sgm.slot_group_id
                    LEFT JOIN integration.rds_worksites rw
                        ON rw.id = integration.resolve_slot_worksite(ls.id, r.navigation_id)
                    WHERE r.destination_business_code IS NULL
                      AND (
                           (r.destination_slot_id IS NOT NULL AND ls.id = r.destination_slot_id)
                           OR (r.destination_slot_id IS NULL AND template.destination_slot_group_id IS NOT NULL
                               AND sgm.slot_group_id = template.destination_slot_group_id AND ls.layout_id = r.layout_id)
                           OR (r.destination_slot_id IS NULL AND template.destination_slot_group_id IS NULL
                               AND ls.layout_id = r.layout_id AND ls.is_enabled AND (
                                   (tt.code = 'REMOVE_FULL' AND ls.slot_role = 'DROP')
                                   OR (tt.code = 'REMOVE_EMPTY' AND ls.slot_role = 'EMPTY_STORAGE')
                               ))
                          )
                    UNION ALL
                    SELECT NULL::uuid, b.business_code, NULL::varchar, 'SHARED'::varchar,
                           rw.rds_site_id, rw.rds_type, rw.last_filled,
                           rw.last_locked, rw.last_disabled, rw.last_sync_failed, rw.last_sync_at,
                           0, 0, 0, NULL::uuid
                    FROM integration.profile_shared_endpoint_bindings b
                    JOIN integration.rds_worksites rw ON rw.id = b.rds_worksite_id
                                                     AND rw.navigation_profile_id = r.navigation_id
                    WHERE r.destination_business_code IS NOT NULL
                      AND b.navigation_profile_id = r.navigation_id
                      AND b.business_code = r.destination_business_code
                      AND (
                          (tt.code = 'REMOVE_EMPTY' AND b.endpoint_class = 'EMPTY_RETURN')
                          OR (tt.code = 'REMOVE_FULL' AND b.endpoint_class = 'FINISHED_GOODS' AND r.destination_business_code LIKE 'FG-%')
                          OR (tt.code = 'REMOVE_FULL' AND b.endpoint_class = 'NON_FINISHED_RETURN' AND r.destination_business_code NOT LIKE 'FG-%')
                      )
                ) shared_destination
                ORDER BY preference, member_priority, display_order, sort_id NULLS LAST
                LIMIT 1
            ) dst ON true
            WHERE r.id = @request_id
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("request_id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var request = ReadRequest(reader);
        if (reader.IsDBNull(24))
        {
            throw new RdsExecutionException("RDS_TASK_TEMPLATE_NOT_FOUND", "No active task template matches the request variant, product group and operation.");
        }

        var context = new RdsDispatchContext(
            request,
            reader.GetString(19),
            reader.GetString(20),
            Text(reader, 21),
            Text(reader, 22),
            Text(reader, 23),
            reader.GetGuid(24),
            Text(reader, 25),
            reader.GetInt32(26),
            Json(reader.GetString(27)),
            Endpoint(reader, 28),
            Endpoint(reader, 39));
        await reader.DisposeAsync();
        async Task<RdsDispatchEndpoint> WithOccupancy(RdsDispatchEndpoint endpoint)
        {
            if (endpoint.SlotId is not { } slotId) return endpoint;
            await using var runtime = new NpgsqlCommand("SELECT occupancy_state FROM ops.slot_runtime_state WHERE layout_slot_id = @slot", connection);
            runtime.Parameters.AddWithValue("slot", slotId);
            return endpoint with { BusinessOccupancy = await runtime.ExecuteScalarAsync(cancellationToken) as string ?? "UNKNOWN" };
        }
        return context with { Source = await WithOccupancy(context.Source), Destination = await WithOccupancy(context.Destination) };

    }

    public async Task<string?> ReadRegistryDispatchBlockAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT n.status,
                   COALESCE((n.config_json ->> 'geometryVerified')::boolean, false),
                   n.config_json ->> 'layoutBindingState',
                   tt.code,
                    r.destination_business_code,
                    r.source_business_code,
                   r.source_slot_id IS NOT NULL,
                   r.destination_slot_id IS NOT NULL,
                   EXISTS (
                       SELECT 1 FROM master.task_templates t
                       WHERE t.variant_id = r.variant_id
                         AND t.task_type_id = r.task_type_id
                         AND t.is_active
                         AND (t.product_group_id = r.product_group_id OR t.product_group_id IS NULL)),
                   EXISTS (
                       SELECT 1
                       FROM master.product_groups pg
                       WHERE pg.id = r.product_group_id AND pg.main_part_id = r.part_id)
            FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            JOIN master.navigation_profiles n ON n.id = r.navigation_id
            WHERE r.id = @id
              AND n.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
            """,
            connection);
        command.Parameters.AddWithValue("id", requestId);
        string status;
        bool geometry;
        string? layout;
        string operation;
        string? businessCode;
        string? sourceBusinessCode;
        bool hasSourceSlot;
        bool hasDestinationSlot;
        bool hasTemplate;
        bool finishedGoods;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            status = reader.GetString(0);
            geometry = reader.GetBoolean(1);
            layout = reader.IsDBNull(2) ? null : reader.GetString(2);
            operation = reader.GetString(3);
            businessCode = reader.IsDBNull(4) ? null : reader.GetString(4);
            sourceBusinessCode = reader.IsDBNull(5) ? null : reader.GetString(5);
            hasSourceSlot = reader.GetBoolean(6);
            hasDestinationSlot = reader.GetBoolean(7);
            hasTemplate = reader.GetBoolean(8);
            finishedGoods = reader.GetBoolean(9);
        }

        if (layout == "PENDING_SOURCE_LAYOUT") return "PENDING_SOURCE_LAYOUT";
        if (status != "PUBLISHED") return "NAVIGATION_PROFILE_NOT_PUBLISHED";
        if (!geometry) return "GEOMETRY_NOT_VERIFIED";
        if (!hasTemplate) return "TASK_TEMPLATE_MISSING";

        if (operation == "BRING_FULL")
        {
            return await ReadWorkSiteHealthAsync(connection, requestId, "PREPARATION", null, "PREPARATION_WORKSITE_MAPPING_MISSING", cancellationToken);
        }

        if (operation == "BRING_EMPTY" && !hasSourceSlot)
        {
            await using var prepared = new NpgsqlCommand("""
                SELECT 1 FROM ops.preparation_pallets
                WHERE transport_request_id = @id AND released_at IS NULL AND ready_at IS NOT NULL
                LIMIT 1
                """, connection);
            prepared.Parameters.AddWithValue("id", requestId);
            if (await prepared.ExecuteScalarAsync(cancellationToken) is not null)
                return await ReadWorkSiteHealthAsync(connection, requestId, "PREPARATION", null, "PREPARATION_WORKSITE_MAPPING_MISSING", cancellationToken);
            return await ReadWorkSiteHealthAsync(connection, requestId, "EMPTY_SOURCE", sourceBusinessCode, "EMPTY_SOURCE_MAPPING_MISSING", cancellationToken);
        }

        if (operation == "REMOVE_EMPTY" && (businessCode is not null || !hasDestinationSlot))
        {
            return await ReadWorkSiteHealthAsync(connection, requestId, "EMPTY_RETURN", businessCode, "EMPTY_RETURN_MAPPING_MISSING", cancellationToken);
        }

        if (operation == "REMOVE_FULL" && (businessCode is not null || !hasDestinationSlot))
        {
            if (businessCode is not null && businessCode.StartsWith("FG-", StringComparison.Ordinal))
            {
                return await ReadWorkSiteHealthAsync(connection, requestId, "FINISHED_GOODS", businessCode, "FINISHED_GOODS_MAPPING_MISSING", cancellationToken);
            }

            if (businessCode is not null)
            {
                return await ReadWorkSiteHealthAsync(connection, requestId, "NON_FINISHED_RETURN", businessCode, "NON_FINISHED_RETURN_MAPPING_MISSING", cancellationToken);
            }

            return finishedGoods ? "FINISHED_GOODS_MAPPING_MISSING" : "NON_FINISHED_RETURN_MAPPING_MISSING";
        }

        return null;
    }

    private static async Task<string?> ReadWorkSiteHealthAsync(
        NpgsqlConnection connection,
        Guid requestId,
        string endpointClass,
        string? businessCode,
        string missingCode,
        CancellationToken cancellationToken)
    {
        var sql = endpointClass == "PREPARATION"
            ? """
              SELECT COALESCE(w.last_disabled, 0), COALESCE(w.last_sync_failed, 0)
              FROM ops.transport_requests r
              JOIN ops.preparation_pallets pp ON pp.transport_request_id = r.id AND pp.released_at IS NULL AND pp.ready_at IS NOT NULL
              JOIN integration.preparation_area_rds_mapping pm ON pm.preparation_area_id = pp.preparation_area_id
              JOIN integration.rds_worksites w ON w.id = pm.rds_worksite_id AND w.navigation_profile_id = r.navigation_id
              WHERE r.id = @id
              LIMIT 1
              """
            : """
              SELECT COALESCE(w.last_disabled, 0), COALESCE(w.last_sync_failed, 0)
              FROM ops.transport_requests r
              JOIN integration.profile_shared_endpoint_bindings b ON b.navigation_profile_id = r.navigation_id
              JOIN integration.rds_worksites w ON w.id = b.rds_worksite_id AND w.navigation_profile_id = r.navigation_id
              WHERE r.id = @id
                AND b.endpoint_class = @class
                AND (CAST(@business AS varchar) IS NULL OR b.business_code = CAST(@business AS varchar))
              ORDER BY b.business_code
              LIMIT 1
              """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", requestId);
        if (endpointClass != "PREPARATION")
        {
            command.Parameters.AddWithValue("class", endpointClass);
            command.Parameters.Add(new NpgsqlParameter("business", NpgsqlTypes.NpgsqlDbType.Varchar)
            {
                Value = (object?)businessCode ?? DBNull.Value
            });
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return missingCode;
        if (reader.GetInt32(0) == 1) return "RDS_WORKSITE_DISABLED";
        if (reader.GetInt32(1) == 1) return "RDS_WORKSITE_SYNC_FAILED";
        return null;
    }

    private async Task<string?> ReadPreparationPhysicalBlockAsync(
        NpgsqlConnection connection, Guid requestId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT w.rds_type, w.last_filled, w.last_locked, w.last_disabled,
                   w.last_sync_failed, w.last_sync_at
            FROM ops.transport_requests r
            JOIN ops.preparation_pallets p
              ON p.transport_request_id = r.id AND p.released_at IS NULL AND p.ready_at IS NOT NULL
            JOIN integration.preparation_area_rds_mapping map
              ON map.preparation_area_id = p.preparation_area_id
            JOIN integration.rds_worksites w
              ON w.id = map.rds_worksite_id AND w.navigation_profile_id = r.navigation_id
            WHERE r.id = @id
            LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return "PREPARATION_WORKSITE_MAPPING_MISSING";
        var evidence = new RdsPhysicalEvidence(
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
        var policy = physicalPolicy ?? new RdsPhysicalAvailabilityPolicy();
        var state = policy.State(evidence);
        var sourceAvailable = policy.IsSimulationMode ? state == "AVAILABLE" : state == "OCCUPIED";
        if (sourceAvailable) return null;
        return !policy.IsSimulationMode && state == "AVAILABLE"
            ? "RDS_WORKSITE_OCCUPANCY_CONFLICT"
            : RdsPhysicalAvailabilityPolicy.ErrorCode(state) ?? "RDS_WORKSITE_STATE_UNKNOWN";
    }

    public async Task<TransportRequestSnapshot?> FindRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(RequestSelect + " WHERE r.id = @id", connection);
        command.Parameters.AddWithValue("id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRequest(reader) : null;
    }

    public Task<RdsTaskProjection?> FindTaskByRequestAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        FindTaskAsync("rt.transport_request_id = @value", requestId, cancellationToken);

    public Task<RdsTaskProjection?> FindTaskByRecordIdAsync(string taskRecordId, CancellationToken cancellationToken = default) =>
        FindTaskAsync("rt.rds_task_record_id = @value", taskRecordId, cancellationToken);

    public async Task EnsureTaskRecordAsync(Guid requestId, string taskRecordId, string taskLabel, JsonElement requestPayload, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand(
                         """
                         INSERT INTO integration.rds_tasks (
                             transport_request_id, rds_task_record_id, rds_task_label, request_payload_json)
                         VALUES (@request_id, @record_id, @label, @payload)
                         ON CONFLICT (transport_request_id) DO NOTHING
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("request_id", requestId);
            insert.Parameters.AddWithValue("record_id", taskRecordId);
            insert.Parameters.AddWithValue("label", taskLabel);
            AddJson(insert, "payload", requestPayload);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var verify = new NpgsqlCommand(
                         "SELECT rds_task_record_id, rds_task_label FROM integration.rds_tasks WHERE transport_request_id = @request_id FOR UPDATE",
                         connection,
                         transaction))
        {
            verify.Parameters.AddWithValue("request_id", requestId);
            await using var reader = await verify.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || !string.Equals(reader.GetString(0), taskRecordId, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(1), taskLabel, StringComparison.Ordinal))
            {
                throw new RdsExecutionException("RDS_TASK_IDENTITY_CONFLICT", "The request already owns a different RDS task identity or label.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task RecordDispatchResponseAsync(Guid requestId, JsonElement responsePayload, DateTimeOffset syncedAt, CancellationToken cancellationToken = default) =>
        UpdateJsonAsync(
            """
            UPDATE integration.rds_tasks
            SET response_payload_json = @json, last_sync_at = @at, updated_at = now()
            WHERE transport_request_id = @request_id
            """,
            requestId,
            responsePayload,
            syncedAt,
            cancellationToken);

    public async Task RecordTaskStatusAsync(Guid requestId, RdsWindTaskSnapshot task, DateTimeOffset syncedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE integration.rds_tasks
            SET rds_task_label = COALESCE(@label, rds_task_label),
                out_order_no = COALESCE(@out_order_no, out_order_no),
                agv_id = COALESCE(@agv_id, agv_id),
                rds_status = @status,
                ended_reason = COALESCE(@ended_reason, ended_reason),
                last_status_json = @raw,
                sent_at = COALESCE(sent_at, @synced_at),
                started_at = CASE WHEN @status = 1000 THEN COALESCE(started_at, @synced_at) ELSE started_at END,
                ended_at = CASE WHEN @status IN (1001,1003,1004,1005,1006) THEN COALESCE(ended_at, @synced_at) ELSE ended_at END,
                last_sync_at = @synced_at,
                updated_at = now()
            WHERE transport_request_id = @request_id
            """,
            connection);
        command.Parameters.AddWithValue("request_id", requestId);
        AddNullableText(command, "label", task.TaskLabel);
        AddNullableText(command, "out_order_no", task.OutOrderNo);
        AddNullableText(command, "agv_id", task.AgvId);
        command.Parameters.AddWithValue("status", (object?)task.Status ?? DBNull.Value);
        AddNullableText(command, "ended_reason", task.EndedReason);
        AddJson(command, "raw", task.RawState);
        command.Parameters.AddWithValue("synced_at", syncedAt);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new RdsExecutionException("RDS_TASK_NOT_FOUND", "The RDS task projection does not exist.");
    }

    public async Task MarkCancellationRequestedAsync(Guid requestId, JsonElement responsePayload, DateTimeOffset requestedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE integration.rds_tasks
            SET response_payload_json = response_payload_json || jsonb_build_object(
                    'cancellationRequestedAt', @requested_at,
                    'stopResponse', @response),
                updated_at = now()
            WHERE transport_request_id = @request_id
            """,
            connection);
        command.Parameters.AddWithValue("request_id", requestId);
        command.Parameters.AddWithValue("requested_at", requestedAt);
        AddJson(command, "response", responsePayload);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new RdsExecutionException("RDS_TASK_NOT_FOUND", "The RDS task projection does not exist.");
    }

    public Task WriteExecutionAuditAsync(
        Guid requestId,
        string action,
        string code,
        string message,
        Guid correlationId,
        CancellationToken cancellationToken = default) =>
        WriteExecutionAuditAsync(requestId, action, code, message, correlationId, null, cancellationToken);

    public async Task WriteExecutionAuditAsync(
        Guid requestId,
        string action,
        string code,
        string message,
        Guid correlationId,
        object? extraDetails,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        string afterJson;
        if (extraDetails is null || extraDetails is CancellationToken)
        {
            afterJson = JsonSerializer.Serialize(new { code, message });
        }
        else
        {
            var dict = new Dictionary<string, object?>
            {
                ["code"] = code,
                ["message"] = message
            };
            var extraJson = JsonSerializer.SerializeToElement(extraDetails);
            if (extraJson.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in extraJson.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.Clone();
                }
            }
            afterJson = JsonSerializer.Serialize(dict);
        }

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO audit.audit_logs (
                action, entity_type, entity_id, after_json, correlation_id)
            VALUES (
                @action, 'TRANSPORT_REQUEST', @entity_id,
                @after_json::jsonb,
                @correlation_id)
            """,
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entity_id", requestId.ToString());
        command.Parameters.AddWithValue("after_json", afterJson);
        command.Parameters.AddWithValue("correlation_id", correlationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RdsMappedWorkSite?> FindMappedWorkSiteAsync(Guid slotId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT map.layout_slot_id, rw.id, rw.rds_site_id, rw.rds_type,
                   rw.last_disabled, rw.last_sync_failed, rw.last_sync_at
            FROM integration.layout_slot_rds_mapping map
            JOIN integration.rds_worksites rw ON rw.id = map.rds_worksite_id
            WHERE map.layout_slot_id = @slot_id
            """,
            connection);
        command.Parameters.AddWithValue("slot_id", slotId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(slotId, reader.GetGuid(1), reader.GetString(2), Number(reader, 3), Number(reader, 4), Number(reader, 5), Timestamp(reader, 6))
            : null;
    }

    public async Task<IReadOnlyList<RdsTaskTemplateSummary>> ListTaskTemplatesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT t.id, t.variant_id, v.code, t.product_group_id, p.part_code,
                   tt.code, t.rds_task_label, t.priority, t.config_json::text, t.is_active
            FROM master.task_templates t
            JOIN master.variants v ON v.id = t.variant_id
            JOIN master.task_types tt ON tt.id = t.task_type_id
            LEFT JOIN master.product_groups pg ON pg.id = t.product_group_id
            LEFT JOIN master.parts p ON p.id = pg.main_part_id
            ORDER BY v.code, p.part_code NULLS FIRST, tt.code, t.id
            """,
            connection);
        var rows = new List<RdsTaskTemplateSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                Uuid(reader, 3), Text(reader, 4), reader.GetString(5), Text(reader, 6),
                reader.GetInt32(7), Json(reader.GetString(8)), reader.GetBoolean(9)));
        }
        return rows;
    }

    public async Task<RdsNavigationProfileSummary?> GetNavigationProfileSummaryAsync(
        Guid? profileId,
        Guid variantId,
        Guid machineId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql =
            """
            SELECT np.id, np.code, np.name, np.status, np.robot_map_name, np.config_json::text
            FROM master.variant_navigation_profile_assignments vna
            JOIN master.navigation_profiles np ON np.id = vna.navigation_profile_id AND np.machine_id = vna.machine_id
            WHERE vna.variant_id = @variant_id
              AND vna.machine_id = @machine_id
              AND np.machine_id = @machine_id
              AND np.status = 'PUBLISHED'
              AND (@profile_id::uuid IS NULL OR np.id = @profile_id)
            ORDER BY (vna.is_default) DESC, np.id
            LIMIT 1
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("variant_id", variantId);
        command.Parameters.AddWithValue("machine_id", machineId);
        command.Parameters.Add(new NpgsqlParameter("profile_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)profileId ?? DBNull.Value });

        await using (command)
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;

            var id = reader.GetGuid(0);
            var code = reader.GetString(1);
            var name = reader.GetString(2);
            var status = reader.GetString(3);
            var robotMapName = reader.IsDBNull(4) ? null : reader.GetString(4);
            var configStr = reader.IsDBNull(5) ? "{}" : reader.GetString(5);
            var configJson = Json(configStr);

            string? switchTransition = null;
            string? vehicle = null;
            if (configJson.ValueKind == JsonValueKind.Object)
            {
                if (configJson.TryGetProperty("switchTransitionStation", out var stProp) && stProp.ValueKind == JsonValueKind.String)
                {
                    switchTransition = stProp.GetString();
                }
                else if (configJson.TryGetProperty("transitionStation", out var tsProp) && tsProp.ValueKind == JsonValueKind.String)
                {
                    switchTransition = tsProp.GetString();
                }

                if (configJson.TryGetProperty("vehicle", out var vProp) && vProp.ValueKind == JsonValueKind.String)
                {
                    vehicle = vProp.GetString();
                }
                else if (configJson.TryGetProperty("allowedVehicles", out var avProp) && avProp.ValueKind == JsonValueKind.Array && avProp.GetArrayLength() > 0)
                {
                    vehicle = avProp[0].GetString();
                }
            }

            return new RdsNavigationProfileSummary(
                id,
                code,
                name,
                status,
                robotMapName,
                switchTransition,
                vehicle,
                configJson);
        }
    }

    public async Task EnrichRemainingTimesAsync(
        IReadOnlyDictionary<string, double> agvRemainingTimes,
        CancellationToken cancellationToken = default)
    {
        if (agvRemainingTimes.Count == 0) return;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        foreach (var (agvId, remainingTime) in agvRemainingTimes)
        {
            await using var command = new NpgsqlCommand(
                """
                UPDATE integration.rds_tasks
                SET last_status_json = COALESCE(last_status_json, '{}'::jsonb)
                        || jsonb_build_object('remaining_time', @remaining_time),
                    updated_at = now()
                WHERE agv_id = @agv_id
                  AND rds_status NOT IN (1001, 1003, 1004, 1005, 1006)
                """,
                connection);
            command.Parameters.AddWithValue("agv_id", agvId);
            command.Parameters.AddWithValue("remaining_time", remainingTime);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<RdsTaskProjection?> FindTaskAsync(string predicate, object value, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(TaskProjectionSelect + $" WHERE {predicate}", connection);
        command.Parameters.AddWithValue("value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTaskProjection(reader) : null;
    }

    private async Task UpdateJsonAsync(string sql, Guid requestId, JsonElement json, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("request_id", requestId);
        AddJson(command, "json", json);
        command.Parameters.AddWithValue("at", at);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new RdsExecutionException("RDS_TASK_NOT_FOUND", "The RDS task projection does not exist.");
    }

    private static TransportRequestSnapshot ReadRequest(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2),
        WorkflowCode.ParseTransportStatus(reader.GetString(3)),
        WorkflowCode.ParseTaskType(reader.GetString(4)),
        WorkflowCode.ParseRequestOrigin(reader.GetString(5)),
        Uuid(reader, 6), reader.GetGuid(7), reader.GetGuid(8), reader.GetGuid(9),
        Uuid(reader, 10), Uuid(reader, 11), Uuid(reader, 12), Uuid(reader, 13),
        Uuid(reader, 14), Uuid(reader, 15), reader.GetInt32(16),
        WorkflowCode.ParseReadinessState(reader.GetString(17)), Timestamp(reader, 18));

    private static RdsDispatchEndpoint Endpoint(NpgsqlDataReader reader, int start) => new(
        Uuid(reader, start), Text(reader, start + 1), Text(reader, start + 2), Text(reader, start + 3),
        Text(reader, start + 4), Number(reader, start + 5), Number(reader, start + 6),
        Number(reader, start + 7), Number(reader, start + 8), Number(reader, start + 9),
        Timestamp(reader, start + 10), !reader.IsDBNull(start + 4));

    private static RdsTaskProjection ReadTaskProjection(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), Text(reader, 2), Number(reader, 3), Text(reader, 4),
        Text(reader, 5), Text(reader, 6), Json(reader.GetString(7)), Json(reader.GetString(8)),
        Json(reader.GetString(9)), reader.GetBoolean(10), WorkflowCode.ParseTransportStatus(reader.GetString(11)));

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    public async Task<bool> HasUnreleasedMapSwitchLockAsync(Guid requestId, string vehicle, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetUnreleasedMapSwitchLockAsync(requestId, vehicle, cancellationToken);
        return snapshot?.HasUnreleasedLock == true;
    }

    public async Task<RdsMapSwitchLockSnapshot?> GetUnreleasedMapSwitchLockAsync(
        Guid requestId,
        string vehicle,
        CancellationToken cancellationToken = default)
    {
        // The vehicle argument is part of the filter, not decoration: a lock record
        // only describes this recovery context if it was recorded for the robot now
        // being prepared. The application layer revalidates vehicle and target map
        // again before any automatic /unlock (defence in depth).
        const string sql =
            """
            SELECT after_json
            FROM audit.audit_logs
            WHERE entity_type = 'TRANSPORT_REQUEST'
              AND entity_id = @request_id
              AND action = 'MAP_PREP_LOCK_ACQUIRED'
              AND lower(coalesce(after_json ->> 'vehicle', after_json ->> 'Vehicle', '')) = lower(@vehicle)
              AND NOT EXISTS (
                  SELECT 1 FROM audit.audit_logs
                  WHERE entity_type = 'TRANSPORT_REQUEST'
                    AND entity_id = @request_id
                    AND action = 'MAP_PREP_LOCK_RELEASED'
              )
            ORDER BY created_at DESC
            LIMIT 1;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("request_id", requestId.ToString());
        command.Parameters.AddWithValue("vehicle", (vehicle ?? string.Empty).Trim());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
            return null;

        var jsonStr = result.ToString();
        if (string.IsNullOrWhiteSpace(jsonStr))
            return null;

        using var doc = JsonDocument.Parse(jsonStr);
        var root = doc.RootElement;

        string? recordedVehicle = TryGetPropertyIgnoreCase(root, "vehicle", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? targetMap = TryGetPropertyIgnoreCase(root, "requiredMap", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

        RdsCoreRobotLockInfo? lockInfo = null;
        if (TryGetPropertyIgnoreCase(root, "lockInfo", out var li) && li.ValueKind == JsonValueKind.Object)
        {
            bool locked = TryGetPropertyIgnoreCase(li, "locked", out var l) && (l.ValueKind == JsonValueKind.True || (l.ValueKind == JsonValueKind.String && bool.TryParse(l.GetString(), out var lb) && lb));
            string? ip = TryGetPropertyIgnoreCase(li, "ip", out var ipP) && ipP.ValueKind == JsonValueKind.String ? ipP.GetString() : null;
            string? nickName = TryGetPropertyIgnoreCase(li, "nickName", out var nP) && nP.ValueKind == JsonValueKind.String ? nP.GetString()
                : (TryGetPropertyIgnoreCase(li, "nick_name", out var nP2) && nP2.ValueKind == JsonValueKind.String ? nP2.GetString() : null);
            int? port = TryGetPropertyIgnoreCase(li, "port", out var pP) && pP.TryGetInt32(out var pVal) ? pVal : null;
            string? timeT = TryGetPropertyIgnoreCase(li, "timeT", out var tP) && tP.ValueKind is JsonValueKind.String or JsonValueKind.Number ? tP.ToString()
                : (TryGetPropertyIgnoreCase(li, "time_t", out var tP2) && tP2.ValueKind is JsonValueKind.String or JsonValueKind.Number ? tP2.ToString() : null);
            int? type = TryGetPropertyIgnoreCase(li, "type", out var typeP) && typeP.TryGetInt32(out var typeVal) ? typeVal : null;
            string? desc = TryGetPropertyIgnoreCase(li, "desc", out var dP) && dP.ValueKind == JsonValueKind.String ? dP.GetString() : null;

            lockInfo = new RdsCoreRobotLockInfo(locked, ip, nickName, port, timeT, type, desc);
        }

        return new RdsMapSwitchLockSnapshot(true, recordedVehicle, targetMap, lockInfo);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out value))
                return true;
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static Guid? Uuid(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static int? Number(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static DateTimeOffset? Timestamp(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static void AddJson(NpgsqlCommand command, string name, JsonElement value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = value.GetRawText() });

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    /// <summary>
    /// Session advisory lease over a pre-computed key string. Release and connection
    /// disposal both happen on dispose, with disposal in a finally so a failed unlock
    /// still returns the connection; dropping the session would release the lock
    /// anyway, so the lock cannot outlive the connection either way.
    /// </summary>
    private sealed class PostgresRdsAdvisoryLease(NpgsqlConnection connection, string key) : IRdsExecutionLease
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtextextended(@key, 0))",
                    connection);
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteScalarAsync();
            }
            catch
            {
                // Disposing the connection ends the session, which releases the lock.
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }

    private sealed class PostgresRdsExecutionLease(NpgsqlConnection connection, Guid requestId) : IRdsExecutionLease
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtextextended(@key, 0))",
                    connection);
                command.Parameters.AddWithValue("key", $"tkg:rds:{requestId:D}");
                await command.ExecuteScalarAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
