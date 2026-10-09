using Npgsql;
using TkgPda.Domain;
using TkgPda.Application;

namespace TkgPda.Infrastructure.Postgres;

public sealed partial class PostgresTransportWorkflowStore
{
    private const string CommissioningRegistry = "tkg_rds_variant_map_naming_v1";

    /// <summary>
    /// A commissioning navigation profile resolves shared physical endpoints
    /// by business code, the same identity the readiness report evaluates.
    /// A layout slot on that request is machine-side bookkeeping only.
    /// Requests with no registry profile keep the layout-slot path.
    /// </summary>
    private static async Task<Guid?> ActiveNavigationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid machineId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT active_navigation_id FROM ops.machine_runtime_state WHERE machine_id = @machine",
            connection,
            transaction);
        command.Parameters.AddWithValue("machine", machineId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid id ? id : null;
    }

    private static bool IsSingleCapacityEndpoint(string endpointClass) =>
        endpointClass is "FINISHED_GOODS" or "NON_FINISHED_RETURN" or "EMPTY_RETURN";

    /// <summary>
    /// Shared Destination Capacity Model:
    /// - Business identity: Global logical physical destination (FG-01..19, RETURN-01..08, EMPTY-01..08).
    ///   Uniquely identifies physical capacity across all navigation profiles.
    /// - Navigation identity: Profile-specific RDS WorkSite (e.g. TKG_SH01A_V01_WS_RETURN01 vs TKG_PAH01A_V01_WS_RETURN01).
    /// - Runtime: Transport request stores destination_business_code (e.g. RETURN-01).
    /// - Capacity lock: Transaction advisory lock key "shared_destination:{businessCode}".
    ///   Lock key and global active-request occupancy query are 1-to-1 identical in physical scope.
    /// - Dispatch: Uses navigation_id + destination_business_code to resolve the profile-local WorkSite.
    /// </summary>
    private static string SharedDestinationLockKey(string businessCode) =>
        $"shared_destination:{businessCode}";

    private static async Task<bool> TryLockSharedEndpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string businessCode,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_try_advisory_xact_lock(hashtextextended(@key, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue("key", SharedDestinationLockKey(businessCode));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<bool> IsSharedEndpointOccupiedGloballyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string businessCode,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
             SELECT 1
             FROM ops.transport_requests r
             WHERE r.destination_business_code = @code
               AND r.status IN ({ActiveStatusesSql})
             LIMIT 1
             """,
            connection,
            transaction);
        command.Parameters.AddWithValue("code", businessCode);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    internal sealed record SharedCandidate(string BusinessCode, Guid ProfileId, RdsPhysicalEvidence Evidence, bool StoreEnabled)
    {
        // EMPTY_SOURCE is the established pool abstraction, not a destination.
        public bool SourceHealthy => Evidence.LastSyncAt is not null
            && (Evidence.Disabled ?? 0) == 0 && (Evidence.SyncFailed ?? 0) == 0;
    }

    internal static async Task<List<SharedCandidate>> ReadSharedCandidatesAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid profileId,
        string endpointClass, CancellationToken cancellationToken)
    {
        var rows = new List<SharedCandidate>();
        await using var command = new NpgsqlCommand($"""
            SELECT b.business_code, b.navigation_profile_id, w.rds_type, w.last_filled,
                   w.last_locked, w.last_disabled, w.last_sync_failed, w.last_sync_at,
                   b.endpoint_class <> 'FINISHED_GOODS' OR EXISTS (
                       SELECT 1 FROM ops.store_locations sl WHERE sl.code = b.business_code AND sl.is_enabled)
            FROM integration.profile_shared_endpoint_bindings b
            JOIN integration.rds_worksites w ON w.id = b.rds_worksite_id
                AND w.navigation_profile_id = b.navigation_profile_id
            WHERE b.endpoint_class = @class AND b.business_code IN (
                SELECT business_code FROM integration.profile_shared_endpoint_bindings
                WHERE navigation_profile_id = @profile AND endpoint_class = @class)
            ORDER BY b.business_code, b.navigation_profile_id, w.id
            {(transaction is null ? "" : "FOR SHARE OF b, w")}
            """, connection, transaction);
        command.Parameters.AddWithValue("profile", profileId);
        command.Parameters.AddWithValue("class", endpointClass);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetString(0), reader.GetGuid(1), new(
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)), reader.GetBoolean(8)));
        return rows;
    }

    private bool IsPhysicallyAvailable(SharedCandidate candidate, IReadOnlyList<SharedCandidate> observations) =>
        SharedPhysicalAvailability(candidate, observations, _physicalPolicy);

    internal static bool SharedPhysicalAvailability(SharedCandidate candidate, IReadOnlyList<SharedCandidate> observations,
        RdsPhysicalAvailabilityPolicy policy) => candidate.StoreEnabled && policy.IsDestinationAvailable(candidate.Evidence)
        && !observations.Any(other => other.BusinessCode == candidate.BusinessCode
            && other.ProfileId != candidate.ProfileId && policy.ContradictsEmptyDestination(other.Evidence));

    private WorkflowRuleException SharedUnavailable(string endpointClass, IReadOnlyList<SharedCandidate> candidates)
    {
        // Occupancy/cross-NAV contradictions and reservations are capacity, not configuration.
        if (candidates.Any(c => _physicalPolicy.State(c.Evidence) is "AVAILABLE" or "OCCUPIED"))
            return CapacityUnavailableException(endpointClass);
        var state = candidates.Select(c => _physicalPolicy.State(c.Evidence)).FirstOrDefault() ?? "UNKNOWN";
        var code = RdsPhysicalAvailabilityPolicy.ErrorCode(state) ?? "RDS_WORKSITE_STATE_UNKNOWN";
        return new(code, "Fiziksel WorkSite kullanıma uygun değil. Alanı ve RDS senkronunu kontrol edip ekranı yenileyin.");
    }

    private async Task RequireSharedCapacityAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid profileId,
        string endpointClass, int required, string missingCode, CancellationToken cancellationToken)
    {
        var observations = await ReadSharedCandidatesAsync(connection, transaction, profileId, endpointClass, cancellationToken);
        var candidates = observations.Where(c => c.ProfileId == profileId).ToArray();
        if (candidates.Length == 0) throw new WorkflowRuleException(missingCode, SharedEndpointMessage(missingCode));
        if (endpointClass == "FINISHED_GOODS" && !candidates.Any(c => c.StoreEnabled))
            throw new WorkflowRuleException("FINISHED_GOODS_DESTINATION_NOT_CONFIGURED", FinishedGoodsDestinationNotConfiguredMessage);
        if (!IsSingleCapacityEndpoint(endpointClass))
        {
            if (candidates.Count(c => c.SourceHealthy) >= required) return;
            throw new WorkflowRuleException(candidates.Any(c => c.Evidence.Disabled > 0)
                ? "RDS_WORKSITE_DISABLED" : "RDS_WORKSITE_SYNC_FAILED", "Boş kasa kaynağı kullanıma uygun değil.");
        }
        var acquired = 0;
        foreach (var candidate in candidates)
        {
            if (!IsPhysicallyAvailable(candidate, observations)) continue;
            if (!await TryLockSharedEndpointAsync(connection, transaction, candidate.BusinessCode, cancellationToken)) continue;
            if (await IsSharedEndpointOccupiedGloballyAsync(connection, transaction, candidate.BusinessCode, cancellationToken)) continue;
            if (++acquired == required) return;
        }
        throw SharedUnavailable(endpointClass, candidates);
    }

    private static async Task<bool> IsCommissioningRegistryProfileAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? navigationProfileId,
        CancellationToken cancellationToken)
    {
        if (navigationProfileId is not Guid profileId)
        {
            return false;
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT 1
            FROM master.navigation_profiles
            WHERE id = @id
              AND config_json ->> 'commissioningRegistry' = @registry
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", profileId);
        command.Parameters.AddWithValue("registry", CommissioningRegistry);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<CreateTransportRequest> AssignRegistrySharedEndpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateTransportRequest request,
        CancellationToken cancellationToken)
    {
        if (request.NavigationProfileId is not Guid profileId)
        {
            return request;
        }

        switch (request.TaskType)
        {
            case TransportTaskType.BringEmpty:
                await RequireSharedEndpointAsync(connection, transaction, profileId, "EMPTY_SOURCE", "EMPTY_SOURCE_MAPPING_MISSING", cancellationToken);
                return request with { SourceSlotId = null };
            case TransportTaskType.RemoveEmpty:
                var emptyReturn = await AllocateSharedBusinessCodeAsync(
                    connection, transaction, profileId, "EMPTY_RETURN", "EMPTY_RETURN_MAPPING_MISSING", cancellationToken);
                return request with { DestinationSlotId = null, DestinationBusinessCode = emptyReturn };
            case TransportTaskType.RemoveFull:
                var finished = request.EnforcesFinishedGoodsDestination
                    || await IsMainPartAsync(connection, transaction, request.ProductGroupId, request.PartId, cancellationToken);
                if (finished)
                {
                    var fg = await AllocateSharedBusinessCodeAsync(
                        connection, transaction, profileId, "FINISHED_GOODS", "FINISHED_GOODS_MAPPING_MISSING", cancellationToken);
                    return request with { DestinationSlotId = null, DestinationBusinessCode = fg };
                }

                var returning = await AllocateSharedBusinessCodeAsync(
                    connection, transaction, profileId, "NON_FINISHED_RETURN", "NON_FINISHED_RETURN_MAPPING_MISSING", cancellationToken);
                return request with { DestinationSlotId = null, DestinationBusinessCode = returning };
            default:
                return request;
        }
    }

    private static async Task<bool> IsMainPartAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? productGroupId,
        Guid? partId,
        CancellationToken cancellationToken)
    {
        if (productGroupId is not Guid groupId || partId is not Guid materialId)
        {
            return false;
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT 1 FROM master.product_groups
            WHERE id = @group AND main_part_id = @part
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("group", groupId);
        command.Parameters.AddWithValue("part", materialId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task RequireSharedEndpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid profileId,
        string endpointClass,
        string missingCode,
        CancellationToken cancellationToken)
    {
        _ = await AllocateSharedBusinessCodeAsync(connection, transaction, profileId, endpointClass, missingCode, cancellationToken);
    }

    private async Task<string> AllocateSharedBusinessCodeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid profileId,
        string endpointClass, string missingCode, CancellationToken cancellationToken)
    {
        var observations = await ReadSharedCandidatesAsync(connection, transaction, profileId, endpointClass, cancellationToken);
        var candidates = observations.Where(c => c.ProfileId == profileId).ToArray();
        if (candidates.Length == 0) throw new WorkflowRuleException(missingCode, SharedEndpointMessage(missingCode));
        if (endpointClass == "FINISHED_GOODS" && !candidates.Any(c => c.StoreEnabled))
            throw new WorkflowRuleException("FINISHED_GOODS_DESTINATION_NOT_CONFIGURED", FinishedGoodsDestinationNotConfiguredMessage);
        if (!IsSingleCapacityEndpoint(endpointClass))
        {
            var source = candidates.FirstOrDefault(c => c.SourceHealthy);
            if (source is not null) return source.BusinessCode;
            throw new WorkflowRuleException(candidates.Any(c => c.Evidence.Disabled > 0)
                ? "RDS_WORKSITE_DISABLED" : "RDS_WORKSITE_SYNC_FAILED", "Boş kasa kaynağı kullanıma uygun değil.");
        }
        foreach (var candidate in candidates)
        {
            if (!IsPhysicallyAvailable(candidate, observations)) continue;
            if (!await TryLockSharedEndpointAsync(connection, transaction, candidate.BusinessCode, cancellationToken)) continue;
            if (await IsSharedEndpointOccupiedGloballyAsync(connection, transaction, candidate.BusinessCode, cancellationToken)) continue;
            return candidate.BusinessCode;
        }
        throw SharedUnavailable(endpointClass, candidates);
    }

    private static string SharedEndpointMessage(string code) => code switch
    {
        "EMPTY_SOURCE_MAPPING_MISSING" => "Bu NAV profilinde boş kasa kaynağı bağlı değil.",
        "EMPTY_RETURN_MAPPING_MISSING" => "Bu NAV profilinde boş kasa dönüş alanı bağlı değil.",
        "FINISHED_GOODS_MAPPING_MISSING" => "Bu NAV profilinde bitmiş ürün alanı bağlı değil.",
        "NON_FINISHED_RETURN_MAPPING_MISSING" => "Bu NAV profilinde alt parça dönüş alanı bağlı değil.",
        _ => "Bu NAV profilinde gerekli fiziksel uç nokta bağlı değil."
    };

    private static WorkflowRuleException CapacityUnavailableException(string endpointClass) => endpointClass switch
    {
        "FINISHED_GOODS" => new WorkflowRuleException(
            "FINISHED_GOODS_CAPACITY_UNAVAILABLE",
            "Uygun boş bitmiş ürün alanı bulunamadı."),
        "NON_FINISHED_RETURN" => new WorkflowRuleException(
            "NON_FINISHED_RETURN_CAPACITY_UNAVAILABLE",
            "Uygun boş alt parça dönüş alanı bulunamadı."),
        "EMPTY_RETURN" => new WorkflowRuleException(
            "EMPTY_RETURN_CAPACITY_UNAVAILABLE",
            "Uygun boş kasa dönüş alanı bulunamadı."),
        _ => new WorkflowRuleException(
            "SHARED_ENDPOINT_CAPACITY_UNAVAILABLE",
            "Uygun boş hedef alan bulunamadı.")
    };
}
