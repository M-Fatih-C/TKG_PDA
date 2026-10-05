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
    /// which is every operation except a full container leaving a machine, and
    /// every DROP position the customer has not linked to a store location yet.
    /// </summary>
    private static async Task RecordStoreDeliveryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransportRequestSnapshot request,
        CancellationToken cancellationToken)
    {
        if (request.TaskType != TransportTaskType.RemoveFull) return;
        if (request.DestinationSlotId is not { } destinationSlotId) return;

        Guid locationId;
        await using (var lookup = new NpgsqlCommand("""
            SELECT store_location_id FROM ops.store_location_delivery_slots
            WHERE layout_slot_id = @slot
            """, connection, transaction))
        {
            lookup.Parameters.AddWithValue("slot", destinationSlotId);
            if (await lookup.ExecuteScalarAsync(cancellationToken) is not Guid found) return;
            locationId = found;
        }

        Guid unitId;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO ops.store_units
                (store_location_id, transport_request_id, part_id, container_type_id, machine_id)
            VALUES (@location, @request, @part, @container, @machine)
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

        await InsertStoreEventAsync(connection, transaction, locationId, unitId, "UNIT_DELIVERED",
            request.PartId, request.ContainerTypeId, request.Id,
            actorUserId: null, deviceId: null, new { source = "TRANSPORT_COMPLETED" }, cancellationToken);
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

            locationId = reader.GetGuid(0);
            partId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            containerTypeId = reader.IsDBNull(2) ? null : reader.GetGuid(2);
            requestId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
            rowVersion = reader.GetInt64(4);
            alreadyRemoved = reader.GetBoolean(5);
        }

        if (alreadyRemoved)
        {
            // A retry removes nothing twice.
            var current = await ReadLocationsAsync(connection, transaction, locationId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return current.Single();
        }

        if (rowVersion != command.ExpectedRowVersion)
        {
            throw new WorkflowRuleException(
                "STORE_VERSION_CONFLICT", "Bu kayıt siz bakarken değişti; ekranı yenileyin.");
        }

        await using (var update = new NpgsqlCommand("""
            UPDATE ops.store_units
            SET removed_at = GREATEST(clock_timestamp(), delivered_at), removed_by = @actor,
                removed_device_id = @device, row_version = row_version + 1
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
            new { }, cancellationToken);

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

        await using (var exists = new NpgsqlCommand(
            "SELECT 1 FROM ops.store_locations WHERE id = @id", connection, transaction))
        {
            exists.Parameters.AddWithValue("id", command.LocationId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new WorkflowRuleException("STORE_LOCATION_NOT_FOUND", "Depo konumu bulunamadı.");
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

        if (cleared.Count > 0)
        {
            await using (var update = new NpgsqlCommand("""
                UPDATE ops.store_units
                SET removed_at = GREATEST(clock_timestamp(), delivered_at), removed_by = @actor,
                    removed_device_id = @device, row_version = row_version + 1
                WHERE id = ANY(@ids)
                """, connection, transaction))
            {
                update.Parameters.AddWithValue("ids", cleared.ToArray());
                update.Parameters.AddWithValue("actor", command.ActorUserId);
                update.Parameters.AddWithValue("device", command.DeviceId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertStoreEventAsync(connection, transaction, command.LocationId, storeUnitId: null,
                "LOCATION_CLEARED", partId: null, containerTypeId: null, transportRequestId: null,
                command.ActorUserId, command.DeviceId, new { removed_units = cleared.Count },
                cancellationToken);
        }

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
                   u.delivered_at, u.row_version
            FROM ops.store_locations l
            LEFT JOIN ops.store_units u
                   ON u.store_location_id = l.id AND u.removed_at IS NULL
            LEFT JOIN ops.transport_requests r ON r.id = u.transport_request_id
            LEFT JOIN master.parts p ON p.id = u.part_id
            LEFT JOIN master.container_types ct ON ct.id = u.container_type_id
            LEFT JOIN master.machines m ON m.id = u.machine_id
            WHERE (@location::uuid IS NULL OR l.id = @location)
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
                locations.Add(new StoreLocationRecord(
                    Id: id,
                    Code: reader.GetString(1),
                    DisplayName: reader.GetString(2),
                    Capacity: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    IsEnabled: reader.GetBoolean(4),
                    Units: units[id]));
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
                RowVersion: reader.GetInt64(15)));
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
