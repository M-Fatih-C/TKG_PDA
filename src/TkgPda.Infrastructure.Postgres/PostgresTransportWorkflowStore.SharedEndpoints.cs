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

    private static async Task RequireSharedCapacityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid profileId,
        string endpointClass,
        int required,
        string missingCode,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
             SELECT count(*)
             FROM integration.profile_shared_endpoint_bindings b
             JOIN integration.rds_worksites w ON w.id = b.rds_worksite_id
                                              AND w.navigation_profile_id = b.navigation_profile_id
             WHERE b.navigation_profile_id = @profile
               AND b.endpoint_class = @class
               AND w.last_sync_at IS NOT NULL
               AND COALESCE(w.last_disabled, 0) = 0
               AND COALESCE(w.last_sync_failed, 0) = 0
               AND NOT EXISTS (
                   SELECT 1
                   FROM ops.transport_requests r
                   WHERE r.navigation_id = @profile
                     AND r.destination_business_code = b.business_code
                     AND r.status IN ({ActiveStatusesSql}))
             """,
            connection,
            transaction);
        command.Parameters.AddWithValue("profile", profileId);
        command.Parameters.AddWithValue("class", endpointClass);
        var available = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (available < required)
        {
            throw new WorkflowRuleException(missingCode, SharedEndpointMessage(missingCode));
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
        int total = 0;
        int disabled = 0;
        string? code = null;
        await using (var command = new NpgsqlCommand(
            $"""
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
               AND NOT EXISTS (
                   SELECT 1
                   FROM ops.transport_requests r
                   WHERE r.navigation_id = @profile
                     AND r.destination_business_code = b.business_code
                     AND r.status IN ({ActiveStatusesSql}))
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
                total++;
                if (reader.GetInt32(1) > 0) disabled++;
                if (code is null && reader.GetBoolean(3))
                {
                    code = reader.GetString(0);
                }
            }
        }

        if (code is not null)
        {
            return code;
        }

        if (total == 0)
        {
            throw new WorkflowRuleException(missingCode, SharedEndpointMessage(missingCode));
        }

        if (disabled > 0)
        {
            throw new WorkflowRuleException("RDS_WORKSITE_DISABLED", "Bağlı WorkSite kapalı. Taşıma oluşturulmadı.");
        }

        throw new WorkflowRuleException("RDS_WORKSITE_SYNC_FAILED", "Bağlı WorkSite senkronu başarısız. Taşıma oluşturulmadı.");
    }

    private static string SharedEndpointMessage(string code) => code switch
    {
        "EMPTY_SOURCE_MAPPING_MISSING" => "Bu NAV profilinde boş kasa kaynağı bağlı değil.",
        "EMPTY_RETURN_MAPPING_MISSING" => "Bu NAV profilinde boş kasa dönüş alanı bağlı değil.",
        "FINISHED_GOODS_MAPPING_MISSING" => "Bu NAV profilinde bitmiş ürün alanı bağlı değil.",
        "NON_FINISHED_RETURN_MAPPING_MISSING" => "Bu NAV profilinde alt parça dönüş alanı bağlı değil.",
        _ => "Bu NAV profilinde gerekli fiziksel uç nokta bağlı değil."
    };
}
