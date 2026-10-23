using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Infrastructure.Postgres;

/// <summary>
/// The finished-goods store: what the robot left, and what the operator took.
///
/// A container appears here when the transport that carried it completes, from
/// inside that completion's own transaction, so the store can never disagree
/// with the transport that filled it. It leaves only when the operator states
/// they physically removed it; no transport status removes anything.
/// </summary>
public sealed partial class PostgresTransportWorkflowStore : IFinishedGoodsStore
{
    /// <summary>
    /// Called from the completion transition while it holds the request lock.
    /// Silently does nothing when the transport did not deliver into the store,
    /// which is every operation except a full container leaving a machine.
    ///
    /// REMOVE_FULL requests created by the machine-side container flow can never
    /// reach the "unlinked destination" silent exit: WP-08 refuses to create them
    /// unless their destination is already linked to an enabled store location
    /// (ResolveFinishedGoodsDestinationAsync). The exit remains only for REMOVE_FULL
    /// requests that bypass that flow (explicit-slot callers and legacy rows).
    /// Only a genuine COMPLETED transition calls this, inside its own transaction.
    /// </summary>
    private static async Task RecordStoreDeliveryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransportRequestSnapshot request,
        CancellationToken cancellationToken)
    {
        if (request.TaskType != TransportTaskType.RemoveFull) return;

        Guid? preferredLocationId = null;
        if (request.DestinationBusinessCode is { } businessCode)
        {
            await using var byCode = new NpgsqlCommand(
                """
                SELECT sl.id
                FROM ops.store_locations sl
                LEFT JOIN integration.profile_shared_endpoint_bindings b
                  ON b.navigation_profile_id = @profile
                 AND b.endpoint_class = 'FINISHED_GOODS'
                 AND b.business_code = @code
                WHERE sl.is_enabled
                  AND sl.area_purpose = 'DELIVERY'
                  AND (
                      (b.id IS NOT NULL AND (b.store_location_id = sl.id OR (b.store_location_id IS NULL AND sl.code = @code)))
                      OR (@profile IS NULL AND sl.code = @code)
                  )
                LIMIT 1
                """,
                connection,
                transaction);
            byCode.Parameters.AddWithValue("code", businessCode);
            AddNullableUuid(byCode, "profile", request.NavigationProfileId);
            if (await byCode.ExecuteScalarAsync(cancellationToken) is Guid found)
            {
                preferredLocationId = found;
            }
        }
        if (preferredLocationId is null && request.DestinationSlotId is { } destinationSlotId)
        {
            await using var lookup = new NpgsqlCommand("""
                SELECT store_location_id FROM ops.store_location_delivery_slots
                WHERE layout_slot_id = @slot
                """, connection, transaction);
            lookup.Parameters.AddWithValue("slot", destinationSlotId);
            if (await lookup.ExecuteScalarAsync(cancellationToken) is Guid found)
            {
                preferredLocationId = found;
            }
        }

        // Check if an active DROP reservation exists for this transport request
        Guid? reservedLocationId = null;
        await using (var getRes = new NpgsqlCommand("""
            SELECT store_location_id FROM ops.store_reservations
            WHERE transport_request_id = @req AND reservation_type = 'DROP' AND released_at IS NULL
            LIMIT 1
            """, connection, transaction))
        {
            getRes.Parameters.AddWithValue("req", request.Id);
            if (await getRes.ExecuteScalarAsync(cancellationToken) is Guid resLoc)
            {
                reservedLocationId = resLoc;
            }
        }

        // A request may only land on the exact area selected when it was created.
        // Never redirect a completed robot task to another vacant pallet position.
        if (preferredLocationId is null) return;
        if (reservedLocationId is { } reserved && reserved != preferredLocationId)
            throw new WorkflowRuleException("STORE_RESERVATION_MISMATCH", "Mamul alanı rezervasyonu görev hedefiyle eşleşmiyor.");

        var locationId = reservedLocationId ?? preferredLocationId.Value;
        await using (var lockLocation = new NpgsqlCommand("""
            SELECT 1
            FROM ops.store_locations sl
            WHERE sl.id = @location
              AND sl.is_enabled
              AND sl.area_purpose = 'DELIVERY'
              AND NOT EXISTS (
                  SELECT 1 FROM ops.store_units u
                  WHERE u.store_location_id = sl.id AND u.removed_at IS NULL)
            FOR UPDATE OF sl
            """, connection, transaction))
        {
            lockLocation.Parameters.AddWithValue("location", locationId);
            if (await lockLocation.ExecuteScalarAsync(cancellationToken) is null)
                throw new WorkflowRuleException(
                    "FINISHED_GOODS_STORE_FULL",
                    "Görev için seçilen mamul alanı artık boş değil. Teslimat başka alana yönlendirilmedi.");
        }

        Guid unitId;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO ops.store_units
                (store_location_id, transport_request_id, part_id, container_type_id, machine_id, load_state)
            VALUES (@location, @request, @part, @container, @machine, 'FULL')
            -- Reconciliation can replay a completion. One delivery per
            -- transport, or the store looks twice as full as it is.
            ON CONFLICT (transport_request_id) WHERE transport_request_id IS NOT NULL
            DO NOTHING
            RETURNING id
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("location", locationId);
            insert.Parameters.AddWithValue("request", request.Id);
            AddNullableUuid(insert, "part", request.PartId);
            AddNullableUuid(insert, "container", request.ContainerTypeId);
            insert.Parameters.AddWithValue("machine", request.MachineId);
            if (await insert.ExecuteScalarAsync(cancellationToken) is not Guid inserted) return;
            unitId = inserted;
        }

        // Release DROP reservation upon successful placement
        await using (var release = new NpgsqlCommand("""
            UPDATE ops.store_reservations
            SET released_at = clock_timestamp()
            WHERE transport_request_id = @req AND released_at IS NULL
            """, connection, transaction))
        {
            release.Parameters.AddWithValue("req", request.Id);
            await release.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertStoreEventAsync(connection, transaction, locationId, unitId, "UNIT_DELIVERED",
            request.PartId, request.ContainerTypeId, request.Id,
            actorUserId: null, deviceId: null, new { source = "TRANSPORT_COMPLETED" }, cancellationToken);
        await EnqueueFinishedGoodsNotificationAsync(
            connection, transaction, locationId, unitId, request, cancellationToken);
    }

    private static async Task EnqueueFinishedGoodsNotificationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid locationId,
        Guid unitId,
        TransportRequestSnapshot request,
        CancellationToken cancellationToken)
    {
        string displayName = "";
        string? partCode = null;
        string? machineCode = null;
        await using (var lookup = new NpgsqlCommand(
            """
            SELECT l.display_name, p.part_code, m.code
            FROM ops.store_locations l
            LEFT JOIN master.parts p ON p.id = @part
            LEFT JOIN master.machines m ON m.id = @machine
            WHERE l.id = @location
            """,
            connection,
            transaction))
        {
            lookup.Parameters.AddWithValue("location", locationId);
            AddNullableUuid(lookup, "part", request.PartId);
            lookup.Parameters.AddWithValue("machine", request.MachineId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                displayName = reader.GetString(0);
                partCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                machineCode = reader.IsDBNull(2) ? null : reader.GetString(2);
            }

            reader.Close();
        }

        var data = new Dictionary<string, string>
        {
            ["notificationType"] = FinishedGoodsNotificationCopy.NotificationType,
            ["unitId"] = unitId.ToString("D"),
            ["locationId"] = locationId.ToString("D"),
            ["locationName"] = displayName,
            ["requestNo"] = request.RequestNo.ToString()
        };
        if (!string.IsNullOrWhiteSpace(partCode)) data["partCode"] = partCode;
        if (!string.IsNullOrWhiteSpace(machineCode)) data["machineCode"] = machineCode;

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO integration.notification_outbox
                (event_key, registration_id, notification_type, title, body, data_json)
            SELECT @event_key, reg.id, @notification_type, @title, @body, @data
            FROM integration.device_notification_registrations reg
            JOIN auth.users u ON u.id = reg.user_id AND u.is_active
            JOIN audit.client_devices d ON d.id = reg.device_id AND d.is_active
            WHERE reg.is_active
              AND EXISTS (
                  SELECT 1
                  FROM auth.user_roles ur
                  JOIN auth.roles role ON role.id = ur.role_id
                  WHERE ur.user_id = reg.user_id
                    AND role.code = ANY(@roles))
            ON CONFLICT (event_key, registration_id) DO NOTHING
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("event_key", FinishedGoodsNotificationCopy.EventKey(unitId));
        insert.Parameters.AddWithValue("notification_type", FinishedGoodsNotificationCopy.NotificationType);
        insert.Parameters.AddWithValue("title", FinishedGoodsNotificationCopy.Title(displayName));
        insert.Parameters.AddWithValue("body", FinishedGoodsNotificationCopy.Body(partCode, machineCode, request.RequestNo));
        insert.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(data) });
        insert.Parameters.Add(new NpgsqlParameter("roles", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = FinishedGoodsNotificationAuthorization.RecipientRoles
        });
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// When a BRING_EMPTY transport completes, an empty container has been picked
    /// from the finished-goods store / empty pool and delivered to the machine slot.
    /// The exact pallet captured by the request's PICKUP reservation is removed;
    /// another empty pallet is never substituted during completion.
    /// </summary>
    private static async Task RecordStoreEmptyCollectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransportRequestSnapshot request,
        CancellationToken cancellationToken)
    {
        if (request.TaskType != TransportTaskType.BringEmpty) return;

        // New finished-goods preparation flow: the exact empty pallet is
        // request-bound in ops.preparation_pallets and remains there until the
        // warehouse confirms physical collection. It is not a store unit and
        // therefore has no legacy PICKUP reservation to remove here.
        await using (var prepared = new NpgsqlCommand("""
            SELECT 1 FROM ops.preparation_pallets
            WHERE transport_request_id = @req
              AND released_at IS NULL AND ready_at IS NOT NULL
            LIMIT 1
            """, connection, transaction))
        {
            prepared.Parameters.AddWithValue("req", request.Id);
            if (await prepared.ExecuteScalarAsync(cancellationToken) is not null) return;
        }

        Guid? unitId = null;
        Guid? locationId = null;

        // Check if an active PICKUP reservation exists for this transport request
        await using (var getRes = new NpgsqlCommand("""
            SELECT sr.store_unit_id, sr.store_location_id FROM ops.store_reservations sr
            WHERE sr.transport_request_id = @req AND sr.reservation_type = 'PICKUP' AND sr.released_at IS NULL
            LIMIT 1
            """, connection, transaction))
        {
            getRes.Parameters.AddWithValue("req", request.Id);
            await using var reader = await getRes.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0)) unitId = reader.GetGuid(0);
                locationId = reader.GetGuid(1);
            }
        }

        if (unitId is null || locationId is null)
        {
            // If the request was prepared without a direct store reservation (e.g. simulation or deferred source),
            // do not fail completion when destination machine slot is valid.
            await using var unreservedCheck = new NpgsqlCommand(
                "SELECT 1 FROM ops.transport_requests WHERE id = @req AND source_slot_id IS NULL AND destination_slot_id IS NOT NULL",
                connection, transaction);
            unreservedCheck.Parameters.AddWithValue("req", request.Id);
            if (await unreservedCheck.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return;
            }

            throw new WorkflowRuleException(
                "STORE_PICKUP_RESERVATION_MISSING",
                "Boş kasa görevinin fiziksel palet rezervasyonu bulunamadı.");
        }

        await using (var lockUnit = new NpgsqlCommand("""
            SELECT 1 FROM ops.store_units
            WHERE id = @id AND store_location_id = @location
              AND load_state = 'EMPTY' AND removed_at IS NULL
            FOR UPDATE
            """, connection, transaction))
        {
            lockUnit.Parameters.AddWithValue("id", unitId.Value);
            lockUnit.Parameters.AddWithValue("location", locationId.Value);
            if (await lockUnit.ExecuteScalarAsync(cancellationToken) is null)
                throw new WorkflowRuleException(
                    "STORE_PICKUP_UNIT_MISSING",
                    "Rezerve edilen boş kasa artık kaynak alanda bulunmuyor.");
        }

        await using var removeCmd = new NpgsqlCommand(
            """
            UPDATE ops.store_units
            SET removed_at = clock_timestamp(),
                removed_by = NULL,
                removed_device_id = NULL,
                removal_source = 'ROBOT',
                row_version = row_version + 1
            WHERE id = @id AND removed_at IS NULL
            """,
            connection,
            transaction);
        removeCmd.Parameters.AddWithValue("id", unitId.Value);
        if (await removeCmd.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new WorkflowRuleException("STORE_PICKUP_UNIT_MISSING", "Rezerve edilen boş kasa kaldırılamadı.");

        // Release PICKUP reservation
        await using (var release = new NpgsqlCommand("""
            UPDATE ops.store_reservations
            SET released_at = clock_timestamp()
            WHERE transport_request_id = @req AND released_at IS NULL
            """, connection, transaction))
        {
            release.Parameters.AddWithValue("req", request.Id);
            await release.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertStoreEventAsync(
            connection,
            transaction,
            locationId.Value,
            unitId.Value,
            "UNIT_REMOVED",
            partId: null,
            containerTypeId: request.ContainerTypeId,
            transportRequestId: request.Id,
            actorUserId: request.RequestedBy,
            deviceId: null,
            data: new { source = "BRING_EMPTY_COMPLETED", transport_request_id = request.Id },
            cancellationToken);
    }

    private const string FinishedGoodsDestinationNotConfiguredMessage =
        "Bitmiş ürün teslim alanı henüz yapılandırılmamış. Sistem yöneticisine başvurun.";

    /// <summary>
    /// WP-08. Resolves, server-side only, the physical slot a Kasa Doldu
    /// REMOVE_FULL will be delivered to, and proves it is linked to an enabled
    /// finished-goods store location — so a completed delivery can never leave
    /// the warehouse unaware of it.
    ///
    /// Resolution follows the configured task template's destination slot group
    /// by priority, otherwise the layout's enabled DROP positions, and selects
    /// the first enabled store location with no active pallet or reservation. No
    /// slot, WorkSite or location is invented; a missing or disabled link
    /// fails closed with FINISHED_GOODS_DESTINATION_NOT_CONFIGURED. The mapping
    /// and location rows are locked FOR SHARE so a concurrent disable or unlink
    /// cannot slip between this check and the commit.
    /// </summary>
    private static async Task<Guid> ResolveFinishedGoodsDestinationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CreateTransportRequest request,
        CancellationToken cancellationToken)
    {
        var destinationSlotId = request.DestinationSlotId
            ?? await ResolveFinishedGoodsDestinationSlotAsync(
                connection, transaction, request.LayoutId, request.VariantId, request.ProductGroupId,
                requireAvailableCapacity: true, cancellationToken);
        if (destinationSlotId is null && request.DestinationSlotId is null)
        {
            var configuredSlotId = await ResolveFinishedGoodsDestinationSlotAsync(
                connection, transaction, request.LayoutId, request.VariantId, request.ProductGroupId,
                requireAvailableCapacity: false, cancellationToken);
            if (configuredSlotId is not null)
            {
                throw new WorkflowRuleException(
                    "FINISHED_GOODS_STORE_FULL",
                    "Bitmiş ürün alanında boş veya rezerve edilmemiş palet yeri yok.");
            }
        }
        if (destinationSlotId is not { } slotId
            || !await IsFinishedGoodsDeliverySlotMappedAsync(connection, transaction, slotId, lockRows: transaction is not null, cancellationToken))
        {
            throw new WorkflowRuleException("FINISHED_GOODS_DESTINATION_NOT_CONFIGURED", FinishedGoodsDestinationNotConfiguredMessage);
        }

        return slotId;
    }

    private static async Task<Guid?> ResolveFinishedGoodsDestinationSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid layoutId,
        Guid variantId,
        Guid? productGroupId,
        bool requireAvailableCapacity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH template AS (
                SELECT t.destination_slot_group_id
                FROM master.task_templates t
                JOIN master.task_types tt ON tt.id = t.task_type_id AND tt.code = 'REMOVE_FULL'
                WHERE t.variant_id = @variant_id
                  AND t.is_active
                  AND (t.product_group_id = @product_group_id OR t.product_group_id IS NULL)
                ORDER BY (t.product_group_id = @product_group_id) DESC NULLS LAST, t.id
                LIMIT 1
            )
            SELECT ls.id
            FROM master.layout_slots ls
            LEFT JOIN master.slot_group_members sgm ON sgm.layout_slot_id = ls.id
            LEFT JOIN template ON true
            JOIN ops.store_location_delivery_slots d ON d.layout_slot_id = ls.id
            JOIN ops.store_locations sl ON sl.id = d.store_location_id AND sl.is_enabled AND sl.area_purpose = 'DELIVERY'
            WHERE ls.layout_id = @layout_id
              AND ls.is_enabled
              AND ls.slot_role <> 'MACHINE'
              AND ((template.destination_slot_group_id IS NOT NULL
                    AND sgm.slot_group_id = template.destination_slot_group_id)
                   OR (template.destination_slot_group_id IS NULL AND ls.slot_role = 'DROP'))
              AND (NOT @require_available OR (
                    NOT EXISTS (
                        SELECT 1 FROM ops.store_units u
                        WHERE u.store_location_id = sl.id AND u.removed_at IS NULL)
                    AND NOT EXISTS (
                        SELECT 1 FROM ops.store_reservations sr
                        WHERE sr.store_location_id = sl.id AND sr.released_at IS NULL)))
            ORDER BY sgm.priority, ls.display_order, ls.id
            LIMIT 1
            """ + (transaction is not null && requireAvailableCapacity ? " FOR UPDATE OF sl SKIP LOCKED" : string.Empty),
            connection,
            transaction);
        command.Parameters.AddWithValue("layout_id", layoutId);
        command.Parameters.AddWithValue("variant_id", variantId);
        AddNullableUuid(command, "product_group_id", productGroupId);
        command.Parameters.AddWithValue("require_available", requireAvailableCapacity);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid id ? id : null;
    }

    private static async Task<bool> IsFinishedGoodsDeliverySlotMappedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid layoutSlotId,
        bool lockRows,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
             SELECT 1
             FROM ops.store_location_delivery_slots d
             JOIN ops.store_locations sl ON sl.id = d.store_location_id
             WHERE d.layout_slot_id = @slot_id AND sl.is_enabled AND sl.area_purpose = 'DELIVERY'
             {(lockRows ? "FOR SHARE OF d, sl" : string.Empty)}
             """,
            connection,
            transaction);
        command.Parameters.AddWithValue("slot_id", layoutSlotId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>
    /// Read-only form for the PDA projection: whether a Kasa Doldu for this
    /// product group could currently resolve a mapped finished-goods destination.
    /// </summary>
    private async Task<string?> FinishedGoodsAvailabilityCodeAsync(
        NpgsqlConnection connection, Guid machineId, Guid layoutId, Guid variantId,
        Guid productGroupId, CancellationToken cancellationToken)
    {
        await using var registry = new NpgsqlCommand("""
            SELECT n.id, n.status FROM ops.machine_runtime_state runtime
            JOIN master.navigation_profiles n ON n.id = runtime.active_navigation_id
            WHERE runtime.machine_id = @machine AND n.config_json->>'commissioningRegistry' = @registry
            """, connection);
        registry.Parameters.AddWithValue("machine", machineId);
        registry.Parameters.AddWithValue("registry", CommissioningRegistry);
        Guid? profileId = null;
        string? status = null;
        await using (var reader = await registry.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken)) { profileId = reader.GetGuid(0); status = reader.GetString(1); }
        if (profileId is { } id)
        {
            var observations = await ReadSharedCandidatesAsync(connection, null, id, "FINISHED_GOODS", cancellationToken);
            var candidates = observations.Where(c => c.ProfileId == id).ToArray();
            if (status != "PUBLISHED" || candidates.Length == 0 || !candidates.Any(c => c.StoreEnabled))
                return "FINISHED_GOODS_DESTINATION_NOT_CONFIGURED";
            foreach (var candidate in candidates)
                if (IsPhysicallyAvailable(candidate, observations)
                    && !await IsSharedEndpointOccupiedGloballyAsync(connection, null, candidate.BusinessCode, cancellationToken)) return null;
            return SharedUnavailable("FINISHED_GOODS", candidates).Code;
        }
        var slotId = await ResolveFinishedGoodsDestinationSlotAsync(
            connection, null, layoutId, variantId, productGroupId, requireAvailableCapacity: true, cancellationToken);
        if (slotId is not null) return null;
        var configuredSlotId = await ResolveFinishedGoodsDestinationSlotAsync(
            connection, null, layoutId, variantId, productGroupId, requireAvailableCapacity: false, cancellationToken);
        return configuredSlotId is null
            ? "FINISHED_GOODS_DESTINATION_NOT_CONFIGURED"
            : "FINISHED_GOODS_STORE_FULL";
    }

    public async Task<IReadOnlyList<StoreLocationRecord>> ListLocationsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadLocationsAsync(connection, null, locationId: null, cancellationToken);
    }

    public async Task<StoreLocationRecord> RemoveUnitAsync(
        RemoveStoreUnitCommand command, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ValidateFinishedGoodsActorAsync(connection, transaction, command.ActorUserId, command.DeviceId, cancellationToken);

        Guid locationId;
        long observedRowVersion;
        bool observedRemoved;
        await using (var peek = new NpgsqlCommand("""
            SELECT store_location_id, row_version, removed_at IS NOT NULL
            FROM ops.store_units WHERE id = @id
            """, connection, transaction))
        {
            peek.Parameters.AddWithValue("id", command.UnitId);
            await using var reader = await peek.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new WorkflowRuleException("STORE_UNIT_NOT_FOUND", "Depo kaydı bulunamadı.");
            }

            locationId = reader.GetGuid(0);
            observedRowVersion = reader.GetInt64(1);
            observedRemoved = reader.GetBoolean(2);
        }

        if (observedRemoved)
        {
            // A retry removes nothing twice.
            var current = await ReadLocationsAsync(connection, transaction, locationId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return current.Single();
        }

        if (observedRowVersion != command.ExpectedRowVersion)
        {
            throw new WorkflowRuleException(
                "STORE_VERSION_CONFLICT", "Bu kayıt siz bakarken değişti; ekranı yenileyin.");
        }

        var reconciledReservation = await ReconcileTerminalStoreReservationAsync(
            connection, transaction, locationId, command.UnitId,
            command.ActorUserId, command.DeviceId, cancellationToken);

        // Robot completion locks the reservation before the exact store unit.
        // Reconciliation follows the same order above, then locks and verifies
        // the unit so the two paths cannot deadlock or remove a changed pallet.
        Guid? partId;
        Guid? containerTypeId;
        Guid? requestId;
        long rowVersion;
        bool alreadyRemoved;
        await using (var locking = new NpgsqlCommand("""
            SELECT store_location_id, part_id, container_type_id, transport_request_id,
                   row_version, removed_at IS NOT NULL
            FROM ops.store_units WHERE id = @id FOR UPDATE
            """, connection, transaction))
        {
            locking.Parameters.AddWithValue("id", command.UnitId);
            await using var reader = await locking.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new WorkflowRuleException("STORE_UNIT_NOT_FOUND", "Depo kaydı bulunamadı.");
            }

            if (reader.GetGuid(0) != locationId)
            {
                throw new WorkflowRuleException(
                    "STORE_VERSION_CONFLICT", "Bu kayıt siz bakarken değişti; ekranı yenileyin.");
            }

            partId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            containerTypeId = reader.IsDBNull(2) ? null : reader.GetGuid(2);
            requestId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
            rowVersion = reader.GetInt64(4);
            alreadyRemoved = reader.GetBoolean(5);
        }

        if (alreadyRemoved)
        {
            var current = await ReadLocationsAsync(connection, transaction, locationId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return current.Single();
        }

        if (rowVersion != command.ExpectedRowVersion)
        {
            throw new WorkflowRuleException(
                "STORE_VERSION_CONFLICT", "Bu kayıt siz bakarken değişti; ekranı yenileyin.");
        }

        await using (var checkReservation = new NpgsqlCommand("""
            SELECT sr.reservation_type FROM ops.store_reservations sr
            WHERE (sr.store_unit_id = @unitId OR sr.store_location_id = @locationId)
              AND sr.released_at IS NULL
            LIMIT 1
            """, connection, transaction))
        {
            checkReservation.Parameters.AddWithValue("unitId", command.UnitId);
            checkReservation.Parameters.AddWithValue("locationId", locationId);
            if (await checkReservation.ExecuteScalarAsync(cancellationToken) is string)
            {
                throw new WorkflowRuleException(
                    "UNIT_RESERVED_FOR_ROBOT",
                    "Bu kasa veya alan robot taşıması için rezerve edilmiştir. Kaldırılamaz.");
            }
        }

        await using (var update = new NpgsqlCommand("""
            UPDATE ops.store_units
            SET removed_at = GREATEST(clock_timestamp(), delivered_at), removed_by = @actor,
                removed_device_id = @device, removal_source = 'OPERATOR', row_version = row_version + 1
            WHERE id = @id AND removed_at IS NULL
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", command.UnitId);
            update.Parameters.AddWithValue("actor", command.ActorUserId);
            update.Parameters.AddWithValue("device", command.DeviceId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertStoreEventAsync(connection, transaction, locationId, command.UnitId, "UNIT_REMOVED",
            partId, containerTypeId, requestId, command.ActorUserId, command.DeviceId,
            new { reconciled_transport_reservation = reconciledReservation }, cancellationToken);

        var refreshed = await ReadLocationsAsync(connection, transaction, locationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return refreshed.Single();
    }

    public async Task<StoreLocationRecord> ClearLocationAsync(
        ClearStoreLocationCommand command, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ValidateFinishedGoodsActorAsync(connection, transaction, command.ActorUserId, command.DeviceId, cancellationToken);

        var reconciledReservation = await ReconcileTerminalStoreReservationAsync(
            connection, transaction, command.LocationId, storeUnitId: null,
            command.ActorUserId, command.DeviceId, cancellationToken);

        // Match robot delivery's reservation -> location lock order.
        await using (var exists = new NpgsqlCommand(
            "SELECT 1 FROM ops.store_locations WHERE id = @id FOR UPDATE", connection, transaction))
        {
            exists.Parameters.AddWithValue("id", command.LocationId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new WorkflowRuleException("STORE_LOCATION_NOT_FOUND", "Depo konumu bulunamadı.");
            }
        }

        await using (var checkReservation = new NpgsqlCommand("""
            SELECT sr.reservation_type FROM ops.store_reservations sr
            WHERE sr.store_location_id = @locationId
              AND sr.released_at IS NULL
            LIMIT 1
            """, connection, transaction))
        {
            checkReservation.Parameters.AddWithValue("locationId", command.LocationId);
            if (await checkReservation.ExecuteScalarAsync(cancellationToken) is string)
            {
                throw new WorkflowRuleException(
                    "LOCATION_RESERVED",
                    "Bu alan robot taşıması için rezerve edilmiştir. Temizlenemez.");
            }
        }

        // Lock every standing unit before touching any, so a delivery arriving
        // mid-clear is either fully included or left for the next clear.
        var cleared = new List<Guid>();
        await using (var locking = new NpgsqlCommand("""
            SELECT id FROM ops.store_units
            WHERE store_location_id = @location AND removed_at IS NULL
            ORDER BY delivered_at
            FOR UPDATE
            """, connection, transaction))
        {
            locking.Parameters.AddWithValue("location", command.LocationId);
            await using var reader = await locking.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) cleared.Add(reader.GetGuid(0));
        }

        if (cleared.Count > 1)
            throw new WorkflowRuleException(
                "STORE_LOCATION_INTEGRITY_CONFLICT",
                "Bu alanda birden fazla palet kaydı var. Fiziksel sayım yapıp kayıtları tek tek uzlaştırın.");

        if (cleared.Count > 0)
        {
            await using (var update = new NpgsqlCommand("""
                UPDATE ops.store_units
                SET removed_at = GREATEST(clock_timestamp(), delivered_at), removed_by = @actor,
                    removed_device_id = @device, removal_source = 'OPERATOR', row_version = row_version + 1
                WHERE id = ANY(@ids)
                """, connection, transaction))
            {
                update.Parameters.AddWithValue("ids", cleared.ToArray());
                update.Parameters.AddWithValue("actor", command.ActorUserId);
                update.Parameters.AddWithValue("device", command.DeviceId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

        }

        if (cleared.Count > 0 || reconciledReservation)
        {
            await InsertStoreEventAsync(connection, transaction, command.LocationId, storeUnitId: null,
                "LOCATION_CLEARED", partId: null, containerTypeId: null, transportRequestId: null,
                command.ActorUserId, command.DeviceId,
                new { removed_units = cleared.Count, reconciled_transport_reservation = reconciledReservation },
                cancellationToken);
        }

        var refreshed = await ReadLocationsAsync(connection, transaction, command.LocationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return refreshed.Single();
    }

    /// <summary>
    /// A failed/cancelled robot execution keeps its endpoint reserved because
    /// its physical result is unknown. A warehouse operator's explicit remove
    /// or clear action is the evidence that closes that hold. PICKUP holds can
    /// be reconciled by removing their exact pallet; clearing a location can
    /// reconcile either reservation shape.
    /// </summary>
    private static async Task<bool> ReconcileTerminalStoreReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid locationId,
        Guid? storeUnitId,
        Guid actorUserId,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        // Lock every active reservation for the location first. Non-terminal
        // robot work remains locked and is rejected by the caller; only a
        // terminal, explicitly uncertain reservation can be reconciled.
        var locked = new List<(Guid Id, bool ReconciliationRequired, string Status, string Type, Guid? UnitId)>();
        await using (var locking = new NpgsqlCommand(
            """
            SELECT sr.id, sr.reconciliation_required, r.status,
                   sr.reservation_type, sr.store_unit_id
            FROM ops.store_reservations sr
            JOIN ops.transport_requests r ON r.id = sr.transport_request_id
            WHERE sr.store_location_id = @location
              AND sr.released_at IS NULL
            ORDER BY sr.created_at, sr.id
            FOR UPDATE OF sr
            """,
            connection,
            transaction))
        {
            locking.Parameters.AddWithValue("location", locationId);
            await using var reader = await locking.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                locked.Add((
                    reader.GetGuid(0),
                    reader.GetBoolean(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4)));
            }
        }

        var reconciled = false;
        foreach (var reservation in locked)
        {
            var terminal = reservation.Status is "FAILED" or "CANCELLED";
            var matchesEvidence = storeUnitId is null
                || (reservation.Type == "PICKUP" && reservation.UnitId == storeUnitId);
            if (!reservation.ReconciliationRequired || !terminal || !matchesEvidence) continue;

            await using var command = new NpgsqlCommand(
                """
                UPDATE ops.store_reservations
                SET released_at = clock_timestamp(),
                    reconciled_at = clock_timestamp(),
                    reconciled_by = @actor,
                    reconciled_device_id = @device
                WHERE id = @id AND released_at IS NULL
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("id", reservation.Id);
            command.Parameters.AddWithValue("actor", actorUserId);
            command.Parameters.AddWithValue("device", deviceId);
            reconciled |= await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        return reconciled;
    }

    public async Task<StoreLocationRecord> RecordEmptyContainerAsync(
        RecordEmptyContainerCommand command, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ValidateFinishedGoodsActorAsync(connection, transaction, command.ActorUserId, command.DeviceId, cancellationToken);
        var key = command.IdempotencyKey.Trim();

        await using (var replay = new NpgsqlCommand("""
            SELECT 1 FROM ops.store_events
            WHERE store_location_id = @location
              AND event_type = 'EMPTY_CONTAINER_RECORDED'
              AND data_json->>'idempotency_key' = @key
            """, connection, transaction))
        {
            replay.Parameters.AddWithValue("location", command.LocationId);
            replay.Parameters.AddWithValue("key", key);
            if (await replay.ExecuteScalarAsync(cancellationToken) is not null)
            {
                var existing = await ReadLocationsAsync(connection, transaction, command.LocationId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return existing.Single();
            }
        }

        await using (var lockLocation = new NpgsqlCommand(
            "SELECT is_enabled FROM ops.store_locations WHERE id = @loc AND area_purpose = 'DELIVERY' FOR UPDATE",
            connection,
            transaction))
        {
            lockLocation.Parameters.AddWithValue("loc", command.LocationId);
            if (await lockLocation.ExecuteScalarAsync(cancellationToken) is not bool isEnabled || !isEnabled)
                throw new WorkflowRuleException("STORE_LOCATION_NOT_FOUND", "Depo konumu bulunamadı veya kapalı.");
        }

        await using (var checkOccupancy = new NpgsqlCommand("""
            SELECT
                EXISTS (SELECT 1 FROM ops.store_units WHERE store_location_id = @loc AND removed_at IS NULL),
                EXISTS (SELECT 1 FROM ops.store_reservations WHERE store_location_id = @loc AND released_at IS NULL)
            """, connection, transaction))
        {
            checkOccupancy.Parameters.AddWithValue("loc", command.LocationId);
            await using var reader = await checkOccupancy.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var hasUnits = reader.GetBoolean(0);
                var isReserved = reader.GetBoolean(1);
                if (hasUnits || isReserved)
                {
                    throw new WorkflowRuleException(
                        "LOCATION_NOT_AVAILABLE",
                        "Bu alan dolu veya rezerve edilmiş. Boş kasa bırakılamaz.");
                }
            }
        }

        Guid unitId;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO ops.store_units (store_location_id, load_state)
            SELECT id, 'EMPTY' FROM ops.store_locations
            WHERE id = @location AND is_enabled AND area_purpose = 'DELIVERY'
            RETURNING id
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("location", command.LocationId);
            if (await insert.ExecuteScalarAsync(cancellationToken) is not Guid created)
            {
                throw new WorkflowRuleException("STORE_LOCATION_NOT_FOUND", "Depo konumu bulunamadı veya kapalı.");
            }

            unitId = created;
        }

        await InsertStoreEventAsync(connection, transaction, command.LocationId, unitId, "EMPTY_CONTAINER_RECORDED",
            partId: null, containerTypeId: null, transportRequestId: null,
            command.ActorUserId, command.DeviceId, new { idempotency_key = key }, cancellationToken);
        var refreshed = await ReadLocationsAsync(connection, transaction, command.LocationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return refreshed.Single();
    }

    private static async Task<List<StoreLocationRecord>> ReadLocationsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid? locationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT l.id, l.code, l.display_name, l.capacity, l.is_enabled,
                   u.id, u.transport_request_id, r.request_no, u.part_id, p.part_code,
                   u.container_type_id, ct.code, u.machine_id, m.code,
                   u.delivered_at, u.row_version, u.load_state,
                   sr.id IS NOT NULL AS is_reserved, sr.reservation_type, sr.transport_request_id AS res_req_id
            FROM ops.store_locations l
            LEFT JOIN ops.store_reservations sr
                   ON sr.store_location_id = l.id AND sr.released_at IS NULL
            LEFT JOIN ops.store_units u
                   ON u.store_location_id = l.id AND u.removed_at IS NULL
            LEFT JOIN ops.transport_requests r ON r.id = u.transport_request_id
            LEFT JOIN master.parts p ON p.id = u.part_id
            LEFT JOIN master.container_types ct ON ct.id = u.container_type_id
            LEFT JOIN master.machines m ON m.id = u.machine_id
            WHERE l.area_purpose = 'DELIVERY'
              AND l.is_operator_visible
              AND (@location::uuid IS NULL OR l.id = @location)
            ORDER BY l.code, u.delivered_at
            """, connection, transaction);
        AddNullableUuid(command, "location", locationId);

        var locations = new List<StoreLocationRecord>();
        var units = new Dictionary<Guid, List<StoreUnitRecord>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            if (!units.ContainsKey(id))
            {
                units[id] = [];
                var isReserved = reader.GetBoolean(17);
                var reservationType = reader.IsDBNull(18) ? null : reader.GetString(18);
                var reservationReqId = reader.IsDBNull(19) ? (Guid?)null : reader.GetGuid(19);

                locations.Add(new StoreLocationRecord(
                    Id: id,
                    Code: reader.GetString(1),
                    DisplayName: reader.GetString(2),
                    Capacity: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    IsEnabled: reader.GetBoolean(4),
                    Units: units[id],
                    IsReserved: isReserved,
                    ReservationType: reservationType,
                    ReservationRequestId: reservationReqId));
            }

            if (reader.IsDBNull(5)) continue;
            units[id].Add(new StoreUnitRecord(
                Id: reader.GetGuid(5),
                TransportRequestId: reader.IsDBNull(6) ? null : reader.GetGuid(6),
                RequestNo: reader.IsDBNull(7) ? null : reader.GetInt64(7),
                PartId: reader.IsDBNull(8) ? null : reader.GetGuid(8),
                PartCode: reader.IsDBNull(9) ? null : reader.GetString(9),
                ContainerTypeId: reader.IsDBNull(10) ? null : reader.GetGuid(10),
                ContainerCode: reader.IsDBNull(11) ? null : reader.GetString(11),
                MachineId: reader.IsDBNull(12) ? null : reader.GetGuid(12),
                MachineCode: reader.IsDBNull(13) ? null : reader.GetString(13),
                DeliveredAt: reader.GetFieldValue<DateTimeOffset>(14),
                RowVersion: reader.GetInt64(15),
                LoadState: reader.IsDBNull(16) ? "FULL" : reader.GetString(16)));
        }

        return locations;
    }

    private static async Task InsertStoreEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid locationId,
        Guid? storeUnitId, string eventType, Guid? partId, Guid? containerTypeId,
        Guid? transportRequestId, Guid? actorUserId, Guid? deviceId, object data,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO ops.store_events
                (store_location_id, store_unit_id, event_type, part_id, container_type_id,
                 transport_request_id, actor_user_id, device_id, data_json)
            VALUES (@location, @unit, @type, @part, @container, @request, @actor, @device, @data)
            """, connection, transaction);
        command.Parameters.AddWithValue("location", locationId);
        AddNullableUuid(command, "unit", storeUnitId);
        command.Parameters.AddWithValue("type", eventType);
        AddNullableUuid(command, "part", partId);
        AddNullableUuid(command, "container", containerTypeId);
        AddNullableUuid(command, "request", transportRequestId);
        AddNullableUuid(command, "actor", actorUserId);
        AddNullableUuid(command, "device", deviceId);
        command.Parameters.Add("data", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(data);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ValidateFinishedGoodsActorAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid actorId, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM auth.users u
            WHERE u.id = @actor AND u.is_active
              AND EXISTS (SELECT 1 FROM audit.client_devices d WHERE d.id = @device AND d.is_active)
              AND EXISTS (SELECT 1 FROM auth.user_roles ur JOIN auth.roles r ON r.id = ur.role_id
                          WHERE ur.user_id = u.id AND r.code IN ('ADMIN','SUPERVISOR','WAREHOUSE_OPERATOR','FINISHED_GOODS_WAREHOUSE_OPERATOR'))
            """, connection, transaction);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("device", deviceId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new WorkflowRuleException(
                "STORE_ACCESS_DENIED", "Kullanıcı veya cihaz bitmiş ürün deposu işlemi yapmaya yetkili değil.");
        }
    }
}
