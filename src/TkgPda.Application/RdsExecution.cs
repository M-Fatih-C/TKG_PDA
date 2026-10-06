using System.Text.Json;
using TkgPda.Domain;

namespace TkgPda.Application;

/// <summary>Fleet-scoped robot maintenance operations exposed to Admin/Supervisor.</summary>
public enum RdsRobotMaintenanceAction
{
    Pause,
    Resume,
    ClearErrors,
    SetDispatchable
}

/// <summary>
/// Outcome of an expert recovery action. <see cref="Vehicle"/> and
/// <see cref="TaskRecordId"/> are the server-resolved targets, echoed back so the
/// caller can see exactly what was acted on.
/// </summary>
public sealed record RdsExpertActionResult(
    Guid RequestId,
    string TaskRecordId,
    string Vehicle,
    int Code,
    string Message,
    bool Accepted);

public sealed record RdsExecutionSettings(
    bool DispatchEnabled,
    TimeSpan WorkSiteFreshness,
    string? EmptyContentValue,
    string? FullContentValue);

public sealed record RdsDispatchEndpoint(
    Guid? SlotId,
    string? SlotCode,
    string? SlotGroupCode,
    string? SlotRole,
    string? SiteId,
    int? Type,
    int? Filled,
    int? Locked,
    int? Disabled,
    int? SyncFailed,
    DateTimeOffset? LastSyncAt,
    bool IsMapped);

public sealed record RdsDispatchContext(
    TransportRequestSnapshot Request,
    string MachineCode,
    string VariantCode,
    string? ProductGroupCode,
    string? PartCode,
    string? ContainerCode,
    Guid TaskTemplateId,
    string? RdsTaskLabel,
    int TemplatePriority,
    JsonElement TemplateConfig,
    RdsDispatchEndpoint Source,
    RdsDispatchEndpoint Destination);

public sealed record RdsTaskProjection(
    Guid TransportRequestId,
    string TaskRecordId,
    string? TaskLabel,
    int? Status,
    string? AgvId,
    string? OutOrderNo,
    string? EndedReason,
    JsonElement RequestPayload,
    JsonElement ResponsePayload,
    JsonElement LastStatus,
    bool CancellationRequested,
    TransportRequestStatus RequestStatus);

public sealed record RdsTaskTemplateSummary(
    Guid Id,
    Guid VariantId,
    string VariantCode,
    Guid? ProductGroupId,
    string? ProductGroupCode,
    string Operation,
    string? RdsTaskLabel,
    int Priority,
    JsonElement Config,
    bool IsActive);

public sealed record RdsMappedWorkSite(
    Guid SlotId,
    Guid WorkSiteId,
    string SiteId,
    int? Type,
    int? Disabled,
    int? SyncFailed,
    DateTimeOffset? LastSyncAt);

public sealed record RdsNavigationProfileSummary(
    Guid Id,
    string Code,
    string Name,
    string Status,
    string? RobotMapName,
    string? SwitchTransitionStation,
    string? Vehicle,
    JsonElement ConfigJson);

public interface IRdsExecutionLease : IAsyncDisposable;

public interface IRdsExecutionStore
{
    Task<IReadOnlyList<Guid>> ListQueuedRequestIdsAsync(int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RdsTaskProjection>> ListActiveTasksAsync(int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> ListPendingWorkSiteRefreshRequestIdsAsync(int limit, CancellationToken cancellationToken = default);
    Task<IRdsExecutionLease?> TryAcquireRequestLeaseAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serialises TKG operations that target one physical ROBOT, as opposed to the
    /// per-request lease above. The request lease cannot cover this: two different
    /// TransportRequests can legitimately hold their own request leases while both
    /// resolving to the same robot, so a robot-scoped mutation validated under only a
    /// request lease can be invalidated by another request between the check and the
    /// call. It must be a distributed lock because TKG can run as more than one
    /// process; an in-process semaphore would not serialise across instances.
    ///
    /// <paramref name="robotLeaseKey"/> is <see cref="RdsRobotIdentity.RobotLeaseKey"/>
    /// and must already be resolved from telemetry so that a uuid and a vehicle_id
    /// naming the same robot cannot take two different leases.
    /// </summary>
    Task<IRdsExecutionLease?> TryAcquireRobotLeaseAsync(string robotLeaseKey, CancellationToken cancellationToken = default);
    Task<RdsDispatchContext?> GetDispatchContextAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<TransportRequestSnapshot?> FindRequestAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<RdsTaskProjection?> FindTaskByRequestAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<RdsTaskProjection?> FindTaskByRecordIdAsync(string taskRecordId, CancellationToken cancellationToken = default);
    Task EnsureTaskRecordAsync(Guid requestId, string taskRecordId, string taskLabel, JsonElement requestPayload, CancellationToken cancellationToken = default);
    Task RecordDispatchResponseAsync(Guid requestId, JsonElement responsePayload, DateTimeOffset syncedAt, CancellationToken cancellationToken = default);
    Task RecordTaskStatusAsync(Guid requestId, RdsWindTaskSnapshot task, DateTimeOffset syncedAt, CancellationToken cancellationToken = default);
    Task MarkCancellationRequestedAsync(Guid requestId, JsonElement responsePayload, DateTimeOffset requestedAt, CancellationToken cancellationToken = default);
    Task WriteExecutionAuditAsync(Guid requestId, string action, string code, string message, Guid correlationId, CancellationToken cancellationToken = default);
    Task WriteExecutionAuditAsync(Guid requestId, string action, string code, string message, Guid correlationId, object? extraDetails, CancellationToken cancellationToken = default);
    Task<RdsMappedWorkSite?> FindMappedWorkSiteAsync(Guid slotId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RdsTaskTemplateSummary>> ListTaskTemplatesAsync(CancellationToken cancellationToken = default);
    Task EnrichRemainingTimesAsync(IReadOnlyDictionary<string, double> agvRemainingTimes, CancellationToken cancellationToken = default);
    Task<RdsNavigationProfileSummary?> GetNavigationProfileSummaryAsync(Guid? profileId, Guid variantId, Guid machineId, CancellationToken cancellationToken = default);
    Task<bool> HasUnreleasedMapSwitchLockAsync(Guid requestId, string vehicle, CancellationToken cancellationToken = default);
    Task<RdsMapSwitchLockSnapshot?> GetUnreleasedMapSwitchLockAsync(Guid requestId, string vehicle, CancellationToken cancellationToken = default);
}

public sealed record RdsValidateTransportRequest(
    string TaskRecordId,
    string TaskLabel,
    IReadOnlyDictionary<string, object?> InputParams);

public interface IRobotTelemetryProvider
{
    Task<IReadOnlyDictionary<string, double>> GetRobotRemainingTimesAsync(CancellationToken cancellationToken = default);
}

public sealed record RdsValidationResult(
    bool Validated,
    bool TaskLabelExists,
    bool TaskRecordExists,
    int Code,
    string Message,
    JsonElement RawResponse);

public sealed record RdsCreateTransportRequest(
    string TaskRecordId,
    string TaskLabel,
    int Priority,
    IReadOnlyDictionary<string, object?> InputParams);

public sealed record RdsCreateTransportResult(
    bool Accepted,
    bool IdempotentReplay,
    int Code,
    string Message,
    JsonElement RawResponse);

public sealed record RdsStopTaskResult(int Code, string Message, JsonElement RawResponse);

public sealed record RdsContentUpdateResult(int Code, string Message, JsonElement RawResponse);

public interface IRdsExecutionClient
{
    bool DispatchEnabled { get; }
    Task<RdsValidationResult> ValidateTransportAsync(RdsValidateTransportRequest request, Guid correlationId, CancellationToken cancellationToken = default);
    Task<RdsCreateTransportResult> CreateTransportAsync(RdsCreateTransportRequest request, Guid correlationId, CancellationToken cancellationToken = default);
    Task<RdsStopTaskResult> StopTaskAsync(string taskRecordId, Guid correlationId, CancellationToken cancellationToken = default);
    Task<RdsContentUpdateResult> UpdateContentAsync(string siteId, string content, Guid correlationId, CancellationToken cancellationToken = default);
}

public sealed class RdsExecutionException(string code, string message, Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public string Code { get; } = code;
}

public sealed record RdsDispatchAttemptResult(
    Guid RequestId,
    string TaskRecordId,
    bool Dispatched,
    bool IdempotentReplay,
    string Code,
    string Message);

public sealed record RdsCancellationResult(
    TransportRequestSnapshot Request,
    bool PendingRdsConfirmation,
    bool WasIdempotentReplay);

public sealed record RdsLoadContentSyncResult(
    string Status,
    string? SiteId,
    string? ConfiguredContentValue,
    string? Message);

public sealed record RdsCallbackEvent(string EventType, JsonElement RawPayload, DateTimeOffset? EventTimestamp);

public sealed record RdsCallbackResult(bool Accepted, bool Mutated, string Code, string Message, string? TaskRecordId);

public static class RdsTaskInputBinder
{
    private static readonly HashSet<string> SupportedSources = new(StringComparer.Ordinal)
    {
        "SOURCE_RDS_SITE_ID",
        "DESTINATION_RDS_SITE_ID",
        "SOURCE_SLOT_GROUP_CODE",
        "DESTINATION_SLOT_GROUP_CODE",
        "MACHINE_CODE",
        "VARIANT_CODE",
        "PRODUCT_GROUP_CODE",
        "PART_CODE",
        "CONTAINER_CODE",
        "REQUEST_ID",
        "REQUEST_NO",
        "TASK_TYPE",
        "LITERAL"
    };

    public static IReadOnlyDictionary<string, object?> Bind(RdsDispatchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TemplateConfig.ValueKind != JsonValueKind.Object
            || !context.TemplateConfig.TryGetProperty("rdsInputBindings", out var bindings)
            || bindings.ValueKind != JsonValueKind.Array)
        {
            throw new RdsExecutionException("RDS_TASK_BINDINGS_REQUIRED", "The task template must define an rdsInputBindings array.");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var binding in bindings.EnumerateArray())
        {
            if (binding.ValueKind != JsonValueKind.Object)
            {
                throw new RdsExecutionException("RDS_TASK_BINDING_INVALID", "Every RDS input binding must be an object.");
            }

            var param = Text(binding, "param");
            var source = Text(binding, "source");
            var required = binding.TryGetProperty("required", out var requiredElement)
                && requiredElement.ValueKind == JsonValueKind.True;
            if (string.IsNullOrWhiteSpace(param) || param.Length > 200 || string.IsNullOrWhiteSpace(source) || !SupportedSources.Contains(source))
            {
                throw new RdsExecutionException("RDS_TASK_BINDING_INVALID", "An RDS input binding contains an invalid param or unsupported source.");
            }

            if (!values.TryAdd(param, Resolve(context, binding, source)))
            {
                throw new RdsExecutionException("RDS_TASK_BINDING_DUPLICATE", $"RDS input parameter '{param}' is configured more than once.");
            }

            if (required && IsMissing(values[param]))
            {
                throw new RdsExecutionException("RDS_TASK_INPUT_UNRESOLVED", $"Required RDS input '{param}' could not be resolved from {source}.");
            }
        }

        if (values.Count == 0)
        {
            throw new RdsExecutionException("RDS_TASK_BINDINGS_REQUIRED", "At least one explicit RDS input binding is required.");
        }

        return values;
    }

    private static object? Resolve(RdsDispatchContext context, JsonElement binding, string source) => source switch
    {
        "SOURCE_RDS_SITE_ID" => context.Source.SiteId,
        "DESTINATION_RDS_SITE_ID" => context.Destination.SiteId,
        "SOURCE_SLOT_GROUP_CODE" => context.Source.SlotGroupCode,
        "DESTINATION_SLOT_GROUP_CODE" => context.Destination.SlotGroupCode,
        "MACHINE_CODE" => context.MachineCode,
        "VARIANT_CODE" => context.VariantCode,
        "PRODUCT_GROUP_CODE" => context.ProductGroupCode,
        "PART_CODE" => context.PartCode,
        "CONTAINER_CODE" => context.ContainerCode,
        "REQUEST_ID" => context.Request.Id.ToString(),
        "REQUEST_NO" => context.Request.RequestNo,
        "TASK_TYPE" => context.Request.TaskType.ToDatabase(),
        "LITERAL" => binding.TryGetProperty("value", out var literal) ? JsonLiteral(literal) : null,
        _ => null
    };

    private static object? JsonLiteral(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw new RdsExecutionException("RDS_TASK_BINDING_INVALID", "LITERAL supports only scalar JSON values.")
    };

    private static bool IsMissing(object? value) => value is null || value is string text && string.IsNullOrWhiteSpace(text);

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;
}

public sealed class RdsExecutionCoordinator(
    IRdsExecutionStore store,
    IRdsExecutionClient executionClient,
    IRdsReadClient readClient,
    IRdsWorkSiteReconciler workSiteReconciler,
    TransportWorkflowService workflow,
    RdsExecutionSettings settings,
    TimeProvider timeProvider,
    IRobotTelemetryProvider? robotTelemetry = null,
    IRdsCoreNativeOrderClient? nativeOrderClient = null)
{
    /// <summary>
    /// Global physical mutation gate. <c>Rds:DispatchEnabled = false</c> means ZERO
    /// normal execution-triggered physical RDS mutation — <c>/setOrder</c>, native
    /// order dispatch, WindTask dispatch, <c>/lock</c>, <c>/switchMap</c>,
    /// <c>/unlock</c> and the automatic map preparation that surrounds them —
    /// regardless of which execution engine happens to be configured.
    /// Read-only telemetry, reconciliation and Admin diagnostics are deliberately
    /// unaffected, and cancellation (<c>/terminate</c>) stays reachable because it
    /// stops motion rather than starting it.
    /// </summary>
    private bool IsGlobalDispatchAllowed => settings.DispatchEnabled;

    /// <summary>
    /// Native engine <em>configuration</em> only: explicitly selected native mode,
    /// enabled client, and a valid configured RDSCore endpoint. This selects which
    /// engine reconciliation and cancellation talk to. It never authorises new
    /// physical motion on its own and must never bypass
    /// <see cref="IsGlobalDispatchAllowed"/>.
    /// </summary>
    private bool IsNativeMode => nativeOrderClient is not null
        && nativeOrderClient.Mode == RdsExecutionMode.RdsCoreNativeOrder
        && nativeOrderClient.MutationEnabled;

    /// <summary>
    /// Dispatch eligibility is the global gate AND at least one configured
    /// execution engine. Native enablement can never bypass the gate.
    /// </summary>
    public bool IsDispatchEnabled => IsGlobalDispatchAllowed
        && (executionClient.DispatchEnabled || IsNativeMode);

    public async Task<IReadOnlyList<RdsDispatchAttemptResult>> DispatchQueuedAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (!IsDispatchEnabled)
        {
            return [];
        }

        var ids = await store.ListQueuedRequestIdsAsync(Math.Clamp(limit, 1, 100), cancellationToken);
        var results = new List<RdsDispatchAttemptResult>();
        foreach (var id in ids)
        {
            var correlationId = Guid.NewGuid();
            try
            {
                var result = await DispatchOneAsync(id, correlationId, cancellationToken);
                if (result is not null)
                {
                    results.Add(result);
                }
            }
            catch (RdsExecutionException error)
            {
                await store.WriteExecutionAuditAsync(id, "RDS_DISPATCH_BLOCKED", error.Code, error.Message, correlationId, cancellationToken);
                results.Add(new(id, id.ToString(), false, false, error.Code, error.Message));
            }
            catch (RdsReadException error)
            {
                await store.WriteExecutionAuditAsync(id, "RDS_DISPATCH_DEFERRED", error.Code, error.Message, correlationId, cancellationToken);
                results.Add(new(id, id.ToString(), false, false, error.Code, "RDS read evidence is unavailable; the request remains queued."));
            }
            catch (RdsCoreNativeOrderException error)
            {
                await store.WriteExecutionAuditAsync(id, "RDS_DISPATCH_DEFERRED", error.Code, error.Message, correlationId, cancellationToken);
                results.Add(new(id, id.ToString(), false, false, error.Code, "RDSCore is temporarily unavailable; the request remains queued."));
            }
        }

        return results;
    }

    public async Task<RdsDispatchAttemptResult?> DispatchOneAsync(Guid requestId, Guid correlationId, CancellationToken cancellationToken = default)
    {
        // Defence in depth: this method is public and directly callable, so the
        // global mutation gate is re-checked here and not only in
        // DispatchQueuedAsync. No lease is taken and no RDS call is made when the
        // gate is closed.
        if (!IsDispatchEnabled)
        {
            await store.WriteExecutionAuditAsync(
                requestId,
                "RDS_DISPATCH_BLOCKED",
                "RDS_DISPATCH_DISABLED",
                "Physical RDS dispatch is globally disabled by configuration (Rds:DispatchEnabled=false).",
                correlationId,
                cancellationToken);
            return new(requestId, requestId.ToString(), false, false, "RDS_DISPATCH_DISABLED", "Physical RDS dispatch is globally disabled by configuration.");
        }

        await using var lease = await store.TryAcquireRequestLeaseAsync(requestId, cancellationToken);
        if (lease is null)
        {
            return null;
        }

        var context = await store.GetDispatchContextAsync(requestId, cancellationToken)
            ?? throw new RdsExecutionException("RDS_DISPATCH_CONTEXT_NOT_FOUND", "The queued transport request no longer has a dispatch context.");
        if (context.Request.Status != TransportRequestStatus.Queued)
        {
            return new(requestId, requestId.ToString(), false, false, "REQUEST_NOT_QUEUED", "The request is no longer queued.");
        }

        if (context.Request.ReadinessState == TransportReadinessState.WaitingPreparation)
        {
            return new(requestId, requestId.ToString(), false, false, "READINESS_WAITING_PREPARATION", "The request is waiting for warehouse preparation.");
        }

        // BRING_FULL takes material from a shared warehouse preparation area,
        // which no layout owns, so its source is never a layout slot. It is
        // resolved from the pallet standing in an area for this request, and
        // only once that pallet was confirmed ready. If no such pallet exists,
        // or the area carries no point for this navigation profile, the source
        // stays unresolved and dispatch is refused rather than guessing: a task
        // template configured with a source slot group must not be able to
        // quietly substitute another layout's slot and send the robot to
        // collect the wrong material.
        if (context.Request.TaskType == TransportTaskType.BringFull
            && string.IsNullOrWhiteSpace(context.Source.SiteId))
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", "BRING_FULL_SOURCE_UNRESOLVED", "No ready pallet in a mapped preparation area resolves this material feed; dispatch stays closed rather than guessing a source.", correlationId, cancellationToken);
            return new(requestId, requestId.ToString(), false, false, "BRING_FULL_SOURCE_UNRESOLVED", "No ready pallet in a mapped preparation area resolves this material feed.");
        }

        var executionModeStr = GetLocation(context.TemplateConfig, "executionMode");
        if (string.IsNullOrWhiteSpace(executionModeStr))
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", "EXECUTION_MODE_UNRESOLVED", "The task template configuration does not define an explicit executionMode.", correlationId, cancellationToken);
            return new(requestId, requestId.ToString(), false, false, "EXECUTION_MODE_UNRESOLVED", "The task template configuration does not define an explicit executionMode.");
        }

        var isNative = string.Equals(executionModeStr, "RDSCORE_NATIVE_ORDER", StringComparison.OrdinalIgnoreCase);
        var isWindTask = string.Equals(executionModeStr, "WIND_TASK", StringComparison.OrdinalIgnoreCase);

        if (!isNative && !isWindTask)
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", "EXECUTION_MODE_INVALID", $"Unsupported executionMode '{executionModeStr}'. Must be RDSCORE_NATIVE_ORDER or WIND_TASK.", correlationId, cancellationToken);
            return new(requestId, requestId.ToString(), false, false, "EXECUTION_MODE_INVALID", $"Unsupported executionMode '{executionModeStr}'. Must be RDSCORE_NATIVE_ORDER or WIND_TASK.");
        }

        if (isNative)
        {
            if (!IsNativeMode)
            {
                await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", "NATIVE_DISPATCH_DISABLED", "Template requires RDSCORE_NATIVE_ORDER but Native dispatch is not configured or disabled.", correlationId, cancellationToken);
                return new(requestId, requestId.ToString(), false, false, "NATIVE_DISPATCH_DISABLED", "Template requires RDSCORE_NATIVE_ORDER but Native dispatch is not configured or disabled.");
            }
            return await DispatchNativeOrderAsync(requestId, context, correlationId, cancellationToken);
        }

        var taskLabel = context.RdsTaskLabel?.Trim();
        if (string.IsNullOrWhiteSpace(taskLabel))
        {
            return new(requestId, requestId.ToString(), false, false, "RDS_TASK_TEMPLATE_NOT_READY", "The matching task template has no RDS task label.");
        }

        var inputParams = RdsTaskInputBinder.Bind(context);
        ValidateReferencedEndpoints(context, inputParams);
        var taskRecordId = requestId.ToString();
        var createRequest = new RdsCreateTransportRequest(taskRecordId, taskLabel, context.TemplatePriority, inputParams);
        var requestPayload = JsonSerializer.SerializeToElement(createRequest);
        await store.EnsureTaskRecordAsync(requestId, taskRecordId, taskLabel, requestPayload, cancellationToken);
        await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_ATTEMPTED", "RDS_CREATE_REQUEST", "Dispatch was attempted with the immutable TKG request identity.", correlationId, cancellationToken);

        RdsCreateTransportResult? createResult = null;
        try
        {
            createResult = await executionClient.CreateTransportAsync(createRequest, correlationId, cancellationToken);
            await store.RecordDispatchResponseAsync(requestId, createResult.RawResponse, timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (RdsExecutionException)
        {
            // A lost HTTP response is ambiguous. Query the same taskRecordId before deciding whether to retry.
        }

        var query = await readClient.QueryWindTasksAsync(new(1, 100, TaskRecordId: taskRecordId), correlationId, cancellationToken);
        var exact = query.Data.Tasks.Where(task => string.Equals(task.TaskRecordId, taskRecordId, StringComparison.Ordinal)).ToArray();
        if (exact.Length > 1 || query.Data.TotalCount > 1)
        {
            throw new RdsExecutionException("RDS_DUPLICATE_TASK_IDENTITY", "RDS returned more than one task for the immutable TKG taskRecordId.");
        }

        if (exact.Length == 0)
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_UNCONFIRMED", "RDS_TASK_NOT_YET_OBSERVED", "The create response was not treated as execution proof; the request remains queued.", correlationId, cancellationToken);
            return new(requestId, taskRecordId, false, createResult?.IdempotentReplay ?? false, "RDS_TASK_NOT_YET_OBSERVED", "Create was not treated as execution proof; reconciliation will retry the same identity.");
        }

        await store.RecordTaskStatusAsync(requestId, exact[0], query.ReadAt, cancellationToken);
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds, OperationalEventContext.System(correlationId), cancellationToken);
        await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_CONFIRMED", "RDS_TASK_OBSERVED", "RDS task existence was proven by taskRecordId.", correlationId, cancellationToken);
        return new(requestId, taskRecordId, true, createResult?.IdempotentReplay ?? true, "RDS_TASK_OBSERVED", "RDS task existence was proven by taskRecordId.");
    }

    public async Task ReconcileAsync(int limit, CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 500);
        var tasks = await store.ListActiveTasksAsync(boundedLimit, cancellationToken);
        foreach (var projection in tasks)
        {
            await using var lease = await store.TryAcquireRequestLeaseAsync(projection.TransportRequestId, cancellationToken);
            if (lease is null)
            {
                continue;
            }

            var correlationId = Guid.NewGuid();
            try
            {
                if (IsNativeMode)
                {
                    var details = await nativeOrderClient!.GetOrderDetailsAsync(projection.TaskRecordId, correlationId, cancellationToken);
                    var status = MapNativeStateToStatus(details.State.Known);
                    var vehicle = ExtractVehicle(details.Raw, projection.AgvId);
                    var task = new RdsWindTaskSnapshot(
                        projection.TaskRecordId,
                        projection.TaskLabel ?? "",
                        status,
                        vehicle,
                        projection.TaskRecordId,
                        details.Raw,
                        details.State.Raw);
                    await ApplyTaskStatusAsync(projection, task, timeProvider.GetUtcNow(), "NATIVE_RECONCILIATION", cancellationToken, correlationId);
                    if (IsTerminalRdsStatus(status))
                    {
                        await RefreshRelevantWorkSitesAsync(projection.TransportRequestId, correlationId, cancellationToken);
                    }
                }
                else
                {
                    var query = await readClient.QueryWindTasksAsync(new(1, 100, TaskRecordId: projection.TaskRecordId), correlationId, cancellationToken);
                    var exact = query.Data.Tasks.Where(task => string.Equals(task.TaskRecordId, projection.TaskRecordId, StringComparison.Ordinal)).ToArray();
                    if (exact.Length == 1)
                    {
                        await ApplyTaskStatusAsync(projection, exact[0], query.ReadAt, "RECONCILIATION", cancellationToken, correlationId);
                        if (IsTerminalRdsStatus(exact[0].Status))
                        {
                            await RefreshRelevantWorkSitesAsync(projection.TransportRequestId, correlationId, cancellationToken);
                        }
                    }
                    else if (exact.Length > 1 || query.Data.TotalCount > 1)
                    {
                        throw new RdsExecutionException("RDS_DUPLICATE_TASK_IDENTITY", "RDS returned duplicate task instances for one taskRecordId.");
                    }
                }
            }
            catch (RdsExecutionException error)
            {
                await store.WriteExecutionAuditAsync(projection.TransportRequestId, "RDS_RECONCILIATION_BLOCKED", error.Code, error.Message, correlationId, cancellationToken);
            }
            catch (RdsReadException error)
            {
                await store.WriteExecutionAuditAsync(projection.TransportRequestId, "RDS_RECONCILIATION_DEFERRED", error.Code, error.Message, correlationId, cancellationToken);
            }
            catch (RdsCoreNativeOrderException error)
            {
                await store.WriteExecutionAuditAsync(projection.TransportRequestId, "RDS_RECONCILIATION_BLOCKED", error.Code, error.Message, correlationId, cancellationToken);
            }
        }

        var pendingRefreshes = await store.ListPendingWorkSiteRefreshRequestIdsAsync(boundedLimit, cancellationToken);
        foreach (var requestId in pendingRefreshes)
        {
            await using var lease = await store.TryAcquireRequestLeaseAsync(requestId, cancellationToken);
            if (lease is null)
            {
                continue;
            }

            await RefreshRelevantWorkSitesAsync(requestId, Guid.NewGuid(), cancellationToken);
        }

        if (robotTelemetry is not null && tasks.Count > 0)
        {
            try
            {
                var remainingTimes = await robotTelemetry.GetRobotRemainingTimesAsync(cancellationToken);
                if (remainingTimes.Count > 0)
                {
                    await store.EnrichRemainingTimesAsync(remainingTimes, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort enrichment; ETA falls back to elapsed-time estimate.
                // Cancellation propagates: a shutdown must not be reported as a
                // completed reconciliation cycle. RDS clients already translate
                // request timeouts into RdsReadException/RdsCoreNativeOrderException,
                // so an OperationCanceledException here is always caller cancellation.
            }
        }
    }

    // ---------------------------------------------------------------------
    // Expert recovery actions.
    //
    // These are high-risk maintenance operations, so the server resolves the
    // exact target itself and never trusts a caller-supplied vehicle or order
    // identifier. Each action binds to a real TKG TransportRequest, refuses
    // terminal requests, targets exactly the robot RDS assigned to that request,
    // and writes an execution audit record.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Resolves the authoritative RDS task and assigned vehicle for a request, or
    /// throws with a precise reason. Never returns a caller-influenced target.
    /// </summary>
    private async Task<(TransportRequestSnapshot Request, RdsTaskProjection Task)> ResolveExpertTargetAsync(
        Guid requestId,
        bool requireAssignedVehicle,
        CancellationToken cancellationToken)
    {
        var request = await store.FindRequestAsync(requestId, cancellationToken)
            ?? throw new RdsExecutionException("REQUEST_NOT_FOUND", "The transport request does not exist.");

        if (request.Status is TransportRequestStatus.Completed
            or TransportRequestStatus.Cancelled
            or TransportRequestStatus.Failed)
        {
            throw new RdsExecutionException(
                "REQUEST_ALREADY_TERMINAL",
                $"The transport request is already {request.Status.ToDatabase()}; expert recovery does not apply.");
        }

        var task = await store.FindTaskByRequestAsync(requestId, cancellationToken)
            ?? throw new RdsExecutionException(
                "RDS_TASK_NOT_FOUND",
                "The request has no dispatched RDS task, so there is nothing to recover.");

        if (requireAssignedVehicle && string.IsNullOrWhiteSpace(task.AgvId))
        {
            throw new RdsExecutionException(
                "RDS_VEHICLE_UNASSIGNED",
                "RDS has not assigned a robot to this request yet, so no exact vehicle can be targeted.");
        }

        return (request, task);
    }

    /// <summary>
    /// Live corroboration for a vehicle-scoped expert mutation.
    ///
    /// <c>/manualFinished</c> and <c>/redoFailedOrder</c> act on whatever the named
    /// robot is doing RIGHT NOW. <see cref="ResolveExpertTargetAsync"/> proves only
    /// historical facts — a TransportRequest, a persisted integration.rds_tasks row
    /// and the agv_id recorded at dispatch time — and a historical mapping is not
    /// authority for a physical mutation. If the robot has since moved on, acting on
    /// the recorded agv_id advances or re-runs an unrelated order.
    ///
    /// So the vehicle is re-derived from CURRENT telemetry: the order identity comes
    /// from the persisted task (never the caller), the order must currently be in one
    /// of <paramref name="allowedStates"/>, and exactly one robot must currently be
    /// executing that order. Matching uses <see cref="RdsRobotIdentity"/>, so a
    /// recorded uuid still corroborates telemetry reporting a vehicle_id. Missing,
    /// ambiguous or contradictory evidence fails closed.
    /// </summary>
    private async Task<(RdsTaskProjection Task, string Vehicle)> ResolveCurrentExecutionTargetAsync(
        Guid requestId,
        Func<RdsCoreKnownOrderState, RdsCoreRobotTelemetry, bool> satisfiesPrecondition,
        string preconditionDescription,
        string operation,
        string heldLeaseKey,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var (_, task) = await ResolveExpertTargetAsync(requestId, requireAssignedVehicle: true, cancellationToken);

        RdsCoreOrderDetails details;
        try
        {
            details = await nativeOrderClient!.GetOrderDetailsAsync(task.TaskRecordId, correlationId, cancellationToken);
        }
        catch (RdsCoreNativeOrderException error)
        {
            throw new RdsExecutionException(
                "EXPERT_CURRENT_STATE_UNAVAILABLE",
                $"Current RDS state for order '{task.TaskRecordId}' could not be read, so {operation} is refused.",
                error);
        }

        RdsCoreFleetStatus fleet;
        try
        {
            fleet = await nativeOrderClient.GetRobotsStatusAsync(correlationId, cancellationToken);
        }
        catch (RdsCoreNativeOrderException error)
        {
            throw new RdsExecutionException(
                "EXPERT_CURRENT_STATE_UNAVAILABLE",
                $"Current robot telemetry could not be read, so {operation} is refused.",
                error);
        }

        // The robot that is executing THIS order right now — not the one that was
        // assigned when the order was dispatched.
        var robots = fleet.Robots;
        var executing = robots
            .Where(robot => !string.IsNullOrWhiteSpace(robot.CurrentOrderId)
                            && string.Equals(robot.CurrentOrderId, task.TaskRecordId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (executing.Length != 1)
        {
            throw new RdsExecutionException(
                "EXPERT_ORDER_NOT_CURRENTLY_EXECUTING",
                executing.Length == 0
                    ? $"No robot is currently executing order '{task.TaskRecordId}', so {operation} is refused."
                    : $"Order '{task.TaskRecordId}' is reported by more than one robot, so {operation} is refused.");
        }

        var robotNow = executing[0];

        // The recorded assignment must still agree with live telemetry, compared
        // across identity spaces rather than by raw string equality.
        if (!robotNow.ResolvedIdentity.Matches(task.AgvId))
        {
            throw new RdsExecutionException(
                "EXPERT_ROBOT_MISMATCH",
                $"Order '{task.TaskRecordId}' is currently executing on a different robot than the one recorded for this request, so {operation} is refused.");
        }

        // Before anything else about this robot is trusted, prove the lease we hold is
        // the lease for THIS robot.
        EnsureLeaseCoversTarget(heldLeaseKey, robotNow, operation);

        var vehicle = robotNow.ResolvedIdentity.MutationTarget;
        if (string.IsNullOrWhiteSpace(vehicle))
        {
            throw new RdsExecutionException(
                "EXPERT_ROBOT_MISMATCH",
                $"The robot executing order '{task.TaskRecordId}' reports no usable vehicle identity, so {operation} is refused.");
        }

        // The documented SEER precondition, evaluated last because it may depend on
        // BOTH the current order state and the current robot state.
        if (!satisfiesPrecondition(details.State.Known, robotNow))
        {
            throw new RdsExecutionException(
                "EXPERT_ORDER_STATE_INCOMPATIBLE",
                $"Order '{task.TaskRecordId}' is {details.State.Known.ToString().ToUpperInvariant()} and robot '{vehicle}' reports no error condition. {operation} requires {preconditionDescription}.");
        }

        return (task, vehicle);
    }

    /// <summary>
    /// SEER's documented manualFinished precondition, verbatim from
    /// SEER_RDSCore_HTTP_API_Turkce_Ceviri_Tam.docx section 4.3 (/manualFinished):
    /// "Yalnız mevcut order hata durumundayken veya AGV hata raporluyorken çalışır."
    /// — it works ONLY while the current order is in an error condition, or while the
    /// AGV is reporting an error. The section is titled "Mevcut Başarısız Block'u
    /// Manuel Tamamlama" (manually completing the current FAILED block).
    ///
    /// RUNNING, WAITING and SUSPENDED are therefore NOT sufficient on their own. An
    /// earlier round accepted them, which asked RDSCore to advance a block that was
    /// executing normally.
    ///
    /// "Order in an error condition" maps to the two documented error-ish order
    /// states: FAILED ("aktif/uygulama kaynaklı failure") and Error ("parametre hatası
    /// nedeniyle yürütülemez"). "AGV reporting an error" maps to is_error or a
    /// blocking fatal/error alarm.
    /// </summary>
    private static bool SatisfiesManualFinishPrecondition(RdsCoreKnownOrderState state, RdsCoreRobotTelemetry robot) =>
        state is RdsCoreKnownOrderState.Failed or RdsCoreKnownOrderState.Error
        || robot.IsError
        || robot.HasBlockingAlarm;

    /// <summary>
    /// SEER's documented redoFailedOrder precondition, verbatim from
    /// SEER_RDSCore_HTTP_API_Turkce_Ceviri_Tam.docx section 3.18 (/redoFailedOrder):
    /// "Yalnız order durumu FAILED iken etkilidir; diğer durumlarda komut yok sayılır."
    /// — effective ONLY when the order status is FAILED; in any other state the command
    /// is ignored.
    ///
    /// Error is deliberately NOT accepted. The documented order-state list makes them
    /// distinct: FAILED is "aktif/uygulama kaynaklı failure" while Error is "parametre
    /// hatası nedeniyle yürütülemez" — an order that cannot run at all. An earlier
    /// round treated them as equivalent without evidence.
    /// </summary>
    private static bool SatisfiesRedoPrecondition(RdsCoreKnownOrderState state, RdsCoreRobotTelemetry robot) =>
        state == RdsCoreKnownOrderState.Failed;

    private void EnsureNativeExpertActionAvailable()
    {
        if (nativeOrderClient is null || !IsNativeMode)
        {
            throw new RdsExecutionException(
                "NATIVE_EXECUTION_UNAVAILABLE",
                "RDSCore native execution is not configured, so expert recovery is unavailable.");
        }
    }

    /// <summary>Reads current telemetry for fleet maintenance, failing closed.</summary>
    private async Task<RdsCoreFleetStatus> ReadFleetForMaintenanceAsync(
        string operation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await nativeOrderClient!.GetRobotsStatusAsync(correlationId, cancellationToken);
        }
        catch (RdsCoreNativeOrderException error)
        {
            throw new RdsExecutionException(
                "EXPERT_CURRENT_STATE_UNAVAILABLE",
                $"Current robot telemetry could not be read, so {operation} is refused.",
                error);
        }
    }

    /// <summary>
    /// Fail-closed alias resolution for the map-prep re-reads. An ambiguous or absent
    /// alias yields null, which every caller already treats as "cannot proceed".
    /// </summary>
    private static RdsCoreRobotTelemetry? ResolveUniqueRobot(IReadOnlyList<RdsCoreRobotTelemetry> robots, string? alias)
    {
        var match = RdsRobotResolver.Resolve(robots, alias);
        return match.Kind == RdsRobotMatchKind.Unique ? match.Robot : null;
    }

    /// <summary>
    /// Resolves the canonical <see cref="RdsRobotIdentity.RobotLeaseKey"/> for a robot
    /// that a caller knows by ONE alias. This read exists purely to collapse the two
    /// identity spaces onto one lease key before the lease is taken: a caller holding a
    /// uuid and a caller holding a vehicle_id must not end up on two different locks
    /// for the same physical robot. It deliberately proves nothing about safety —
    /// every safety-relevant fact is re-read INSIDE the lease.
    ///
    /// Returns null when telemetry cannot identify the robot, which callers treat as
    /// fail-closed rather than as "no lock needed".
    /// </summary>
    private async Task<string?> ResolveRobotLeaseKeyAsync(
        string knownAlias,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(knownAlias)) return null;

        RdsCoreFleetStatus fleet;
        try
        {
            fleet = await nativeOrderClient!.GetRobotsStatusAsync(correlationId, cancellationToken);
        }
        catch (RdsCoreNativeOrderException)
        {
            return null;
        }

        // Ambiguous aliases must not silently pick a machine. Returning null makes the
        // caller refuse the mutation rather than lease the wrong robot.
        var match = RdsRobotResolver.Resolve(fleet.Robots, knownAlias);
        return match.Kind == RdsRobotMatchKind.Unique ? match.Robot!.ResolvedIdentity.RobotLeaseKey : null;
    }

    /// <summary>
    /// Runs <paramref name="leasedWork"/> while holding the robot-scoped distributed
    /// lease, so that VALIDATE-then-MUTATE is atomic with respect to every other TKG
    /// operation that could retarget the same physical robot. The per-request lease is
    /// not sufficient: two different TransportRequests can each hold their own request
    /// lease and still resolve to the same robot.
    ///
    /// The lease is a PostgreSQL session advisory lock, so it serialises across TKG
    /// processes, not merely across tasks in one process. It is released in the
    /// implicit finally of the await-using on every path — success, adapter exception
    /// and cancellation alike.
    /// </summary>
    private async Task<T> WithRobotLeaseAsync<T>(
        string knownAlias,
        string operation,
        Guid correlationId,
        Func<string, CancellationToken, Task<T>> leasedWork,
        CancellationToken cancellationToken)
    {
        var leaseKey = await ResolveRobotLeaseKeyAsync(knownAlias, correlationId, cancellationToken);
        if (leaseKey is null)
        {
            throw new RdsExecutionException(
                "EXPERT_CURRENT_STATE_UNAVAILABLE",
                $"The robot for this request could not be identified from current telemetry, so {operation} is refused.");
        }

        await using var lease = await store.TryAcquireRobotLeaseAsync(leaseKey, cancellationToken);
        if (lease is null)
        {
            throw new RdsExecutionException(
                "ROBOT_BUSY",
                $"Another TKG operation is currently acting on robot '{leaseKey}', so {operation} is refused. Retry once it completes.");
        }

        // The held key is handed to the leased work so it can prove the robot it
        // finally mutates is the robot this lease actually covers.
        return await leasedWork(leaseKey, cancellationToken);
    }

    /// <summary>
    /// Held-lease / final-target invariant. The lease is taken from a candidate alias
    /// BEFORE the authoritative in-lease re-read, so the re-read can legitimately land
    /// on a different physical robot — RDSCore may have reassigned the order in
    /// between. Mutating that robot while holding a lease on another one would defeat
    /// the whole point of the lease, so this fails closed and the caller retries from
    /// fresh state. Swapping leases mid-attempt is deliberately not done: it would
    /// reintroduce the very window the lease exists to close.
    /// </summary>
    private static void EnsureLeaseCoversTarget(string heldLeaseKey, RdsCoreRobotTelemetry target, string operation)
    {
        var targetKey = target.ResolvedIdentity.RobotLeaseKey;
        if (!string.Equals(heldLeaseKey, targetKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new RdsExecutionException(
                "EXPERT_ROBOT_CHANGED_DURING_VALIDATION",
                $"The robot for this request changed from '{heldLeaseKey}' to '{targetKey}' after the lease was taken, so {operation} is refused. Retry from current state.");
        }
    }

    /// <summary>
    /// Single enforcement point for the global physical mutation gate outside the
    /// dispatch path. Being Admin/Supervisor-only never substitutes for this check.
    /// </summary>
    private void EnsureMutationAllowed(RdsMutationClass mutationClass, string operation)
    {
        if (!RdsMutationPolicy.IsAllowed(mutationClass, IsGlobalDispatchAllowed))
        {
            throw new RdsExecutionException(
                RdsMutationPolicy.BlockedCode,
                RdsMutationPolicy.BlockedMessage(operation));
        }
    }

    /// <summary>
    /// Fleet-scoped robot maintenance routed through the Application layer so it
    /// shares the one mutation policy instead of calling the adapter directly.
    /// An explicit, non-empty vehicle list is required; there are no broadcast
    /// semantics. Machine scoping for Supervisor is deliberately not invented here.
    /// </summary>
    public async Task<RdsCoreSimpleResult> RobotMaintenanceAsync(
        RdsRobotMaintenanceAction action,
        IReadOnlyList<string> vehicles,
        string? dispatchableType,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        if (vehicles is not { Count: > 0 } || vehicles.Any(string.IsNullOrWhiteSpace))
        {
            throw new RdsExecutionException("VEHICLES_REQUIRED", "At least one explicit vehicle identifier is required.");
        }

        var (mutationClass, operation) = action switch
        {
            RdsRobotMaintenanceAction.Pause => (RdsMutationClass.SafetyStop, "/gotoSitePause"),
            RdsRobotMaintenanceAction.Resume => (RdsMutationClass.MotionEnabling, "/gotoSiteResume"),
            RdsRobotMaintenanceAction.ClearErrors => (RdsMutationClass.MotionEnabling, "/clearRobotAllError"),
            RdsRobotMaintenanceAction.SetDispatchable => (RdsMutationPolicy.ClassifyDispatchable(dispatchableType), "/dispatchable"),
            _ => throw new RdsExecutionException("UNSUPPORTED_MAINTENANCE_ACTION", "Unsupported robot maintenance action.")
        };

        EnsureMutationAllowed(mutationClass, operation);
        EnsureNativeExpertActionAvailable();

        async Task<RdsCoreSimpleResult> InvokeAsync(IReadOnlyList<string> targets, CancellationToken ct) => action switch
        {
            RdsRobotMaintenanceAction.Pause => await nativeOrderClient!.GotoSitePauseAsync(targets, correlationId, ct),
            RdsRobotMaintenanceAction.Resume => await nativeOrderClient!.GotoSiteResumeAsync(targets, correlationId, ct),
            RdsRobotMaintenanceAction.ClearErrors => await nativeOrderClient!.ClearRobotAllErrorAsync(targets, correlationId, ct),
            _ => await nativeOrderClient!.SetDispatchableAsync(targets, dispatchableType ?? string.Empty, correlationId, ct)
        };

        // Safety-stop actions are deliberately NOT leased. Pause and making a robot
        // undispatchable reduce motion, and queueing them behind a motion-enabling
        // lease would delay a stop — the wrong direction for safety. Only the
        // motion-enabling and execution classes serialise against the same robot.
        if (mutationClass == RdsMutationClass.SafetyStop)
        {
            // A stop is never delayed or refused for identity bookkeeping. It is sent
            // with the caller's aliases exactly as before.
            return await InvokeAsync(vehicles, cancellationToken);
        }

        // Motion-enabling fleet maintenance resolves every caller alias to a PHYSICAL
        // robot first, then leases and calls RDS with the server-resolved identity.
        // Previously the canonical identity was computed only to derive lease keys and
        // the ORIGINAL caller aliases were still sent to RDS, so a request could hold
        // the right lease while naming the robot in a way the server had never
        // validated. Two aliases for one machine now collapse to one target, and an
        // ambiguous alias is refused before any mutation.
        var fleet = await ReadFleetForMaintenanceAsync(operation, correlationId, cancellationToken);
        var resolved = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in vehicles.Select(v => v.Trim()).Where(v => v.Length > 0))
        {
            var match = RdsRobotResolver.Resolve(fleet.Robots, alias);
            if (match.Kind == RdsRobotMatchKind.Ambiguous)
            {
                throw new RdsExecutionException(
                    "ROBOT_ALIAS_AMBIGUOUS",
                    $"'{alias}' matches more than one physical robot, so {operation} is refused.");
            }

            if (match.Kind == RdsRobotMatchKind.NoMatch)
            {
                throw new RdsExecutionException(
                    "EXPERT_CURRENT_STATE_UNAVAILABLE",
                    $"Robot '{alias}' could not be identified in current telemetry, so {operation} is refused.");
            }

            var identity = match.Robot!.ResolvedIdentity;
            var leaseKey = identity.RobotLeaseKey;
            var target = identity.MutationTarget;
            if (string.IsNullOrWhiteSpace(leaseKey) || string.IsNullOrWhiteSpace(target))
            {
                throw new RdsExecutionException(
                    "EXPERT_CURRENT_STATE_UNAVAILABLE",
                    $"Robot '{alias}' reports no usable identity, so {operation} is refused.");
            }

            // Keyed by lease key, so two aliases naming one machine yield one target.
            resolved[leaseKey] = target;
        }

        var leases = new List<IRdsExecutionLease>();
        try
        {
            // Sorted order: two concurrent multi-robot calls cannot deadlock by taking
            // the same pair in opposite orders.
            foreach (var key in resolved.Keys)
            {
                var lease = await store.TryAcquireRobotLeaseAsync(key, cancellationToken)
                    ?? throw new RdsExecutionException(
                        "ROBOT_BUSY",
                        $"Another TKG operation is currently acting on robot '{key}', so {operation} is refused.");
                leases.Add(lease);
            }

            return await InvokeAsync([.. resolved.Values], cancellationToken);
        }
        finally
        {
            for (var i = leases.Count - 1; i >= 0; i--)
            {
                await leases[i].DisposeAsync();
            }
        }
    }

    public async Task<RdsExpertActionResult> ManualFinishAsync(
        Guid requestId,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        // Marking the current block finished advances the robot to the next block,
        // so this starts physical work and is gated.
        EnsureMutationAllowed(RdsMutationClass.MotionEnabling, "/manualFinished");
        EnsureNativeExpertActionAvailable();

        // The recorded agv_id is only a CANDIDATE used to find the lease. All
        // validation happens inside the lease, against freshly read state.
        var (_, candidate) = await ResolveExpertTargetAsync(requestId, requireAssignedVehicle: true, cancellationToken);

        return await WithRobotLeaseAsync(candidate.AgvId!, "manual finish", correlationId, async (heldLeaseKey, ct) =>
        {
            // SEER permits this only in an error condition; see
            // SatisfiesManualFinishPrecondition for the documented wording.
            var (task, vehicle) = await ResolveCurrentExecutionTargetAsync(
                requestId,
                SatisfiesManualFinishPrecondition,
                "the current order to be in an error state (FAILED/Error) or the robot to be reporting an error",
                "manual finish",
                heldLeaseKey,
                correlationId,
                ct);

            var result = await nativeOrderClient!.ManualFinishedAsync([vehicle], correlationId, ct);
            await store.WriteExecutionAuditAsync(
                requestId,
                "RDS_EXPERT_MANUAL_FINISH",
                result.CoreAccepted ? "ACCEPTED" : "REJECTED",
                $"Manual finish targeted vehicle '{vehicle}', corroborated as currently executing order '{task.TaskRecordId}'.",
                correlationId,
                ct);

            return new RdsExpertActionResult(requestId, task.TaskRecordId, vehicle, result.Code ?? 0, result.Message ?? string.Empty, result.CoreAccepted);
        }, cancellationToken);
    }

    public async Task<RdsExpertActionResult> RedoFailedAsync(
        Guid requestId,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        // Re-running a failed block re-dispatches physical work.
        EnsureMutationAllowed(RdsMutationClass.MotionEnabling, "/redoFailedOrder");
        EnsureNativeExpertActionAvailable();

        var (_, candidate) = await ResolveExpertTargetAsync(requestId, requireAssignedVehicle: true, cancellationToken);

        return await WithRobotLeaseAsync(candidate.AgvId!, "redo failed order", correlationId, async (heldLeaseKey, ct) =>
        {
            // SEER permits this only for a FAILED order; see SatisfiesRedoPrecondition.
            var (task, vehicle) = await ResolveCurrentExecutionTargetAsync(
                requestId,
                SatisfiesRedoPrecondition,
                "the order to be FAILED",
                "redo failed order",
                heldLeaseKey,
                correlationId,
                ct);

            var result = await nativeOrderClient!.RedoFailedOrderAsync([vehicle], correlationId, ct);
            await store.WriteExecutionAuditAsync(
                requestId,
                "RDS_EXPERT_REDO_FAILED",
                result.CoreAccepted ? "ACCEPTED" : "REJECTED",
                $"Redo failed order targeted vehicle '{vehicle}', corroborated as currently executing failed order '{task.TaskRecordId}'.",
                correlationId,
                ct);

            return new RdsExpertActionResult(requestId, task.TaskRecordId, vehicle, result.Code ?? 0, result.Message ?? string.Empty, result.CoreAccepted);
        }, cancellationToken);
    }

    /// <summary>
    /// Reprioritises the RDS order that belongs to this request. The order identity
    /// comes from the persisted RDS task, never from the caller. SEER's numeric
    /// priority direction is not documented in this repository, so the value is
    /// range-checked and passed through without inventing overtaking semantics.
    /// </summary>
    public async Task<RdsExpertActionResult> SetRequestPriorityAsync(
        Guid requestId,
        int priority,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        // Input validation first: a malformed value is rejected regardless of which
        // execution engine happens to be configured.
        if (priority is < MinRdsPriority or > MaxRdsPriority)
        {
            throw new RdsExecutionException(
                "PRIORITY_OUT_OF_RANGE",
                $"Priority must be between {MinRdsPriority} and {MaxRdsPriority}.");
        }

        // Reordering the queue can cause work to be dispatched sooner.
        EnsureMutationAllowed(RdsMutationClass.ExecutionMutation, "/setPriority");
        EnsureNativeExpertActionAvailable();
        var (_, task) = await ResolveExpertTargetAsync(requestId, requireAssignedVehicle: false, cancellationToken);

        var result = await nativeOrderClient!.SetPriorityAsync(task.TaskRecordId, priority, correlationId, cancellationToken);
        await store.WriteExecutionAuditAsync(
            requestId,
            "RDS_EXPERT_SET_PRIORITY",
            result.CoreAccepted ? "ACCEPTED" : "REJECTED",
            $"Priority {priority} applied to RDS order '{task.TaskRecordId}'.",
            correlationId,
            cancellationToken);

        return new(requestId, task.TaskRecordId, task.AgvId ?? string.Empty, result.Code ?? 0, result.Message ?? string.Empty, result.CoreAccepted);
    }

    private const int MinRdsPriority = 0;
    private const int MaxRdsPriority = 1000;

    public Task<RdsCancellationResult> CancelAsync(Guid requestId, Guid? actorUserId, Guid correlationId, CancellationToken cancellationToken = default) =>
        CancelAsync(requestId, actorUserId, correlationId, null, cancellationToken);

    public async Task<RdsCancellationResult> CancelAsync(
        Guid requestId,
        Guid? actorUserId,
        Guid correlationId,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default)
    {
        var request = await store.FindRequestAsync(requestId, cancellationToken)
            ?? throw new WorkflowRuleException("REQUEST_NOT_FOUND", "The transport request does not exist.");
        if (TransportStateMachine.CanCancelBeforeExecution(request.Status) || request.Status == TransportRequestStatus.Cancelled)
        {
            var local = await workflow.CancelAsync(requestId, actorUserId, eventContext, cancellationToken);
            return new(local.Request, false, local.WasAlreadyCancelled);
        }

        if (request.Status is not (TransportRequestStatus.SentToRds or TransportRequestStatus.Running))
        {
            throw new WorkflowRuleException("CANCELLATION_NOT_ALLOWED", "The transport request cannot be cancelled from its current state.");
        }

        await using var lease = await store.TryAcquireRequestLeaseAsync(requestId, cancellationToken);
        if (lease is null)
        {
            return new(request, true, true);
        }

        var task = await store.FindTaskByRequestAsync(requestId, cancellationToken)
            ?? throw new RdsExecutionException("RDS_TASK_NOT_FOUND", "The dispatched request has no persisted RDS task identity.");

        if (IsNativeMode)
        {
            var nativeStopResult = await nativeOrderClient!.TerminateAsync(task.TaskRecordId, correlationId, cancellationToken);
            await store.MarkCancellationRequestedAsync(
                requestId,
                nativeStopResult.RawResponse ?? JsonSerializer.SerializeToElement(new { code = nativeStopResult.Code, message = nativeStopResult.Message }),
                timeProvider.GetUtcNow(),
                cancellationToken);

            var details = await nativeOrderClient.GetOrderDetailsAsync(task.TaskRecordId, correlationId, cancellationToken);
            if (details.State.Known != RdsCoreKnownOrderState.Stopped)
            {
                for (var attempt = 0; attempt < 5 && details.State.Known != RdsCoreKnownOrderState.Stopped; attempt++)
                {
                    await Task.Delay(150, cancellationToken);
                    details = await nativeOrderClient.GetOrderDetailsAsync(task.TaskRecordId, correlationId, cancellationToken);
                }
            }

            if (details.State.Known == RdsCoreKnownOrderState.Stopped)
            {
                var vehicle = ExtractVehicle(details.Raw, task.AgvId);
                var taskSnapshot = new RdsWindTaskSnapshot(
                    task.TaskRecordId,
                    task.TaskLabel ?? "",
                    1001,
                    vehicle,
                    task.TaskRecordId,
                    details.Raw,
                    "STOPPED");
                await store.RecordTaskStatusAsync(requestId, taskSnapshot, timeProvider.GetUtcNow(), cancellationToken);
                var cancelled = await workflow.RecordExecutionOutcomeAsync(
                    requestId,
                    new(TransportRequestStatus.Cancelled, EvidenceType: "RDSCORE_TERMINATE"),
                    eventContext ?? OperationalEventContext.System(correlationId),
                    cancellationToken);
                return new(cancelled, false, false);
            }

            return new(request, true, !nativeStopResult.CoreAccepted);
        }

        RdsStopTaskResult? stopResult = null;
        try
        {
            stopResult = await executionClient.StopTaskAsync(task.TaskRecordId, correlationId, cancellationToken);
        }
        catch (RdsExecutionException)
        {
            // The stop outcome is ambiguous. Persist intent and let reconciliation prove the physical result.
        }

        await store.MarkCancellationRequestedAsync(
            requestId,
            stopResult?.RawResponse ?? JsonSerializer.SerializeToElement(new { code = "AMBIGUOUS_STOP_RESPONSE" }),
            timeProvider.GetUtcNow(),
            cancellationToken);

        var query = await readClient.QueryWindTasksAsync(new(1, 100, TaskRecordId: task.TaskRecordId), correlationId, cancellationToken);
        var exact = query.Data.Tasks.SingleOrDefault(candidate => string.Equals(candidate.TaskRecordId, task.TaskRecordId, StringComparison.Ordinal));
        if (exact?.Status == 1001)
        {
            await store.RecordTaskStatusAsync(requestId, exact, query.ReadAt, cancellationToken);
            var cancelled = await workflow.RecordExecutionOutcomeAsync(
                requestId,
                new(TransportRequestStatus.Cancelled, EvidenceType: "RDS_QUERY_AFTER_STOP"),
                eventContext ?? OperationalEventContext.System(correlationId),
                cancellationToken);
            return new(cancelled, false, false);
        }

        return new(request, true, stopResult is null);
    }

    private async Task<RdsDispatchAttemptResult> DispatchNativeOrderAsync(
        Guid requestId,
        RdsDispatchContext context,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var taskLabel = string.IsNullOrWhiteSpace(context.RdsTaskLabel) ? "TKG_TRANSPORT" : context.RdsTaskLabel.Trim();
        var taskRecordId = requestId.ToString("D");

        var navProfile = await store.GetNavigationProfileSummaryAsync(context.Request.NavigationProfileId, context.Request.VariantId, context.Request.MachineId, cancellationToken);
        if (navProfile is null)
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", "NAVIGATION_PROFILE_UNRESOLVED", "No published NavigationProfile could be resolved for the effective variant.", correlationId, cancellationToken);
            return new(requestId, taskRecordId, false, false, "NAVIGATION_PROFILE_UNRESOLVED", "No published NavigationProfile could be resolved for the effective variant.");
        }

        var requiredMap = navProfile.RobotMapName?.Trim();
        if (string.IsNullOrWhiteSpace(requiredMap))
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", "REQUIRED_MAP_UNRESOLVED", "The resolved NavigationProfile has no configured robot map name.", correlationId, cancellationToken);
            return new(requestId, taskRecordId, false, false, "REQUIRED_MAP_UNRESOLVED", "The resolved NavigationProfile has no configured robot map name.");
        }

        var vehicle = GetLocation(context.TemplateConfig, "vehicle") ?? navProfile.Vehicle;
        if (string.IsNullOrWhiteSpace(vehicle))
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", "VEHICLE_UNRESOLVED", "No vehicle could be resolved from the template or navigation profile.", correlationId, cancellationToken);
            return new(requestId, taskRecordId, false, false, "VEHICLE_UNRESOLVED", "No vehicle could be resolved from the template or navigation profile.");
        }

        // Everything from here on — telemetry validation, map-prep lock/switchMap and
        // the SetOrder itself — is motion-enabling work against ONE physical robot, so
        // it takes the robot-scoped distributed lease. The per-request lease already
        // held by DispatchOneAsync is not enough: two different TransportRequests can
        // each hold their own request lease and still resolve to the same robot, which
        // is precisely how an expert mutation could retarget a robot mid-dispatch.
        // The lease is released when this method returns, on every path.
        var dispatchLeaseKey = await ResolveRobotLeaseKeyAsync(vehicle, correlationId, cancellationToken);
        if (dispatchLeaseKey is null)
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_DEFERRED", "ROBOT_IDENTITY_UNRESOLVED", $"Robot '{vehicle}' could not be identified in current telemetry, so dispatch is deferred.", correlationId, cancellationToken);
            return new(requestId, taskRecordId, false, false, "ROBOT_IDENTITY_UNRESOLVED", $"Robot '{vehicle}' could not be identified in current telemetry.");
        }

        await using var dispatchRobotLease = await store.TryAcquireRobotLeaseAsync(dispatchLeaseKey, cancellationToken);
        if (dispatchRobotLease is null)
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_DEFERRED", "ROBOT_BUSY", $"Another TKG operation currently holds the lease on robot '{dispatchLeaseKey}'.", correlationId, cancellationToken);
            return new(requestId, taskRecordId, false, false, "ROBOT_BUSY", "The robot is currently held by another TKG operation; the request remains queued.");
        }

        RdsCoreFleetStatus fleet;
        IReadOnlyList<RdsCoreRobotTelemetry> robots;
        try
        {
            fleet = await nativeOrderClient!.GetRobotsStatusAsync(correlationId, cancellationToken);
            robots = fleet.Robots;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cancellation is never a robot fault: a shutdown must not be recorded
            // as ROBOT_OFFLINE against a healthy robot.
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", "ROBOT_OFFLINE", $"Failed to query robot telemetry: {ex.Message}", correlationId, cancellationToken);
            return new(requestId, taskRecordId, false, false, "ROBOT_OFFLINE", "RDSCore robot status query failed.");
        }

        // A core-level fatal/error means RDSCore's own state is unsafe to dispatch
        // into — "have no charge point in map" and friends are scene problems, not
        // one robot's problem. These alarms were previously parsed but never handed
        // to any caller, so this gate could not fire at all.
        if (fleet.GlobalHasBlockingAlarm)
        {
            var codes = string.Join(",", fleet.GlobalAlarms.Where(a => a.IsBlocking).Select(a => a.Code));
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", "RDSCORE_HAS_BLOCKING_ALARM", $"RDSCore reports core-level blocking alarms: {codes}.", correlationId, cancellationToken);
            return new(requestId, taskRecordId, false, false, "RDSCORE_HAS_BLOCKING_ALARM", "RDSCore reports a core-level blocking alarm.");
        }

        var robot = ResolveUniqueRobot(robots, vehicle);
        // Same invariant as the expert paths: the lease was taken from the configured
        // vehicle alias before this authoritative read, so confirm the robot we are
        // about to dispatch to is the one the held lease covers.
        if (robot is not null && !string.Equals(dispatchLeaseKey, robot.ResolvedIdentity.RobotLeaseKey, StringComparison.OrdinalIgnoreCase))
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_DEFERRED", "ROBOT_CHANGED_DURING_VALIDATION", $"Robot for '{vehicle}' changed from '{dispatchLeaseKey}' after the lease was taken.", correlationId, cancellationToken);
            return new(requestId, taskRecordId, false, false, "ROBOT_CHANGED_DURING_VALIDATION", "The robot changed after the lease was taken; the request remains queued.");
        }

        if (robot is null || robot.ConnectionStatus != 1)
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", "ROBOT_OFFLINE", $"Robot '{vehicle}' is offline or not found.", correlationId, cancellationToken);
            return new(requestId, taskRecordId, false, false, "ROBOT_OFFLINE", $"Robot '{vehicle}' is offline or not found.");
        }

        var lockSnapshot = await store.GetUnreleasedMapSwitchLockAsync(requestId, vehicle, cancellationToken);
        var hasUnreleasedLock = lockSnapshot?.HasUnreleasedLock == true;
        var capturedLockInfo = lockSnapshot?.CapturedLockInfo;
        var isRobotLocked = robot.LockInfo?.Locked == true;

        // Defence in depth: the store already filters the record by vehicle, but the
        // returned record is revalidated here against the CURRENT preparation context
        // before it can authorise an automatic /unlock. The recorded robot is matched
        // through the shared identity so a uuid is never compared against a
        // vehicle_id, and the recorded target map must be the map being prepared now.
        // A record missing either value cannot be trusted and is treated as
        // uncorrelated.
        var recordedVehicleMatches = !string.IsNullOrWhiteSpace(lockSnapshot?.Vehicle)
                                     && robot.ResolvedIdentity.Matches(lockSnapshot!.Vehicle);
        var recordedMapMatches = !string.IsNullOrWhiteSpace(lockSnapshot?.TargetMap)
                                 && string.Equals(lockSnapshot!.TargetMap!.Trim(), requiredMap, StringComparison.OrdinalIgnoreCase);

        // Needed by the recovery eligibility check below as well as by the normal
        // transition flow, so it is resolved before either uses it.
        var mapPrepOrderId = RdsCoreNormalOrder.TransitionOrderId(requestId, requiredMap);

        var isLockCorrelated = hasUnreleasedLock
                               && recordedVehicleMatches
                               && recordedMapMatches
                               && capturedLockInfo is not null
                               && robot.LockInfo is not null
                               && capturedLockInfo.CorrelatesWith(robot.LockInfo);

        if (hasUnreleasedLock)
        {
            var isObservedUnlocked = robot.Dispatchable && robot.LockInfo?.Locked == false;
            if (isObservedUnlocked)
            {
                if (string.Equals(robot.CurrentMap?.Trim(), requiredMap, StringComparison.OrdinalIgnoreCase))
                {
                    await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_LOCK_RELEASED", "LOCK_ALREADY_RELEASED", $"Robot '{vehicle}' observed unlocked on required map; closing prior map-switch lock record.", correlationId, cancellationToken: cancellationToken);
                }
                else
                {
                    await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_BLOCKED", "MAP_SWITCH_LOCK_RECOVERY_REQUIRED", $"Robot '{vehicle}' has an unreleased map switch lock from a crashed attempt before map switch completed.", correlationId, cancellationToken: cancellationToken);
                    return new(requestId, taskRecordId, false, false, "MAP_SWITCH_LOCK_RECOVERY_REQUIRED", $"Robot '{vehicle}' has an unreleased map switch lock from a crashed attempt before map switch completed.");
                }
            }
            else
            {
                // Robot is still locked, not dispatchable, or lock_info is unavailable.
                //
                // Automatic /unlock is a physical mutation on a robot TKG believes it
                // left locked. Correlation proves the lock is OURS; it does not prove
                // that releasing it right now is SAFE. Ownership and safety are
                // separate questions and both must be answered BEFORE the adapter
                // call — previously the blocking-alarm and incompatible-order checks
                // sat further down this method and so ran only AFTER the unlock had
                // already been issued.
                //
                // Every condition is evaluated here, and the adapter call is last:
                //   A recovery record exists for this request      hasUnreleasedLock
                //   B the record names this robot                  recordedVehicleMatches
                //   C the record targets the map being prepared    recordedMapMatches
                //   D the robot currently reports itself locked    robotReportsLocked
                //   E the live lock fingerprint correlates         CorrelatesWith
                //   F the robot is on the required map now         currentMapMatches
                //   G no incompatible order is active              !hasIncompatibleOrder
                //   H no blocking (fatal/error) alarm              !HasBlockingAlarm
                //   I business state is documented-idle            IsBusinessIdle
                // A/B/C/E are folded into isLockCorrelated above.
                var currentMapMatches = string.Equals(
                    robot.CurrentMap?.Trim(), requiredMap, StringComparison.OrdinalIgnoreCase);
                var robotReportsLocked = robot.LockInfo?.Locked == true;

                // ANY non-terminal order blocks automatic recovery — including this
                // request's own main order and its own MAP_PREP transition order. An
                // earlier round exempted those two on the grounds that they belong to
                // this request, but ownership is not safety: a MAP_PREP order that is
                // still RUNNING or SUSPENDED means the map preparation this lock exists
                // for is still in progress, and releasing the lock underneath it is
                // exactly the unsafe case. SEER documents no order state that is
                // provably safe to unlock beneath, so the rule is: no active order at
                // all. A terminal own order simply leaves this check satisfied.
                var hasActiveOrder = IsOrderActive(robot.CurrentOrderId, robot.CurrentOrderState);

                // procBusiness is a NECESSARY condition, never sufficient — see
                // RdsCoreRobotTelemetry.MayBeExecutingBusinessWork for the documentation
                // ambiguity. The positive evidence that nothing is executing is
                // hasActiveOrder above; this only adds SEER's own report and fails
                // closed when it is true or absent.
                var recoveryEligible = isLockCorrelated
                    && currentMapMatches
                    && robotReportsLocked
                    && !hasActiveOrder
                    && !robot.HasBlockingAlarm
                    && !robot.MayBeExecutingBusinessWork;

                if (!recoveryEligible && isLockCorrelated)
                {
                    // The lock is ours but the runtime state forbids touching it now.
                    // Recorded so a commissioning engineer can see why recovery stalled.
                    var blockingReason = !currentMapMatches ? "MAP_MISMATCH"
                        : !robotReportsLocked ? "ROBOT_NOT_LOCKED"
                        : hasActiveOrder ? "ACTIVE_ORDER_ON_ROBOT"
                        : robot.HasBlockingAlarm ? "BLOCKING_ALARM"
                        : robot.ProcBusiness is null ? "BUSINESS_STATE_UNKNOWN"
                        : "BUSINESS_BUSY";
                    await store.WriteExecutionAuditAsync(
                        requestId,
                        "MAP_PREP_BLOCKED",
                        "LOCK_RECOVERY_UNSAFE",
                        $"Correlated map-switch lock on '{vehicle}' was not automatically released: {blockingReason}.",
                        correlationId,
                        cancellationToken: cancellationToken);
                }

                if (recoveryEligible)
                {
                    var unlockRecovery = await nativeOrderClient.UnlockAsync([vehicle], correlationId, cancellationToken);
                    if (unlockRecovery.CoreAccepted)
                    {
                        await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_LOCK_RELEASED", "LOCK_RECOVERY_SUCCESS", "Successfully released post-switchMap lock after correlated crash recovery.", correlationId, cancellationToken: cancellationToken);
                        robots = (await nativeOrderClient.GetRobotsStatusAsync(correlationId, cancellationToken)).Robots;
                        robot = ResolveUniqueRobot(robots, vehicle);
                    }
                }

                if (robot is null || robot.LockInfo?.Locked == true || !robot.Dispatchable)
                {
                    const string errorCode = "MAP_SWITCH_LOCK_RECOVERY_REQUIRED";
                    var errorMessage = $"Robot '{vehicle}' remains locked after a prior map-switch attempt; manual lock recovery is required.";
                    await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", errorCode, errorMessage, correlationId, cancellationToken: cancellationToken);
                    return new(requestId, taskRecordId, false, false, errorCode, errorMessage);
                }
            }
        }
        else if (!robot.Dispatchable)
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", "ROBOT_NOT_DISPATCHABLE", $"Robot '{vehicle}' is not dispatchable.", correlationId, cancellationToken: cancellationToken);
            return new(requestId, taskRecordId, false, false, "ROBOT_NOT_DISPATCHABLE", $"Robot '{vehicle}' is not dispatchable.");
        }

        // Only fatal/error alarms block dispatch. A warning or a notice is reported
        // but is not a fault, so it must not strand a healthy robot.
        if (robot.IsError || robot.HasBlockingAlarm)
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", "ROBOT_HAS_BLOCKING_ALARM", $"Robot '{vehicle}' has blocking errors/alarms.", correlationId, cancellationToken);
            return new(requestId, taskRecordId, false, false, "ROBOT_HAS_BLOCKING_ALARM", $"Robot '{vehicle}' has blocking errors/alarms.");
        }

        if (IsOrderActive(robot.CurrentOrderId, robot.CurrentOrderState)
            && !string.Equals(robot.CurrentOrderId, taskRecordId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(robot.CurrentOrderId, mapPrepOrderId, StringComparison.OrdinalIgnoreCase))
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", "ROBOT_BUSY_INCOMPATIBLE_ORDER", $"Robot '{vehicle}' is currently executing order '{robot.CurrentOrderId}'.", correlationId, cancellationToken);
            return new(requestId, taskRecordId, false, false, "ROBOT_BUSY_INCOMPATIBLE_ORDER", $"Robot '{vehicle}' is currently executing incompatible order '{robot.CurrentOrderId}'.");
        }

        var currentMap = robot.CurrentMap?.Trim();
        if (!string.Equals(currentMap, requiredMap, StringComparison.OrdinalIgnoreCase))
        {
            if (hasUnreleasedLock)
            {
                // Scenario A: Lock was acquired, but crash occurred before map switch completed
                await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_BLOCKED", "MAP_SWITCH_LOCK_RECOVERY_REQUIRED", $"Robot '{vehicle}' has an unreleased map switch lock from a crashed attempt before map switch completed.", correlationId, cancellationToken);
                return new(requestId, taskRecordId, false, false, "MAP_SWITCH_LOCK_RECOVERY_REQUIRED", $"Robot '{vehicle}' has an unreleased map switch lock from a crashed attempt before map switch completed.");
            }

            var transitionStation = navProfile.SwitchTransitionStation?.Trim();
            if (string.IsNullOrWhiteSpace(transitionStation))
            {
                await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", "SWITCH_TRANSITION_UNRESOLVED", "Current robot map differs from required map, but no switch transition station is configured in navigation profile.", correlationId, cancellationToken);
                return new(requestId, taskRecordId, false, false, "SWITCH_TRANSITION_UNRESOLVED", "Current robot map differs from required map, but no switch transition station is configured in navigation profile.");
            }

            var isAtStation = string.Equals(robot.CurrentStation?.Trim(), transitionStation, StringComparison.OrdinalIgnoreCase);
            if (!isAtStation)
            {
                RdsCoreOrderDetails? prepDetails = null;
                try
                {
                    prepDetails = await nativeOrderClient.GetOrderDetailsAsync(mapPrepOrderId, correlationId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Order not yet found or query failed. Cancellation propagates:
                    // the branch below decides whether to dispatch a *physical*
                    // transition navigation, so a cancelled caller must never fall
                    // through to a motion-enabling SetOrder.
                }

                if (prepDetails?.State.Known is RdsCoreKnownOrderState.Running or RdsCoreKnownOrderState.Created or RdsCoreKnownOrderState.Waiting or RdsCoreKnownOrderState.ToBeDispatched)
                {
                    await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_AWAITING", prepDetails.State.Known.ToString().ToUpperInvariant(), "Preliminary navigation to transition station is in progress.", correlationId, cancellationToken);
                    return new(requestId, mapPrepOrderId, false, false, "MAP_PREP_RUNNING", "Preliminary navigation to transition station is running.");
                }
                else if (prepDetails?.State.Known != RdsCoreKnownOrderState.Finished)
                {
                    var navBlock = new RdsCoreNavigationBlock(RdsCoreNormalOrder.TransitionBlockId(requestId), transitionStation);
                    var navOrder = RdsCoreNormalOrder.CreateTransition(requestId, requiredMap, navBlock, vehicle, context.TemplatePriority);
                    var navResult = await nativeOrderClient.SetOrderAsync(navOrder, correlationId, cancellationToken);
                    if (!navResult.CoreAccepted && !navResult.DuplicateOrderId)
                    {
                        await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_BLOCKED", navResult.Code?.ToString() ?? "REJECTED", navResult.Message ?? "SetOrder for map prep rejected.", correlationId, cancellationToken);
                        return new(requestId, mapPrepOrderId, false, false, "MAP_PREP_SET_ORDER_REJECTED", navResult.Message ?? "SetOrder for transition navigation was rejected.");
                    }

                    await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_DISPATCHED", "TRANSITION_NAVIGATION", $"Dispatched preliminary navigation to transition site '{transitionStation}'.", correlationId, cancellationToken);
                    return new(requestId, mapPrepOrderId, true, navResult.DuplicateOrderId, "MAP_PREP_DISPATCHED", "Dispatched preliminary navigation to transition station.");
                }
            }

            // Requirement 5: Transition Arrival Verification
            // Re-query /robotsStatus and strictly confirm current_station == transitionStation and robot health
            try
            {
                robots = (await nativeOrderClient.GetRobotsStatusAsync(correlationId, cancellationToken)).Robots;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_BLOCKED", "SWITCH_TRANSITION_NOT_CONFIRMED", $"Failed to query robot status for transition arrival verification: {ex.Message}", correlationId, cancellationToken);
                return new(requestId, taskRecordId, false, false, "SWITCH_TRANSITION_NOT_CONFIRMED", "Failed to query robot status for transition arrival verification.");
            }

            robot = ResolveUniqueRobot(robots, vehicle);
            if (robot is null
                || robot.ConnectionStatus != 1
                || !robot.Dispatchable
                || robot.IsError
                // Fatal/error only: a standing notice must not permanently prevent a
                // legitimate map switch.
                || robot.HasBlockingAlarm
                || (IsOrderActive(robot.CurrentOrderId, robot.CurrentOrderState) && !string.Equals(robot.CurrentOrderId, mapPrepOrderId, StringComparison.OrdinalIgnoreCase))
                || !string.Equals(robot.CurrentStation?.Trim(), transitionStation, StringComparison.OrdinalIgnoreCase))
            {
                await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_BLOCKED", "SWITCH_TRANSITION_NOT_CONFIRMED", $"Transition station arrival could not be confirmed. Station='{robot?.CurrentStation}', expected='{transitionStation}'.", correlationId, cancellationToken);
                return new(requestId, taskRecordId, false, false, "SWITCH_TRANSITION_NOT_CONFIRMED", $"Transition station arrival could not be confirmed. Robot station is '{robot?.CurrentStation}', expected '{transitionStation}'.");
            }

            RdsCoreSimpleResult lockResult;
            try
            {
                lockResult = await nativeOrderClient.LockAsync([vehicle], correlationId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_BLOCKED", "LOCK_FAILED", ex.Message, correlationId, cancellationToken);
                return new(requestId, taskRecordId, false, false, "LOCK_FAILED", ex.Message);
            }

            if (!lockResult.CoreAccepted)
            {
                await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_BLOCKED", "LOCK_FAILED", lockResult.Message ?? "Lock vehicle rejected before switchMap.", correlationId, cancellationToken);
                return new(requestId, taskRecordId, false, false, "LOCK_FAILED", lockResult.Message ?? "Lock vehicle rejected before switchMap.");
            }

            // Immediately query /robotsStatus to capture lock_info fingerprint
            RdsCoreRobotLockInfo? activeLockInfo = null;
            try
            {
                var postLockRobots = (await nativeOrderClient.GetRobotsStatusAsync(correlationId, cancellationToken)).Robots;
                var postLockRobot = ResolveUniqueRobot(postLockRobots, vehicle);
                activeLockInfo = postLockRobot?.LockInfo;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Telemetry capture failure does not block switchMap attempt, but leaves lock uncorrelatable on crash.
                // Cancellation propagates instead: SwitchMap below is a physical
                // mutation, so a cancelled caller must not continue into it.
            }

            await store.WriteExecutionAuditAsync(
                requestId,
                "MAP_PREP_LOCK_ACQUIRED",
                "LOCK_ACQUIRED",
                $"Lock acquired on vehicle '{vehicle}' for map switch to '{requiredMap}'.",
                correlationId,
                extraDetails: new { vehicle, requiredMap, lockInfo = activeLockInfo },
                cancellationToken: cancellationToken);

            RdsCoreSimpleResult switchResult;
            try
            {
                switchResult = await nativeOrderClient.SwitchMapAsync(vehicle, requiredMap, correlationId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_BLOCKED", "SWITCHMAP_FAILED", ex.Message, correlationId, cancellationToken);
                return new(requestId, taskRecordId, false, false, "SWITCHMAP_FAILED", ex.Message);
            }
            finally
            {
                try
                {
                    var unlockResult = await nativeOrderClient.UnlockAsync([vehicle], correlationId, cancellationToken);
                    if (unlockResult.CoreAccepted)
                    {
                        await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_LOCK_RELEASED", "UNLOCK_SUCCESS", $"Lock released on vehicle '{vehicle}'.", correlationId, cancellationToken);
                    }
                }
                catch
                {
                    // If unlock throws or fails, MAP_PREP_LOCK_RELEASED is not written, enabling safe recovery on restart.
                    // This one deliberately swallows cancellation too: throwing out of a
                    // finally block would replace the real SWITCHMAP_FAILED cause, and
                    // swallowing is the fail-safe direction here — the lock stays recorded
                    // as unreleased, so recovery blocks rather than assuming it was freed.
                }
            }

            if (!switchResult.CoreAccepted)
            {
                await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_BLOCKED", "SWITCHMAP_REJECTED", switchResult.Message ?? "SwitchMap rejected by RDSCore.", correlationId, cancellationToken);
                return new(requestId, taskRecordId, false, false, "SWITCHMAP_REJECTED", switchResult.Message ?? "SwitchMap rejected by RDSCore.");
            }

            try
            {
                robots = (await nativeOrderClient.GetRobotsStatusAsync(correlationId, cancellationToken)).Robots;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_BLOCKED", "POST_SWITCH_TELEMETRY_FAILED", ex.Message, correlationId, cancellationToken);
                return new(requestId, taskRecordId, false, false, "POST_SWITCH_TELEMETRY_FAILED", ex.Message);
            }
            robot = ResolveUniqueRobot(robots, vehicle);
            if (robot is null || !string.Equals(robot.CurrentMap?.Trim(), requiredMap, StringComparison.OrdinalIgnoreCase))
            {
                await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_BLOCKED", "SWITCHMAP_VERIFICATION_FAILED", $"Robot map is '{robot?.CurrentMap}', expected '{requiredMap}'.", correlationId, cancellationToken);
                return new(requestId, taskRecordId, false, false, "SWITCHMAP_VERIFICATION_FAILED", "Robot did not switch to the required map.");
            }

            if (robot.IsError || robot.HasBlockingAlarm)
            {
                await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_BLOCKED", "POST_SWITCH_ALARM", "Robot has alarms following map switch.", correlationId, cancellationToken);
                return new(requestId, taskRecordId, false, false, "POST_SWITCH_ALARM", "Robot has alarms following map switch.");
            }

            await store.WriteExecutionAuditAsync(requestId, "MAP_PREP_CONFIRMED", "SWITCHMAP_SUCCESS", $"Successfully switched robot map to '{requiredMap}'.", correlationId, cancellationToken);
        }

        var pickupLocation = GetLocation(context.TemplateConfig, "pickupLocation")
            ?? context.Source.SiteId;
        var dropoffLocation = GetLocation(context.TemplateConfig, "dropoffLocation")
            ?? context.Destination.SiteId;

        if (string.IsNullOrWhiteSpace(pickupLocation) || string.IsNullOrWhiteSpace(dropoffLocation))
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", "RDS_DISPATCH_ENDPOINT_UNRESOLVED", "Native order dispatch requires resolved pickup and dropoff locations from the task template or layout slot mapping.", correlationId, cancellationToken);
            return new(requestId, taskRecordId, false, false, "RDS_DISPATCH_ENDPOINT_UNRESOLVED", "Native order dispatch requires resolved pickup and dropoff locations from the task template or layout slot mapping.");
        }

        var pickupBlock = new RdsCoreNavigationBlock(RdsCoreNormalOrder.PickupBlockId(requestId), pickupLocation);
        var dropoffBlock = new RdsCoreNavigationBlock(RdsCoreNormalOrder.DropoffBlockId(requestId), dropoffLocation);
        var order = RdsCoreNormalOrder.Create(
            requestId,
            pickupBlock,
            dropoffBlock,
            vehicle: vehicle,
            priority: context.TemplatePriority);

        var requestPayload = JsonSerializer.SerializeToElement(order);
        await store.EnsureTaskRecordAsync(requestId, taskRecordId, taskLabel, requestPayload, cancellationToken);
        await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_ATTEMPTED", "RDS_SET_ORDER", "Native order dispatch was attempted with the immutable TKG request identity.", correlationId, cancellationToken);

        RdsCoreSetOrderResult? setResult = null;
        try
        {
            setResult = await nativeOrderClient!.SetOrderAsync(order, correlationId, cancellationToken);
            await store.RecordDispatchResponseAsync(
                requestId,
                setResult.RawResponse ?? JsonSerializer.SerializeToElement(new { code = setResult.Code, message = setResult.Message }),
                timeProvider.GetUtcNow(),
                cancellationToken);
        }
        catch (RdsCoreNativeOrderException)
        {
            // A lost HTTP response or timeout is ambiguous. Query by taskRecordId before deciding whether to retry.
        }

        if (setResult is not null && !setResult.CoreAccepted && !setResult.DuplicateOrderId)
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_BLOCKED", setResult.Code?.ToString() ?? "REJECTED", setResult.Message ?? "SetOrder was rejected.", correlationId, cancellationToken);
            return new(requestId, taskRecordId, false, false, "RDS_SET_ORDER_REJECTED", setResult.Message ?? "SetOrder was rejected.");
        }

        RdsCoreOrderDetails? details = null;
        try
        {
            details = await nativeOrderClient!.GetOrderDetailsAsync(taskRecordId, correlationId, cancellationToken);
        }
        catch (RdsCoreNativeOrderException)
        {
            // Order details query failed or order not yet observed. Request remains queued for reconciliation.
        }

        if (details is null)
        {
            await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_UNCONFIRMED", "RDS_TASK_NOT_YET_OBSERVED", "The native order response was not confirmed; the request remains queued.", correlationId, cancellationToken);
            return new(requestId, taskRecordId, false, setResult?.DuplicateOrderId ?? false, "RDS_TASK_NOT_YET_OBSERVED", "SetOrder was not confirmed; reconciliation will retry the same identity.");
        }

        var status = MapNativeStateToStatus(details.State.Known);
        var observedVehicle = ExtractVehicle(details.Raw, vehicle);
        var taskSnapshot = new RdsWindTaskSnapshot(
            taskRecordId,
            taskLabel,
            status,
            observedVehicle,
            taskRecordId,
            details.Raw,
            details.State.Raw);

        await store.RecordTaskStatusAsync(requestId, taskSnapshot, timeProvider.GetUtcNow(), cancellationToken);
        await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.SentToRds, OperationalEventContext.System(correlationId), cancellationToken);
        if (details.State.Known == RdsCoreKnownOrderState.Running)
        {
            await workflow.RecordExecutionTransitionAsync(requestId, TransportRequestStatus.Running, OperationalEventContext.System(correlationId), cancellationToken);
        }
        await store.WriteExecutionAuditAsync(requestId, "RDS_DISPATCH_CONFIRMED", "RDS_NATIVE_ORDER_OBSERVED", "RDS native order existence was proven by taskRecordId.", correlationId, cancellationToken);
        return new(requestId, taskRecordId, true, setResult?.DuplicateOrderId ?? true, "RDS_TASK_OBSERVED", "RDS native order existence was proven by taskRecordId.");
    }

    private static string? GetLocation(JsonElement config, string propertyName)
    {
        if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        return null;
    }

    private static string? ExtractVehicle(JsonElement raw, string? fallback)
    {
        if (raw.ValueKind == JsonValueKind.Object)
        {
            if (raw.TryGetProperty("candidateName", out var cProp) && cProp.ValueKind == JsonValueKind.String)
            {
                var s = cProp.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            if (raw.TryGetProperty("vehicle", out var vProp) && vProp.ValueKind == JsonValueKind.String)
            {
                var s = vProp.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }
        return fallback;
    }

    private static int? MapNativeStateToStatus(RdsCoreKnownOrderState state) => state switch
    {
        RdsCoreKnownOrderState.Running => 1000,
        RdsCoreKnownOrderState.Finished => 1003,
        RdsCoreKnownOrderState.Stopped => 1001,
        RdsCoreKnownOrderState.Failed or RdsCoreKnownOrderState.Error => 1004,
        _ => null
    };

    public async Task<RdsCallbackResult> ProcessCallbackAsync(RdsCallbackEvent callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var eventType = callback.EventType.Trim();
        if (eventType is not ("taskDone" or "taskFailed" or "taskStopped" or "workSiteChanged"))
        {
            return new(true, false, "RDS_EVENT_IGNORED", "Unknown callback events do not mutate domain state.", null);
        }

        if (eventType == "workSiteChanged")
        {
            return new(true, false, "RDS_WORKSITE_EVENT_ACCEPTED", "WorkSite evidence will be refreshed by the read-side synchronization path.", null);
        }

        var taskRecordId = FindText(callback.RawPayload, "taskRecordId") ?? FindText(callback.RawPayload, "id");
        if (string.IsNullOrWhiteSpace(taskRecordId))
        {
            return new(true, false, "RDS_TASK_ID_UNRESOLVED", "The callback did not expose a taskRecordId.", null);
        }

        var projection = await store.FindTaskByRecordIdAsync(taskRecordId, cancellationToken);
        if (projection is null)
        {
            return new(true, false, "RDS_TASK_UNKNOWN", "The callback identity is not owned by TKG.", taskRecordId);
        }

        var status = eventType switch
        {
            "taskDone" => 1003,
            "taskFailed" => Number(callback.RawPayload, "status") is 1004 or 1005 or 1006 ? Number(callback.RawPayload, "status")!.Value : 1004,
            "taskStopped" => 1001,
            _ => (int?)null
        };
        var task = new RdsWindTaskSnapshot(
            taskRecordId,
            FindText(callback.RawPayload, "taskLabel") ?? projection.TaskLabel,
            status,
            FindText(callback.RawPayload, "agvId"),
            FindText(callback.RawPayload, "outOrderNo"),
            callback.RawPayload.Clone(),
            FindText(callback.RawPayload, "endedReason"));
        var callbackCorrelationId = Guid.NewGuid();
        await ApplyTaskStatusAsync(projection, task, timeProvider.GetUtcNow(), $"CALLBACK_{eventType}", cancellationToken, callbackCorrelationId);
        await store.WriteExecutionAuditAsync(
            projection.TransportRequestId,
            "RDS_CALLBACK_APPLIED",
            eventType,
            "Authenticated RDS callback evidence was applied idempotently.",
            callbackCorrelationId,
            cancellationToken);
        return new(true, true, "RDS_EVENT_APPLIED", "The callback was applied idempotently.", taskRecordId);
    }

    public async Task<RdsLoadContentSyncResult> SynchronizeLoadContentAsync(Guid slotId, LoadState loadState, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var configured = loadState switch
        {
            LoadState.Empty => settings.EmptyContentValue,
            LoadState.Full => settings.FullContentValue,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new("NOT_CONFIGURED", null, null, "TKG load state was updated; RDS content semantics are not configured.");
        }

        var site = await store.FindMappedWorkSiteAsync(slotId, cancellationToken);
        if (site is null)
        {
            return new("NOT_MAPPED", null, configured, "TKG load state was updated; this slot has no RDS WorkSite mapping.");
        }

        if (site.Type != 1 || site.Disabled == 1 || site.SyncFailed == 1)
        {
            return new("NOT_READY", site.SiteId, configured, "TKG load state was updated; the mapped physical WorkSite is not execution-ready.");
        }

        await executionClient.UpdateContentAsync(site.SiteId, configured, correlationId, cancellationToken);
        return new("SYNCHRONIZED", site.SiteId, configured, "Only RDS content was changed; filled was not modified.");
    }

    private async Task ApplyTaskStatusAsync(
        RdsTaskProjection projection,
        RdsWindTaskSnapshot task,
        DateTimeOffset syncedAt,
        string evidence,
        CancellationToken cancellationToken,
        Guid? correlationId = null)
    {
        var eventContext = OperationalEventContext.System(correlationId ?? Guid.NewGuid());
        await store.RecordTaskStatusAsync(projection.TransportRequestId, task, syncedAt, cancellationToken);
        switch (task.Status)
        {
            case 1000:
                if (projection.RequestStatus == TransportRequestStatus.SentToRds)
                {
                    await workflow.RecordExecutionTransitionAsync(projection.TransportRequestId, TransportRequestStatus.Running, eventContext, cancellationToken);
                }
                break;
            case 1002:
                break;
            case 1003:
                if (projection.RequestStatus == TransportRequestStatus.SentToRds)
                {
                    await workflow.RecordExecutionTransitionAsync(projection.TransportRequestId, TransportRequestStatus.Running, eventContext, cancellationToken);
                }
                if (projection.RequestStatus is TransportRequestStatus.SentToRds or TransportRequestStatus.Running)
                {
                    await workflow.RecordExecutionTransitionAsync(projection.TransportRequestId, TransportRequestStatus.Completed, eventContext, cancellationToken);
                }
                break;
            case 1001:
                await workflow.RecordExecutionOutcomeAsync(
                    projection.TransportRequestId,
                    projection.CancellationRequested
                        ? new(TransportRequestStatus.Cancelled, EvidenceType: evidence)
                        : new(TransportRequestStatus.Failed, "RDS_TASK_TERMINATED", task.EndedReason, evidence),
                    eventContext,
                    cancellationToken);
                break;
            case 1004:
            case 1005:
            case 1006:
                await workflow.RecordExecutionOutcomeAsync(
                    projection.TransportRequestId,
                    new(TransportRequestStatus.Failed, $"RDS_TASK_STATUS_{task.Status}", task.EndedReason, evidence),
                    eventContext,
                    cancellationToken);
                break;
        }
    }

    private async Task RefreshRelevantWorkSitesAsync(
        Guid requestId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var request = await store.FindRequestAsync(requestId, cancellationToken);
        if (request?.NavigationProfileId is not Guid navigationProfileId)
        {
            await store.WriteExecutionAuditAsync(
                requestId,
                "RDS_WORKSITE_REFRESH_SKIPPED",
                "NAVIGATION_PROFILE_NOT_CONFIGURED",
                "Terminal task evidence was applied, but the request has no navigation profile to refresh.",
                correlationId,
                cancellationToken);
            return;
        }

        try
        {
            await workSiteReconciler.SyncWorkSitesAsync(navigationProfileId, correlationId, cancellationToken);
            await store.WriteExecutionAuditAsync(
                requestId,
                "RDS_WORKSITE_REFRESHED",
                "RDS_WORKSITE_EVIDENCE_REFRESHED",
                "Relevant RDS WorkSite evidence was refreshed after a terminal task status.",
                correlationId,
                cancellationToken);
        }
        catch (RdsReadException error)
        {
            await store.WriteExecutionAuditAsync(
                requestId,
                "RDS_WORKSITE_REFRESH_DEFERRED",
                error.Code,
                "Terminal workflow evidence was retained; WorkSite refresh will retry without guessing physical state.",
                correlationId,
                cancellationToken);
        }
        catch (RdsIntegrationRuleException error)
        {
            await store.WriteExecutionAuditAsync(
                requestId,
                "RDS_WORKSITE_REFRESH_DEFERRED",
                error.Code,
                "Terminal workflow evidence was retained; WorkSite refresh will retry without guessing physical state.",
                correlationId,
                cancellationToken);
        }
    }

    private static bool IsTerminalRdsStatus(int? status) => status is 1001 or 1003 or 1004 or 1005 or 1006;

    private void ValidateReferencedEndpoints(RdsDispatchContext context, IReadOnlyDictionary<string, object?> inputParams)
    {
        if (context.Source.SlotRole == "MACHINE" || context.Destination.SlotRole == "MACHINE")
        {
            throw new RdsExecutionException("MACHINE_REGION_FORBIDDEN", "Display-only MACHINE regions can never reach RDS dispatch.");
        }

        var raw = context.TemplateConfig.GetProperty("rdsInputBindings");
        foreach (var binding in raw.EnumerateArray())
        {
            var source = binding.GetProperty("source").GetString();
            if (source == "SOURCE_RDS_SITE_ID") ValidateEndpoint(context.Source, "source");
            if (source == "DESTINATION_RDS_SITE_ID") ValidateEndpoint(context.Destination, "destination");
        }

        _ = inputParams;
    }

    private void ValidateEndpoint(RdsDispatchEndpoint endpoint, string name)
    {
        if (endpoint.SlotRole == "MACHINE") throw new RdsExecutionException("MACHINE_REGION_FORBIDDEN", $"The {name} endpoint is a display-only MACHINE region.");
        if (!endpoint.IsMapped || string.IsNullOrWhiteSpace(endpoint.SiteId)) throw new RdsExecutionException("RDS_WORKSITE_NOT_MAPPED", $"The {name} endpoint has no explicit WorkSite mapping.");
        if (endpoint.Type != 1) throw new RdsExecutionException("RDS_WORKSITE_NOT_PHYSICAL", $"The {name} WorkSite is not physical.");
        if (endpoint.Disabled == 1) throw new RdsExecutionException("RDS_WORKSITE_DISABLED", $"The {name} WorkSite is disabled.");
        if (endpoint.SyncFailed == 1) throw new RdsExecutionException("RDS_WORKSITE_SYNC_FAILED", $"The {name} WorkSite reports syncFailed.");
        if (endpoint.Locked == 1) throw new RdsExecutionException("RDS_WORKSITE_LOCKED", $"The {name} WorkSite is locked.");
        if (!endpoint.LastSyncAt.HasValue || timeProvider.GetUtcNow() - endpoint.LastSyncAt.Value > settings.WorkSiteFreshness)
            throw new RdsExecutionException("RDS_WORKSITE_STALE", $"The {name} WorkSite evidence is stale.");
    }

    private static string? FindText(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
                }
                var nested = FindText(property.Value, name);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var nested = FindText(item, name);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    private static int? Number(JsonElement value, string name)
    {
        var text = FindText(value, name);
        return int.TryParse(text, out var number) ? number : null;
    }

    private static bool IsOrderActive(string? orderId, string? orderState) =>
        !string.IsNullOrWhiteSpace(orderId)
        && !string.Equals(orderState, "FINISHED", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(orderState, "TERMINATED", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(orderState, "CANCELLED", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(orderState, "CANCELED", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(orderState, "COMPLETED", StringComparison.OrdinalIgnoreCase);
}
