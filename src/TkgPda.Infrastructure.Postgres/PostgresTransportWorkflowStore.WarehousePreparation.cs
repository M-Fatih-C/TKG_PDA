using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Infrastructure.Postgres;

/// <summary>
/// Warehouse preparation areas: four semi-finished positions and six
/// request-bound finished-goods empty-container positions.
///
/// LOCK ORDER: transport request, then preparation pallet. MarkReadyAsync
/// therefore reads the pallet's request id without a lock, takes the request
/// lock, then locks the pallet and re-checks that the binding did not move
/// while it waited. Taking the pallet first would invert the order against the
/// transport workflow's own request-first locking and could deadlock.
///
/// The one-pallet-per-area and one-pallet-per-request rules are enforced by the
/// partial unique indexes from migration 008, not by a prior SELECT. A lost
/// race surfaces as a unique violation and is translated into a rule error,
/// so two tablets filling the same area cannot both believe they won.
/// </summary>
public sealed partial class PostgresTransportWorkflowStore : IWarehousePreparationStore
{
    private const string PalletColumns = """
        p.id, p.container_type_id, ct.code, p.load_state, p.part_id, part.part_code,
        p.transport_request_id, r.request_no, m.code, p.placed_at, p.placed_by, p.ready_at, p.row_version, r.status
        """;

    // Every join is LEFT: ListAreasAsync reaches these through a LEFT JOIN to
    // the pallet, so an inner join on the container would drop free areas from
    // the result entirely. Request-bound finished-goods empty pallets may also
    // intentionally have no container type when the customer source contains
    // dimensions but no container code.
    private const string PalletJoins = """
        LEFT JOIN master.container_types ct ON ct.id = p.container_type_id
        LEFT JOIN master.parts part ON part.id = p.part_id
        LEFT JOIN ops.transport_requests r ON r.id = p.transport_request_id
        LEFT JOIN master.machines m ON m.id = r.machine_id
        """;

    public async Task<IReadOnlyList<PreparationAreaRecord>> ListAreasAsync(
        string warehouseScope = WarehousePreparationScopes.SemiFinished,
        CancellationToken cancellationToken = default)
    {
        warehouseScope = WarehousePreparationScopes.Normalize(warehouseScope);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT a.id, a.area_number, a.display_name, a.is_enabled, a.warehouse_scope, {PalletColumns}
            FROM ops.preparation_areas a
            LEFT JOIN ops.preparation_pallets p
                   ON p.preparation_area_id = a.id AND p.released_at IS NULL
            {PalletJoins}
            WHERE a.warehouse_scope = @scope
            ORDER BY a.area_number
            """, connection);
        command.Parameters.AddWithValue("scope", warehouseScope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var areas = new List<PreparationAreaRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            areas.Add(new PreparationAreaRecord(
                reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetBoolean(3),
                reader.IsDBNull(5) ? null : ReadPallet(reader, 5), reader.GetString(4)));
        }

        return areas;
    }

    public async Task<IReadOnlyList<WarehouseDemandRecord>> ListDemandsAsync(
        string warehouseScope = WarehousePreparationScopes.SemiFinished,
        CancellationToken cancellationToken = default)
    {
        warehouseScope = WarehousePreparationScopes.Normalize(warehouseScope);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        // Oldest first: the tablet's side list is a queue, and the operator
        // needs to see what has waited longest, not what arrived last.
        await using var command = new NpgsqlCommand($"""
            SELECT r.id, r.request_no, r.machine_id, m.code, r.part_id, part.part_code,
                   COALESCE(r.container_type_id, machine_slot.default_container_type_id), ct.code,
                   ppm.container_dimensions, tt.code, r.created_at,
                   claimed.preparation_area_id, claimed_area.area_number, part.description,
                   v.code, v.name, pg.name
            FROM ops.transport_requests r
            JOIN master.machines m ON m.id = r.machine_id
            JOIN master.variants v ON v.id = r.variant_id
            JOIN master.task_types tt ON tt.id = r.task_type_id
            LEFT JOIN master.parts part ON part.id = r.part_id
            LEFT JOIN master.product_groups pg ON pg.id = r.product_group_id
            LEFT JOIN master.layout_slots machine_slot
                   ON machine_slot.id = CASE WHEN tt.code = 'BRING_EMPTY' THEN r.destination_slot_id ELSE r.source_slot_id END
            LEFT JOIN master.container_types ct
                   ON ct.id = COALESCE(r.container_type_id, machine_slot.default_container_type_id)
            LEFT JOIN master.part_planning_metrics ppm
                   ON ppm.variant_id = r.variant_id
                  AND ppm.product_group_id = r.product_group_id
                  AND ppm.part_id = r.part_id
            LEFT JOIN ops.preparation_pallets claimed
                   ON claimed.transport_request_id = r.id AND claimed.released_at IS NULL
            LEFT JOIN ops.preparation_areas claimed_area ON claimed_area.id = claimed.preparation_area_id
            WHERE r.status IN ({ActiveStatusesSql})
              AND (
                    (@scope = 'FINISHED_GOODS'
                     AND tt.code = 'BRING_EMPTY'
                     AND (r.readiness_state = 'WAITING_PREPARATION' OR claimed.id IS NOT NULL))
                 OR (@scope = 'SEMI_FINISHED'
                     AND tt.code = 'BRING_FULL'
                     AND r.readiness_state = 'WAITING_PREPARATION')
              )
            ORDER BY r.created_at, r.request_no
            """, connection);
        command.Parameters.AddWithValue("scope", warehouseScope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var demands = new List<WarehouseDemandRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var operation = WorkflowCode.ParseTaskType(reader.GetString(9));
            // What the warehouse has to put in the container, derived from the
            // operation rather than stored twice and allowed to drift.
            var requestedLoad = operation == TransportTaskType.BringFull ? LoadState.Full : LoadState.Empty;
            demands.Add(new WarehouseDemandRecord(
                RequestId: reader.GetGuid(0),
                RequestNo: reader.GetInt64(1),
                MachineId: reader.GetGuid(2),
                MachineCode: reader.GetString(3),
                PartId: reader.IsDBNull(4) ? null : reader.GetGuid(4),
                PartCode: reader.IsDBNull(5) ? null : reader.GetString(5),
                ContainerTypeId: reader.IsDBNull(6) ? null : reader.GetGuid(6),
                ContainerCode: reader.IsDBNull(7) ? null : reader.GetString(7),
                ContainerDimensions: reader.IsDBNull(8) ? null : reader.GetString(8),
                Operation: operation.ToDatabase(),
                RequestedLoad: requestedLoad.ToDatabase(),
                RequestedAt: reader.GetFieldValue<DateTimeOffset>(10),
                ClaimedByAreaId: reader.IsDBNull(11) ? null : reader.GetGuid(11),
                ClaimedByAreaNumber: reader.IsDBNull(12) ? null : reader.GetInt32(12),
                PartDescription: reader.IsDBNull(13) ? null : reader.GetString(13),
                VariantCode: reader.IsDBNull(14) ? null : reader.GetString(14),
                VariantName: reader.IsDBNull(15) ? null : reader.GetString(15),
                ProductGroupName: reader.IsDBNull(16) ? null : reader.GetString(16),
                WarehouseScope: warehouseScope));
        }

        return demands;
    }

    public async Task<PreparationPalletResult> PlacePalletAsync(
        PlacePalletCommand command, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var scope = WarehousePreparationScopes.Normalize(command.WarehouseScope);
        await ValidateWarehouseActorAsync(connection, transaction, command.ActorUserId, command.DeviceId, scope, cancellationToken);

        var replay = await FindPalletAsync(connection, transaction,
            "p.preparation_area_id = @area AND p.placement_key = @key",
            [("area", command.AreaId), ("key", command.IdempotencyKey)], cancellationToken);
        if (replay is not null)
        {
            // Same key, different content is a different intent wearing the
            // same key, not a retry.
            if (replay.ContainerTypeId != command.ContainerTypeId || replay.Load != command.Load.ToDatabase()
                || replay.PartId != command.PartId || replay.TransportRequestId != command.TransportRequestId)
            {
                throw new WorkflowRuleException(
                    "IDEMPOTENCY_KEY_REUSED", "İstek anahtarı farklı bir hazırlık kaydına ait.");
            }

            await transaction.CommitAsync(cancellationToken);
            return new PreparationPalletResult(replay, WasIdempotentReplay: true);
        }

        await EnsureAreaEnabledAsync(connection, transaction, command.AreaId, scope, cancellationToken);
        if (command.TransportRequestId is { } requestId)
        {
            // WP-06: the request row lock is what serialises the first-only
            // WAREHOUSE_PREPARATION_STARTED decision below. It is taken in the same
            // request-then-pallet order MarkReadyAsync uses, before any pallet row
            // is touched, so concurrent placements for one request cannot both
            // observe "no STARTED event yet".
            await LockRequestAsync(connection, transaction, requestId, cancellationToken);
            await ValidateDemandAsync(connection, transaction, requestId, command, scope, cancellationToken);
        }

        PreparationPalletRecord pallet;
        try
        {
            Guid palletId;
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO ops.preparation_pallets
                    (preparation_area_id, container_type_id, load_state, part_id,
                     transport_request_id, placed_by, placed_device_id, placement_key)
                VALUES (@area, @container, @load, @part, @request, @actor, @device, @key)
                RETURNING id
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("area", command.AreaId);
                AddNullableUuid(insert, "container", command.ContainerTypeId);
                insert.Parameters.AddWithValue("load", command.Load.ToDatabase());
                AddNullableUuid(insert, "part", command.PartId);
                AddNullableUuid(insert, "request", command.TransportRequestId);
                insert.Parameters.AddWithValue("actor", command.ActorUserId);
                insert.Parameters.AddWithValue("device", command.DeviceId);
                insert.Parameters.AddWithValue("key", command.IdempotencyKey);
                palletId = (Guid)(await insert.ExecuteScalarAsync(cancellationToken))!;
            }

            pallet = await FindPalletAsync(connection, transaction, "p.id = @id", [("id", palletId)], cancellationToken)
                ?? throw new WorkflowRuleException("PREPARATION_PALLET_NOT_FOUND", "Hazırlık kaydı okunamadı.");
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw TranslatePreparationConflict(error);
        }

        await InsertPreparationEventAsync(connection, transaction, command.AreaId, pallet.Id, "PALLET_PLACED",
            command.PartId, command.ContainerTypeId, command.TransportRequestId,
            command.ActorUserId, command.DeviceId, new { load = command.Load.ToDatabase() }, cancellationToken);

        // WAREHOUSE_PREPARATION_STARTED is a once-per-request lifecycle boundary.
        // A release followed by a replacement pallet is another physical attempt,
        // recorded above as PALLET_PLACED in ops.preparation_events, not a second
        // lifecycle start. The request row is locked, so this check cannot race.
        if (command.TransportRequestId is { } boundRequestId
            && !await HasTransportEventAsync(
                connection, transaction, boundRequestId, TransportEventTypes.WarehousePreparationStarted, cancellationToken))
        {
            await InsertEventAsync(
                connection,
                transaction,
                boundRequestId,
                command.ActorUserId,
                TransportEventTypes.WarehousePreparationStarted,
                null,
                null,
                "Warehouse preparation started for transport request.",
                new
                {
                    preparation_area_id = command.AreaId,
                    preparation_pallet_id = pallet.Id,
                    part_id = command.PartId,
                    container_type_id = command.ContainerTypeId,
                    load_state = command.Load.ToDatabase()
                },
                cancellationToken,
                OperationalEventContext.ForUser(command.ActorUserId, command.DeviceId, command.CorrelationId));
        }

        await transaction.CommitAsync(cancellationToken);
        return new PreparationPalletResult(pallet, WasIdempotentReplay: false);
    }

    public async Task<PreparationPalletRecord> MarkReadyAsync(
        MarkPalletReadyCommand command, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var scope = await ReadPalletAreaScopeAsync(connection, transaction, command.PalletId, cancellationToken);
        await ValidateWarehouseActorAsync(connection, transaction, command.ActorUserId, command.DeviceId, scope, cancellationToken);

        // Unlocked peek only to learn which request to lock first; the binding
        // is re-verified below once both rows are held.
        var boundRequestId = await ReadPalletRequestIdAsync(connection, transaction, command.PalletId, cancellationToken);
        TransportRequestSnapshot? boundRequest = null;
        if (boundRequestId is { } requestId)
        {
            boundRequest = await LockRequestAsync(connection, transaction, requestId, cancellationToken);
        }

        var pallet = await LockPalletAsync(connection, transaction, command.PalletId, cancellationToken);
        if (pallet.TransportRequestId != boundRequestId)
        {
            throw new WorkflowRuleException(
                "PREPARATION_PALLET_CHANGED", "Hazırlık kaydı değişti; ekranı yenileyin.");
        }

        if (pallet.RowVersion != command.ExpectedRowVersion)
        {
            throw new WorkflowRuleException(
                "PREPARATION_VERSION_CONFLICT", "Bu palet siz bakarken değişti; ekranı yenileyin.");
        }

        if (pallet.TransportRequestId is null || boundRequest is null)
        {
            throw new WorkflowRuleException(
                "PREPARATION_DEMAND_REQUIRED", "Taşımaya hazır onayı için palet bir taleple eşleştirilmelidir.");
        }

        if (boundRequest.Status is not (TransportRequestStatus.Created or TransportRequestStatus.Validating or TransportRequestStatus.Queued))
        {
            throw new WorkflowRuleException(
                "PREPARATION_REQUEST_NOT_WAITING", "Bu talep için yeni hazırlık veya hazır onayı yapılamaz.");
        }

        if (pallet.ReadyAt is not null)
        {
            if (boundRequest.ReadinessState != TransportReadinessState.Ready)
            {
                throw new WorkflowRuleException(
                    "PREPARATION_READINESS_CONFLICT", "Palet ve talebin hazırlık durumu uyuşmuyor; ekranı yenileyin.");
            }

            await transaction.CommitAsync(cancellationToken);
            return pallet; // Already confirmed; a retry is not a second event.
        }

        if (boundRequest.ReadinessState != TransportReadinessState.WaitingPreparation)
        {
            throw new WorkflowRuleException(
                "PREPARATION_READINESS_CONFLICT", "Talep hazırlık beklemiyor; güncel durumu yenileyin.");
        }

        PreparationPalletRecord updated;
        await using (var update = new NpgsqlCommand("""
            UPDATE ops.preparation_pallets
            SET ready_at = GREATEST(clock_timestamp(), placed_at), ready_by = @actor, row_version = row_version + 1
            WHERE id = @id AND released_at IS NULL AND ready_at IS NULL
            RETURNING id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", command.PalletId);
            update.Parameters.AddWithValue("actor", command.ActorUserId);
            if (await update.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new WorkflowRuleException(
                    "PREPARATION_PALLET_CHANGED", "Hazırlık kaydı değişti; ekranı yenileyin.");
            }

            updated = await FindPalletAsync(connection, transaction, "p.id = @id", [("id", command.PalletId)], cancellationToken)
                ?? throw new WorkflowRuleException("PREPARATION_PALLET_NOT_FOUND", "Hazırlık kaydı okunamadı.");
        }

        // The request's readiness moves in the SAME transaction. A separate
        // call could leave a request dispatchable with nothing prepared, or a
        // pallet ready for a request that never opened.
        var readyAreaId = await ReadPalletAreaIdAsync(connection, transaction, command.PalletId, cancellationToken);
        var eventContext = OperationalEventContext.ForUser(command.ActorUserId, command.DeviceId, command.CorrelationId);
        await MarkRequestReadyInTransactionAsync(
            connection,
            transaction,
            boundRequest.Id,
            command.ActorUserId,
            readyAreaId,
            updated.Id,
            updated.PartId,
            updated.ContainerTypeId,
            cancellationToken,
            eventContext);
        await InsertPreparationEventAsync(connection, transaction, readyAreaId,
            updated.Id, "PALLET_MARKED_READY", updated.PartId, updated.ContainerTypeId, updated.TransportRequestId,
            command.ActorUserId, command.DeviceId, new { }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<PreparationPalletRecord> ReleaseAsync(
        ReleaseAreaCommand command, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var scope = await ReadPalletAreaScopeAsync(connection, transaction, command.PalletId, cancellationToken);
        await ValidateWarehouseActorAsync(connection, transaction, command.ActorUserId, command.DeviceId, scope, cancellationToken);

        // Keep the workflow-wide lock order: request first, then pallet. The
        // unlocked binding read is verified after both rows are locked.
        var boundRequestId = await ReadPalletRequestIdAsync(connection, transaction, command.PalletId, cancellationToken);
        TransportRequestSnapshot? boundRequest = null;
        if (boundRequestId is { } requestId)
        {
            boundRequest = await LockRequestAsync(connection, transaction, requestId, cancellationToken);
        }

        var pallet = await LockPalletAsync(connection, transaction, command.PalletId, cancellationToken);
        if (pallet.TransportRequestId != boundRequestId)
        {
            throw new WorkflowRuleException(
                "PREPARATION_PALLET_CHANGED", "Hazırlık kaydı değişti; ekranı yenileyin.");
        }

        var areaId = await ReadPalletAreaIdAsync(connection, transaction, command.PalletId, cancellationToken);
        var released = await ReadPalletReleasedAsync(connection, transaction, command.PalletId, cancellationToken);
        if (released)
        {
            await transaction.CommitAsync(cancellationToken);
            return pallet; // The area is already free; a retry frees nothing twice.
        }

        if (pallet.RowVersion != command.ExpectedRowVersion)
        {
            throw new WorkflowRuleException(
                "PREPARATION_VERSION_CONFLICT", "Bu palet siz bakarken değişti; ekranı yenileyin.");
        }

        // COLLECTED is physical evidence, not a shortcut for dispatch. It is
        // accepted only after the request has actually reached the executor.
        if (command.Reason == "COLLECTED")
        {
            if (pallet.ReadyAt is null)
            {
                throw new WorkflowRuleException(
                    "PREPARATION_PALLET_NOT_READY", "Hazır işaretlenmemiş palet toplanmış olarak kapatılamaz.");
            }

            if (boundRequest is null || boundRequest.Status is not (
                    TransportRequestStatus.SentToRds or TransportRequestStatus.Running or TransportRequestStatus.Completed))
            {
                throw new WorkflowRuleException(
                    "PREPARATION_COLLECTION_NOT_DISPATCHED",
                    "Robot görevi başlamadan palet toplanmış olarak kapatılamaz.");
            }
        }
        else if (boundRequest is not null)
        {
            if (boundRequest.Status is TransportRequestStatus.SentToRds or TransportRequestStatus.Running)
            {
                throw new WorkflowRuleException(
                    "PREPARATION_WITHDRAWAL_AFTER_DISPATCH",
                    "Robot görevi başladıktan sonra hazırlık geri çekilemez; fiziksel durum uzlaştırılmalıdır.");
            }

            if (boundRequest.Status == TransportRequestStatus.Completed)
            {
                throw new WorkflowRuleException(
                    "PREPARATION_WITHDRAWAL_AFTER_COMPLETION",
                    "Tamamlanan taşımanın paleti geri çekilmiş olarak kapatılamaz.");
            }

            if (boundRequest.Status is TransportRequestStatus.Created or TransportRequestStatus.Validating or TransportRequestStatus.Queued)
            {
                await using var reset = new NpgsqlCommand("""
                    UPDATE ops.transport_requests
                    SET readiness_state = 'WAITING_PREPARATION', ready_at = NULL,
                        machine_slot_reservation_active = CASE WHEN @keep_reservation THEN machine_slot_reservation_active ELSE false END,
                        updated_at = now()
                    WHERE id = @id
                      AND status IN ('CREATED','VALIDATING','QUEUED')
                    RETURNING id
                    """, connection, transaction);
                reset.Parameters.AddWithValue("id", boundRequest.Id);
                reset.Parameters.AddWithValue("keep_reservation", boundRequest.TaskType == TransportTaskType.BringEmpty);
                if (await reset.ExecuteScalarAsync(cancellationToken) is null)
                {
                    throw new WorkflowRuleException(
                        "PREPARATION_REQUEST_CHANGED", "Talep durumu değişti; ekranı yenileyin.");
                }

                await InsertEventAsync(
                    connection,
                    transaction,
                    boundRequest.Id,
                    command.ActorUserId,
                    TransportEventTypes.WarehousePreparationWithdrawn,
                    boundRequest.Status,
                    boundRequest.Status,
                    "Warehouse preparation was withdrawn before robot dispatch.",
                    new { preparation_area_id = areaId, preparation_pallet_id = pallet.Id },
                    cancellationToken,
                    OperationalEventContext.ForUser(command.ActorUserId, command.DeviceId));
            }
        }

        await using (var update = new NpgsqlCommand("""
            UPDATE ops.preparation_pallets
            SET released_at = GREATEST(clock_timestamp(), placed_at), released_by = @actor,
                released_reason = @reason, row_version = row_version + 1
            WHERE id = @id AND released_at IS NULL
            RETURNING id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", command.PalletId);
            update.Parameters.AddWithValue("actor", command.ActorUserId);
            update.Parameters.AddWithValue("reason", command.Reason);
            if (await update.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new WorkflowRuleException(
                    "PREPARATION_PALLET_CHANGED", "Hazırlık kaydı değişti; ekranı yenileyin.");
            }
        }

        var final = await FindPalletAsync(connection, transaction, "p.id = @id", [("id", command.PalletId)], cancellationToken)
            ?? throw new WorkflowRuleException("PREPARATION_PALLET_NOT_FOUND", "Hazırlık kaydı okunamadı.");
        await InsertPreparationEventAsync(connection, transaction, areaId, final.Id,
            command.Reason == "COLLECTED" ? "PALLET_COLLECTED" : "PALLET_RELEASED",
            final.PartId, final.ContainerTypeId, final.TransportRequestId,
            command.ActorUserId, command.DeviceId, new { reason = command.Reason }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return final;
    }

    // ---- helpers -----------------------------------------------------------

    private static PreparationPalletRecord ReadPallet(NpgsqlDataReader r, int offset) => new(
        Id: r.GetGuid(offset),
        ContainerTypeId: r.IsDBNull(offset + 1) ? null : r.GetGuid(offset + 1),
        ContainerCode: r.IsDBNull(offset + 2) ? null : r.GetString(offset + 2),
        Load: r.GetString(offset + 3),
        PartId: r.IsDBNull(offset + 4) ? null : r.GetGuid(offset + 4),
        PartCode: r.IsDBNull(offset + 5) ? null : r.GetString(offset + 5),
        TransportRequestId: r.IsDBNull(offset + 6) ? null : r.GetGuid(offset + 6),
        RequestNo: r.IsDBNull(offset + 7) ? null : r.GetInt64(offset + 7),
        TargetMachineCode: r.IsDBNull(offset + 8) ? null : r.GetString(offset + 8),
        PlacedAt: r.GetFieldValue<DateTimeOffset>(offset + 9),
        PlacedBy: r.GetGuid(offset + 10),
        ReadyAt: r.IsDBNull(offset + 11) ? null : r.GetFieldValue<DateTimeOffset>(offset + 11),
        RowVersion: r.GetInt64(offset + 12),
        RequestStatus: r.IsDBNull(offset + 13) ? null : r.GetString(offset + 13));

    private static async Task<PreparationPalletRecord?> FindPalletAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string predicate,
        (string Name, object Value)[] parameters, CancellationToken cancellationToken)
    {
        // Predicate is internal static SQL, never supplied by a client.
        await using var command = new NpgsqlCommand($"""
            SELECT {PalletColumns}
            FROM ops.preparation_pallets p
            {PalletJoins}
            WHERE {predicate}
            """, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPallet(reader, 0) : null;
    }

    private static async Task<PreparationPalletRecord> LockPalletAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid palletId, CancellationToken cancellationToken)
    {
        await using (var locking = new NpgsqlCommand(
            "SELECT 1 FROM ops.preparation_pallets WHERE id = @id FOR UPDATE", connection, transaction))
        {
            locking.Parameters.AddWithValue("id", palletId);
            if (await locking.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new WorkflowRuleException("PREPARATION_PALLET_NOT_FOUND", "Hazırlık kaydı bulunamadı.");
            }
        }

        return await FindPalletAsync(connection, transaction, "p.id = @id", [("id", palletId)], cancellationToken)
            ?? throw new WorkflowRuleException("PREPARATION_PALLET_NOT_FOUND", "Hazırlık kaydı bulunamadı.");
    }

    private static async Task<Guid?> ReadPalletRequestIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid palletId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT transport_request_id FROM ops.preparation_pallets WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", palletId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : (Guid)value;
    }

    private static async Task<Guid> ReadPalletAreaIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid palletId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT preparation_area_id FROM ops.preparation_pallets WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", palletId);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<bool> ReadPalletReleasedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid palletId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT released_at IS NOT NULL FROM ops.preparation_pallets WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", palletId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task EnsureAreaEnabledAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid areaId,
        string warehouseScope, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM ops.preparation_areas WHERE id = @id AND is_enabled AND warehouse_scope = @scope", connection, transaction);
        command.Parameters.AddWithValue("id", areaId);
        command.Parameters.AddWithValue("scope", warehouseScope);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new WorkflowRuleException("PREPARATION_AREA_INVALID", "Geçerli bir hazırlık alanı seçilmelidir.");
        }
    }

    /// <summary>
    /// Loads the demand from the database and runs the domain match against it.
    /// The client's claim about what the request wants is never trusted.
    /// </summary>
    private static async Task ValidateDemandAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid requestId,
        PlacePalletCommand command, string warehouseScope, CancellationToken cancellationToken)
    {
        await using var query = new NpgsqlCommand($"""
            SELECT r.machine_id, r.part_id,
                   COALESCE(r.container_type_id, machine_slot.default_container_type_id),
                   ppm.container_dimensions, tt.code, r.status, r.readiness_state
            FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            LEFT JOIN master.layout_slots machine_slot
                   ON machine_slot.id = CASE WHEN tt.code = 'BRING_EMPTY' THEN r.destination_slot_id ELSE r.source_slot_id END
            LEFT JOIN master.part_planning_metrics ppm
                   ON ppm.variant_id = r.variant_id
                  AND ppm.product_group_id = r.product_group_id
                  AND ppm.part_id = r.part_id
            WHERE r.id = @id
            """, connection, transaction);
        query.Parameters.AddWithValue("id", requestId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new WorkflowRuleException("REQUEST_NOT_FOUND", "Hazırlanacak talep bulunamadı.");
        }

        if (reader.IsDBNull(1))
        {
            throw new WorkflowRuleException(
                "PREPARATION_DEMAND_INCOMPLETE", "Talebin parça bilgisi eksik; hazırlık eşleştirilemez.");
        }

        var operation = WorkflowCode.ParseTaskType(reader.GetString(4));
        var expectedOperation = warehouseScope == WarehousePreparationScopes.FinishedGoods
            ? TransportTaskType.BringEmpty
            : TransportTaskType.BringFull;
        if (operation != expectedOperation)
        {
            throw new WorkflowRuleException(
                "PREPARATION_SCOPE_MISMATCH", "Talep seçilen depo hazırlık alanına ait değil.");
        }

        if (reader.IsDBNull(2)
            && (reader.IsDBNull(3) || string.IsNullOrWhiteSpace(reader.GetString(3))))
        {
            throw new WorkflowRuleException(
                "PREPARATION_CONTAINER_SPEC_REQUIRED",
                "Talebin palet/kasa kodu veya doğrulanmış ölçüsü bulunmuyor; hazırlık eşleştirilemez.");
        }

        var demand = new WarehousePreparationDemand(
            RequestId: requestId,
            MachineId: reader.GetGuid(0),
            PartId: reader.GetGuid(1),
            ContainerTypeId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
            RequestedLoad: operation == TransportTaskType.BringFull ? LoadState.Full : LoadState.Empty,
            Status: WorkflowCode.ParseTransportStatus(reader.GetString(5)),
            Readiness: WorkflowCode.ParseReadinessState(reader.GetString(6)));

        WarehousePreparationRules.EnsurePalletMatchesDemand(
            new WarehousePalletContent(command.ContainerTypeId, command.Load, command.PartId), demand);
    }

    private static async Task MarkRequestReadyInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        Guid actorUserId,
        Guid areaId,
        Guid palletId,
        Guid? partId,
        Guid? containerTypeId,
        CancellationToken cancellationToken,
        OperationalEventContext? eventContext = null)
    {
        var readyAt = DateTimeOffset.UtcNow;
        await using (var update = new NpgsqlCommand("""
            UPDATE ops.transport_requests
            SET readiness_state = 'READY', ready_at = @ready_at,
                container_type_id = COALESCE(container_type_id, @container_type_id),
                updated_at = now()
            WHERE id = @id
              AND status IN ('CREATED','VALIDATING','QUEUED')
              AND readiness_state = 'WAITING_PREPARATION'
            RETURNING id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", requestId);
            update.Parameters.AddWithValue("ready_at", readyAt);
            AddNullableUuid(update, "container_type_id", containerTypeId);
            if (await update.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new WorkflowRuleException(
                    "PREPARATION_REQUEST_CHANGED", "Talep durumu değişti; ekranı yenileyin.");
            }
        }

        await InsertEventAsync(
            connection,
            transaction,
            requestId,
            actorUserId,
            TransportEventTypes.WarehousePreparationReady,
            null,
            null,
            "Warehouse preparation completed in a preparation area; request marked READY.",
            new
            {
                preparation_area_id = areaId,
                preparation_pallet_id = palletId,
                part_id = partId,
                container_type_id = containerTypeId,
                ready_at = readyAt
            },
            cancellationToken,
            eventContext);
    }

    private static async Task ValidateWarehouseActorAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid actorId, Guid deviceId, string warehouseScope, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM auth.users u
            WHERE u.id = @actor AND u.is_active
              AND EXISTS (SELECT 1 FROM audit.client_devices d WHERE d.id = @device AND d.is_active)
              AND EXISTS (SELECT 1 FROM auth.user_roles ur JOIN auth.roles r ON r.id = ur.role_id
                          WHERE ur.user_id = u.id AND (
                              r.code IN ('ADMIN','SUPERVISOR','WAREHOUSE_OPERATOR')
                              OR (@scope = 'SEMI_FINISHED' AND r.code = 'SEMI_FINISHED_WAREHOUSE_OPERATOR')
                              OR (@scope = 'FINISHED_GOODS' AND r.code = 'FINISHED_GOODS_WAREHOUSE_OPERATOR')
                          ))
            """, connection, transaction);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("device", deviceId);
        command.Parameters.AddWithValue("scope", warehouseScope);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new WorkflowRuleException(
                "PREPARATION_ACCESS_DENIED", "Kullanıcı veya cihaz depo hazırlığı yapmaya yetkili değil.");
        }
    }

    private static async Task<string> ReadPalletAreaScopeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid palletId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT a.warehouse_scope
            FROM ops.preparation_pallets p
            JOIN ops.preparation_areas a ON a.id = p.preparation_area_id
            WHERE p.id = @id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", palletId);
        return await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new WorkflowRuleException("PREPARATION_PALLET_NOT_FOUND", "Hazırlık kaydı bulunamadı.");
    }

    private static async Task<bool> HasTransportEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid requestId,
        string eventType, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM ops.transport_request_events
            WHERE transport_request_id = @request AND event_type = @type
            LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("request", requestId);
        command.Parameters.AddWithValue("type", eventType);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task InsertPreparationEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid areaId, Guid palletId,
        string eventType, Guid? partId, Guid? containerTypeId, Guid? requestId,
        Guid actorUserId, Guid deviceId, object data, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO ops.preparation_events
                (preparation_area_id, preparation_pallet_id, event_type, part_id, container_type_id,
                 transport_request_id, actor_user_id, device_id, data_json)
            VALUES (@area, @pallet, @type, @part, @container, @request, @actor, @device, @data)
            """, connection, transaction);
        command.Parameters.AddWithValue("area", areaId);
        command.Parameters.AddWithValue("pallet", palletId);
        command.Parameters.AddWithValue("type", eventType);
        AddNullableUuid(command, "part", partId);
        AddNullableUuid(command, "container", containerTypeId);
        AddNullableUuid(command, "request", requestId);
        command.Parameters.AddWithValue("actor", actorUserId);
        command.Parameters.AddWithValue("device", deviceId);
        command.Parameters.Add("data", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(data);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Turns the database's own race verdict into the reason the operator needs.
    /// </summary>
    private static WorkflowRuleException TranslatePreparationConflict(PostgresException error) =>
        error.ConstraintName switch
        {
            "uq_preparation_area_active_pallet" => new WorkflowRuleException(
                "PREPARATION_AREA_NOT_VACANT", "Bu hazırlık alanında zaten bir palet var."),
            "uq_preparation_request_active_pallet" => new WorkflowRuleException(
                "PREPARATION_DEMAND_ALREADY_CLAIMED", "Bu talep için başka bir alanda hazırlık yapılıyor."),
            _ => new WorkflowRuleException("PREPARATION_CONFLICT", "Hazırlık kaydı çakıştı; ekranı yenileyin.")
        };
}
