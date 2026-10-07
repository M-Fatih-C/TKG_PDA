using Npgsql;
using TkgPda.Domain;

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
        NpgsqlTransaction transaction,
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
        NpgsqlTransaction transaction,
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
        NpgsqlTransaction transaction,
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

    private static async Task RequireSharedCapacityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid profileId,
        string endpointClass,
        int required,
        string missingCode,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string BusinessCode, bool Healthy, int Disabled, int SyncFailed)>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT b.business_code,
                   COALESCE(w.last_disabled, 0),
                   COALESCE(w.last_sync_failed, 0),
                   w.last_sync_at IS NOT NULL
                     AND COALESCE(w.last_disabled, 0) = 0
                     AND COALESCE(w.last_sync_failed, 0) = 0 AS healthy
            FROM integration.profile_shared_endpoint_bindings b
            JOIN integration.rds_worksites w ON w.id = b.rds_worksite_id
                                             AND w.navigation_profile_id = b.navigation_profile_id
            WHERE b.navigation_profile_id = @profile
              AND b.endpoint_class = @class
            ORDER BY b.business_code
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("profile", profileId);
            command.Parameters.AddWithValue("class", endpointClass);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((
                    reader.GetString(0),
                    reader.GetBoolean(3),
                    reader.GetInt32(1),
                    reader.GetInt32(2)));
            }
        }

        if (candidates.Count == 0)
        {
            throw new WorkflowRuleException(missingCode, SharedEndpointMessage(missingCode));
        }

        if (!IsSingleCapacityEndpoint(endpointClass))
        {
            var healthyCount = candidates.Count(c => c.Healthy);
            if (healthyCount < required)
            {
                if (candidates.Any(c => c.Disabled > 0))
                {
                    throw new WorkflowRuleException("RDS_WORKSITE_DISABLED", "Bağlı WorkSite kapalı. Taşıma oluşturulmadı.");
                }

                throw new WorkflowRuleException("RDS_WORKSITE_SYNC_FAILED", "Bağlı WorkSite senkronu başarısız. Taşıma oluşturulmadı.");
            }
            return;
        }

        // For single-capacity destinations:
        // Collect and reserve 'required' endpoints in deterministic lexicographical order.
        var acquired = new List<string>();
        var anyHealthy = false;
        foreach (var candidate in candidates)
        {
            if (!candidate.Healthy)
            {
                continue;
            }

            anyHealthy = true;

            if (await IsSharedEndpointOccupiedGloballyAsync(connection, transaction, candidate.BusinessCode, cancellationToken))
            {
                continue;
            }

            var locked = await TryLockSharedEndpointAsync(connection, transaction, candidate.BusinessCode, cancellationToken);
            if (!locked)
            {
                continue;
            }

            if (await IsSharedEndpointOccupiedGloballyAsync(connection, transaction, candidate.BusinessCode, cancellationToken))
            {
                continue;
            }

            acquired.Add(candidate.BusinessCode);
            if (acquired.Count == required)
            {
                break;
            }
        }

        if (acquired.Count < required)
        {
            if (!anyHealthy)
            {
                if (candidates.Any(c => c.Disabled > 0))
                {
                    throw new WorkflowRuleException("RDS_WORKSITE_DISABLED", "Bağlı WorkSite kapalı. Taşıma oluşturulmadı.");
                }

                throw new WorkflowRuleException("RDS_WORKSITE_SYNC_FAILED", "Bağlı WorkSite senkronu başarısız. Taşıma oluşturulmadı.");
            }

            throw CapacityUnavailableException(endpointClass);
        }
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

    private static async Task<CreateTransportRequest> AssignRegistrySharedEndpointAsync(
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

    private static async Task RequireSharedEndpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid profileId,
        string endpointClass,
        string missingCode,
        CancellationToken cancellationToken)
    {
        _ = await AllocateSharedBusinessCodeAsync(connection, transaction, profileId, endpointClass, missingCode, cancellationToken);
    }

    private static async Task<string> AllocateSharedBusinessCodeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid profileId,
        string endpointClass,
        string missingCode,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string BusinessCode, bool Healthy, int Disabled, int SyncFailed)>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT b.business_code,
                   COALESCE(w.last_disabled, 0),
                   COALESCE(w.last_sync_failed, 0),
                   w.last_sync_at IS NOT NULL
                     AND COALESCE(w.last_disabled, 0) = 0
                     AND COALESCE(w.last_sync_failed, 0) = 0 AS healthy
            FROM integration.profile_shared_endpoint_bindings b
            JOIN integration.rds_worksites w ON w.id = b.rds_worksite_id
                                             AND w.navigation_profile_id = b.navigation_profile_id
            WHERE b.navigation_profile_id = @profile
              AND b.endpoint_class = @class
            ORDER BY b.business_code
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("profile", profileId);
            command.Parameters.AddWithValue("class", endpointClass);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((
                    reader.GetString(0),
                    reader.GetBoolean(3),
                    reader.GetInt32(1),
                    reader.GetInt32(2)));
            }
        }

        if (candidates.Count == 0)
        {
            throw new WorkflowRuleException(missingCode, SharedEndpointMessage(missingCode));
        }

        // For non-single-capacity endpoints (e.g. EMPTY_SOURCE pool):
        if (!IsSingleCapacityEndpoint(endpointClass))
        {
            var healthySource = candidates.FirstOrDefault(c => c.Healthy);
            if (healthySource.BusinessCode is not null)
            {
                return healthySource.BusinessCode;
            }

            if (candidates.Any(c => c.Disabled > 0))
            {
                throw new WorkflowRuleException("RDS_WORKSITE_DISABLED", "Bağlı WorkSite kapalı. Taşıma oluşturulmadı.");
            }

            throw new WorkflowRuleException("RDS_WORKSITE_SYNC_FAILED", "Bağlı WorkSite senkronu başarısız. Taşıma oluşturulmadı.");
        }

        // For single-capacity destinations:
        // Deterministically iterate in lexicographical business_code order.
        // Lock the canonical endpoint identity via PostgreSQL transaction advisory lock,
        // then verify that no active request holds it globally.
        var anyHealthy = false;
        foreach (var candidate in candidates)
        {
            if (!candidate.Healthy)
            {
                continue;
            }

            anyHealthy = true;

            if (await IsSharedEndpointOccupiedGloballyAsync(connection, transaction, candidate.BusinessCode, cancellationToken))
            {
                continue;
            }

            var locked = await TryLockSharedEndpointAsync(connection, transaction, candidate.BusinessCode, cancellationToken);
            if (!locked)
            {
                continue;
            }

            if (await IsSharedEndpointOccupiedGloballyAsync(connection, transaction, candidate.BusinessCode, cancellationToken))
            {
                continue;
            }

            return candidate.BusinessCode;
        }

        if (!anyHealthy)
        {
            if (candidates.Any(c => c.Disabled > 0))
            {
                throw new WorkflowRuleException("RDS_WORKSITE_DISABLED", "Bağlı WorkSite kapalı. Taşıma oluşturulmadı.");
            }

            throw new WorkflowRuleException("RDS_WORKSITE_SYNC_FAILED", "Bağlı WorkSite senkronu başarısız. Taşıma oluşturulmadı.");
        }

        throw CapacityUnavailableException(endpointClass);
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
