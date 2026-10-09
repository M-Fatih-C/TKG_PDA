using TkgPda.Domain;

namespace TkgPda.Application;

public sealed record PdaActorContext(
    Guid UserId,
    IReadOnlySet<string> Roles,
    bool IsDeviceBound);

public sealed class PdaOperationalContextService(IApiDataStore store)
{
    public async Task<OperationalContextRecord?> GetAsync(
        PdaActorContext actor,
        Guid machineId,
        bool administrator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var data = await store.GetOperationalContextDataAsync(
            actor.UserId,
            machineId,
            administrator,
            cancellationToken);
        return data is null ? null : PdaOperationalContextPolicy.Project(data, actor);
    }
}

public static class PdaOperationalContextPolicy
{
    /// <summary>
    /// The actions the PDA may offer a user for a slot. REMOVE_EMPTY is deliberately
    /// absent: it is internal orchestration vocabulary that the Remove-All workflow
    /// generates for OCCUPIED+EMPTY positions, not a standalone Operator action. A
    /// REMOVE_EMPTY request created that way still appears in the active-request list
    /// and renders normally; it simply cannot be selected as a new action here.
    /// </summary>
    /// BRING_FULL is deliberately absent too, for a different reason: it is a
    /// real operator intent, but its source is a shared warehouse preparation
    /// area that no layout owns yet, so a request raised here could be created
    /// and prepared and still never dispatch. Offering the button before that
    /// model exists would put a dead action on the machine screen. It is added
    /// once shared preparation areas can resolve a source.
    private static readonly TransportTaskType[] SupportedActions =
    [
        TransportTaskType.BringEmpty,
        TransportTaskType.RemoveFull
    ];

    public static OperationalContextRecord Project(OperationalContextDataRecord data, PdaActorContext actor)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(actor);

        var busySlots = data.ActiveTransportRequests
            .SelectMany(static request => new[] { request.SourceSlotId, request.DestinationSlotId })
            .Where(static slotId => slotId.HasValue)
            .Select(static slotId => slotId!.Value)
            .ToHashSet();

        var slots = data.Slots
            .Select(slot => ProjectSlot(slot, actor, data.Machine.Permissions.CanOperate, busySlots.Contains(slot.Id)))
            .ToArray();
        // No role-dependent technical branch: every PDA role receives the same
        // business-safe execution projection. Elevated PDA authority grants more
        // ACTIONS, never more RDS technical detail.
        var requests = data.ActiveTransportRequests
            .Select(request => ProjectRequest(request, actor, data.Machine.Permissions.CanOperate))
            .ToArray();

        return new OperationalContextRecord(
            data.Machine,
            data.ActiveVariantId,
            data.ActiveVariantCode,
            data.ActiveVariantName,
            data.ActiveLayoutId,
            data.ActiveLayoutCode,
            data.ActiveLayoutName,
            data.ActiveNavigationProfileId,
            data.ActiveNavigationProfileCode,
            data.RuntimeRowVersion,
            data.RuntimeUpdatedAt,
            data.VariantLayouts,
            slots,
            data.ProductGroups,
            requests,
            data.TaskTemplates);
    }

    public static IReadOnlyList<PdaAvailableActionRecord> EvaluateActions(
        SlotContextDataRecord slot,
        PdaActorContext actor,
        bool canOperate,
        bool hasActiveRequest)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(actor);
        return SupportedActions
            .Select(action => EvaluateAction(slot, actor, canOperate, hasActiveRequest, action))
            .ToArray();
    }

    private static SlotContextRecord ProjectSlot(
        SlotContextDataRecord slot,
        PdaActorContext actor,
        bool canOperate,
        bool hasActiveRequest)
    {
        var readiness = ExecutionReadiness(slot.RdsWorkSite);
        return new SlotContextRecord(
            slot.Id,
            slot.Code,
            slot.DisplayName,
            slot.Role,
            slot.IsEnabled,
            slot.TransportEligible,
            slot.DefaultContainerTypeId,
            slot.DefaultContainerCode,
            slot.X,
            slot.Y,
            slot.Width,
            slot.Height,
            slot.DisplayOrder,
            slot.OccupancyState,
            slot.LoadState,
            slot.CurrentPartId,
            slot.CurrentPartCode,
            slot.CurrentContainerTypeId,
            slot.CurrentContainerCode,
            slot.RowVersion,
            slot.RuntimeUpdatedAt,
            // slot.RdsWorkSite stays server-side: `readiness` is its business-safe
            // distillation and is the only part the Operator contract carries.
            readiness,
            EvaluateActions(slot, actor, canOperate, hasActiveRequest),
            EvaluateLoadStateActions(slot, actor, canOperate, hasActiveRequest));
    }

    public static IReadOnlyList<PdaAvailableActionRecord> EvaluateLoadStateActions(
        SlotContextDataRecord slot,
        PdaActorContext actor,
        bool canOperate,
        bool hasActiveRequest) =>
        [
            EvaluateLoadStateAction(slot, actor, canOperate, hasActiveRequest, LoadState.Full),
            EvaluateLoadStateAction(slot, actor, canOperate, hasActiveRequest, LoadState.Empty)
        ];

    private static PdaAvailableActionRecord EvaluateLoadStateAction(
        SlotContextDataRecord slot,
        PdaActorContext actor,
        bool canOperate,
        bool hasActiveRequest,
        LoadState target)
    {
        var action = target == LoadState.Full ? "MARK_FULL" : "MARK_EMPTY";
        if (!actor.IsDeviceBound)
        {
            return Disabled(action, "PDA_DEVICE_REQUIRED", "Bu işlem yalnız cihaz bağlı PDA oturumunda kullanılabilir.");
        }

        var roleAllowed = actor.Roles.Contains(KnownRoleCodes.Admin)
            || actor.Roles.Contains(KnownRoleCodes.Supervisor)
            || actor.Roles.Contains(KnownRoleCodes.LineOperator);
        if (!roleAllowed)
        {
            return Disabled(action, "OPERATION_NOT_ALLOWED_FOR_ROLE", "Rolünüz kasa yük durumunu değiştirmeye yetkili değildir.");
        }

        if (!canOperate)
        {
            return Disabled(action, "MACHINE_OPERATION_NOT_PERMITTED", "Bu makinede operasyon yetkiniz bulunmuyor.");
        }

        if (slot.Role == "MACHINE")
        {
            return Disabled(action, "MACHINE_REGION_FORBIDDEN", "Makine/hücre bölgelerinde kasa yük durumu değiştirilemez.");
        }

        if (!slot.IsEnabled)
        {
            return Disabled(action, "SLOT_DISABLED", "Bu lojistik konumu aktif değildir.");
        }

        if (hasActiveRequest)
        {
            return Disabled(action, "SLOT_BUSY", "Bu lojistik konumunu kullanan aktif bir taşıma talebi bulunuyor.");
        }

        if (slot.OccupancyState != "OCCUPIED")
        {
            return Disabled(action, "OCCUPIED_SLOT_REQUIRED", "Yük durumu yalnız dolu bir konum için bildirilebilir.");
        }

        if (slot.LoadState == target.ToDatabase())
        {
            return Disabled(action, "LOAD_STATE_ALREADY_REPORTED", "Kasa zaten bu yük durumundadır.");
        }

        return new(action, true, null, null);
    }

    private static PdaExecutionReadinessRecord ExecutionReadiness(RdsWorkSiteProjectionRecord workSite)
    {
        if (workSite.IsMapped && workSite.PhysicalState is { } state)
        {
            var code = workSite.ConsistencyResult == "PHYSICAL_STATE_MISMATCH" ? "RDS_WORKSITE_OCCUPANCY_CONFLICT"
                : RdsPhysicalAvailabilityPolicy.ErrorCode(state)
                    ?? (workSite.ConsistencyResult == "PHYSICAL_STATE_UNKNOWN" ? "RDS_WORKSITE_STATE_UNKNOWN" : null);
            return new(true, code is null, code,
                code is null ? null : "Fiziksel alanı ve TKG konumunu kontrol edip RDS senkronundan sonra yeniden deneyin.", workSite.ConsistencyResult);
        }

        if (!workSite.IsMapped)
        {
            return new(false, false, "RDS_WORKSITE_NOT_MAPPED", "Bu lojistik konumu henüz bir RDS WorkSite ile eşleştirilmemiştir.");
        }

        if (workSite.Type != 1)
        {
            return new(true, false, "RDS_WORKSITE_NOT_PHYSICAL", "Eşlenen RDS WorkSite fiziksel bir taşıma noktası değildir.");
        }

        if (!workSite.LastSyncAt.HasValue)
        {
            return new(true, false, "RDS_WORKSITE_STALE", "Eşlenen RDS WorkSite için başarılı senkronizasyon kanıtı yoktur.");
        }

        if (workSite.Disabled == 1)
        {
            return new(true, false, "RDS_WORKSITE_DISABLED", "Eşlenen RDS WorkSite devre dışıdır.");
        }

        if (workSite.SyncFailed == 1)
        {
            return new(true, false, "RDS_WORKSITE_SYNC_FAILED", "RDS WorkSite senkronizasyon hatası bildiriyor.");
        }

        if (workSite.Locked == 1)
        {
            return new(true, false, "RDS_WORKSITE_LOCKED", "RDS WorkSite şu anda kilitli veya meşguldür.");
        }

        return new(true, true, null, null);
    }

    private static PdaAvailableActionRecord EvaluateAction(
        SlotContextDataRecord slot,
        PdaActorContext actor,
        bool canOperate,
        bool hasActiveRequest,
        TransportTaskType action)
    {
        var operation = action.ToDatabase();
        if (!actor.IsDeviceBound)
        {
            return Disabled(operation, "PDA_DEVICE_REQUIRED", "Bu işlem yalnız cihaz bağlı PDA oturumunda kullanılabilir.");
        }

        var canRemove = actor.Roles.Contains(KnownRoleCodes.Admin)
            || actor.Roles.Contains(KnownRoleCodes.Supervisor)
            || actor.Roles.Contains(KnownRoleCodes.LineOperator);
        var canBring = canRemove || actor.Roles.Contains(KnownRoleCodes.WarehouseOperator);
        if ((TransportRules.DeliversToMachine(action) && !canBring)
            || (!TransportRules.DeliversToMachine(action) && !canRemove))
        {
            return Disabled(operation, "OPERATION_NOT_ALLOWED_FOR_ROLE", "Rolünüz bu lojistik işlemini başlatmaya yetkili değildir.");
        }

        if (!canOperate)
        {
            return Disabled(operation, "MACHINE_OPERATION_NOT_PERMITTED", "Bu makinede operasyon yetkiniz bulunmuyor.");
        }

        if (slot.Role == "MACHINE")
        {
            return Disabled(operation, "MACHINE_REGION_FORBIDDEN", "Makine/hücre bölgeleri yalnız ekran referansıdır ve taşıma noktası değildir.");
        }

        if (!slot.IsEnabled)
        {
            return Disabled(operation, "SLOT_DISABLED", "Bu lojistik konumu aktif değildir.");
        }

        if (hasActiveRequest)
        {
            return Disabled(operation, "SLOT_BUSY", "Bu lojistik konumunu kullanan aktif bir taşıma talebi bulunuyor.");
        }

        if (slot.RdsWorkSite.IsMapped && slot.RdsWorkSite.PhysicalState is not null)
        {
            var readiness = ExecutionReadiness(slot.RdsWorkSite);
            if (!readiness.IsExecutionReady) return Disabled(operation, readiness.ReadinessCode!, readiness.ReadinessMessage!);
        }

        var stateMatches = action switch
        {
            TransportTaskType.BringEmpty or TransportTaskType.BringFull =>
                slot.OccupancyState == "VACANT" && slot.LoadState == "UNKNOWN",
            TransportTaskType.RemoveFull => slot.OccupancyState == "OCCUPIED" && slot.LoadState == "FULL",
            TransportTaskType.RemoveEmpty => slot.OccupancyState == "OCCUPIED" && slot.LoadState == "EMPTY",
            _ => false
        };
        return stateMatches
            ? new PdaAvailableActionRecord(operation, true, null, null)
            : Disabled(operation, "SLOT_STATE_MISMATCH", "Konumun mevcut doluluk/yük durumu bu işlemle uyumlu değildir.");
    }

    private static ActiveTransportRequestRecord ProjectRequest(
        ActiveTransportDataRecord request,
        PdaActorContext actor,
        bool canOperate)
    {
        var cancellation = EvaluateCancellation(request, actor, canOperate);
        return new ActiveTransportRequestRecord(
            request.RequestId,
            request.RequestNo,
            request.Operation.ToDatabase(),
            request.Status.ToDatabase(),
            request.RequestedBy,
            request.RequestedByUsername,
            request.IsMine,
            request.SourceSlotId,
            request.DestinationSlotId,
            request.ProductGroupId,
            request.PartId,
            request.ContainerTypeId,
            request.Priority,
            request.CreatedAt,
            request.PartCode,
            request.PartDescription,
            request.RelationType,
            request.ReadinessState,
            request.QueuePosition,
            Math.Max(0, request.AgeSeconds),
            request.ExecutionElapsedSeconds is { } elapsed ? Math.Max(0, elapsed) : null,
            cancellation.Enabled,
            cancellation.DisabledCode,
            cancellation.DisabledMessage,
            PdaExecutionProjector.Project(request),
            request.ChainState,
            request.ProductionEnd);
    }

    private static PdaAvailableActionRecord EvaluateCancellation(
        ActiveTransportDataRecord request,
        PdaActorContext actor,
        bool canOperate)
    {
        if (!actor.IsDeviceBound)
        {
            return Disabled("CANCEL", "PDA_DEVICE_REQUIRED", "İptal işlemi yalnız cihaz bağlı PDA oturumunda kullanılabilir.");
        }

        if (!canOperate)
        {
            return Disabled("CANCEL", "MACHINE_OPERATION_NOT_PERMITTED", "Bu makinede operasyon yetkiniz bulunmuyor.");
        }

        // WP-05: the machine-scoped cancel endpoint requires PolicyNames.ProductionOperator
        // (ADMIN, SUPERVISOR, LINE_OPERATOR). No warehouse role may cancel, not even its
        // own delivery, so the UI must not offer a button that the endpoint refuses.
        var mayCancel = actor.Roles.Contains(KnownRoleCodes.Admin)
            || actor.Roles.Contains(KnownRoleCodes.Supervisor)
            || actor.Roles.Contains(KnownRoleCodes.LineOperator);
        if (!mayCancel)
        {
            return Disabled("CANCEL", "CANCELLATION_NOT_ALLOWED_FOR_ROLE", "Bu talebi iptal etme yetkiniz bulunmuyor.");
        }

        return request.Status is TransportRequestStatus.SentToRds or TransportRequestStatus.Running
            ? new PdaAvailableActionRecord("CANCEL", true, null, null)
            : TransportStateMachine.CanCancelBeforeExecution(request.Status)
            ? new PdaAvailableActionRecord("CANCEL", true, null, null)
            : Disabled(
                "CANCEL",
                WorkflowErrorCodes.CancellationAfterExecutionDeferred,
                "RDS yürütme sınırına ulaşmış bir talep PDA üzerinden iptal edilemez.");
    }

    private static PdaAvailableActionRecord Disabled(string action, string code, string message) =>
        new(action, false, code, message);
}

public static class PdaExecutionProjector
{
    public static PdaExecutionProjectionRecord Project(ActiveTransportDataRecord request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var failure = Failure(request);
        // Raw RDS identifiers (taskRecordId, taskLabel, outOrderNo, AGV id, numeric
        // status) are intentionally not projected into the PDA contract; only the
        // friendly state, freshness and business failure wording cross the boundary.
        //
        // The raw TKG FailureMessage and the raw RDS EndedReason are not projected
        // either, for any role. Failure() below is an explicit allow-list that maps a
        // known failure code to a business code, category and operator wording; an
        // unrecognised code yields (null, null, null) rather than falling back to raw
        // text. A Supervisor needing the technical detail follows CorrelationId into
        // AdminWeb or the server audit log.
        return new PdaExecutionProjectionRecord(
            State(request),
            request.RdsTask?.LastSyncAt,
            failure.Code,
            failure.Category,
            failure.OperatorMessage,
            request.CorrelationId);
    }

    private static string State(ActiveTransportDataRecord request)
    {
        // TKG request status is the business source of truth once it is terminal.
        // Persisted RDS status refines only requests that remain active in TKG.
        if (request.Status == TransportRequestStatus.Completed)
        {
            return "COMPLETED";
        }

        if (request.Status == TransportRequestStatus.Failed)
        {
            return "FAILED";
        }

        if (request.Status == TransportRequestStatus.Cancelled)
        {
            return "TERMINATED";
        }

        if (request.RdsTask?.Status == 1003)
        {
            return "COMPLETED";
        }

        if (request.RdsTask?.Status is 1004 or 1005 or 1006)
        {
            return "FAILED";
        }

        if (request.RdsTask?.Status == 1001)
        {
            return "TERMINATED";
        }

        if (IsTrafficWaiting(request.FailureCode) || request.RdsTask?.Status == 1002)
        {
            return "WAITING";
        }

        return request.Status switch
        {
            TransportRequestStatus.Created or TransportRequestStatus.Validating => "REQUESTED",
            TransportRequestStatus.Queued => "QUEUED",
            TransportRequestStatus.SentToRds when request.RdsTask?.Status == 1000 && request.RdsTask.StartedAt.HasValue => "RUNNING",
            TransportRequestStatus.SentToRds when request.RdsTask?.Status == 1000 && !string.IsNullOrWhiteSpace(request.RdsTask.AgvId) => "ROBOT_ASSIGNED",
            TransportRequestStatus.SentToRds => "WAITING_FOR_ROBOT",
            TransportRequestStatus.Running => "RUNNING",
            _ => "REQUESTED"
        };
    }

    private static (string? Code, string? Category, string? OperatorMessage) Failure(ActiveTransportDataRecord request)
    {
        var code = request.FailureCode?.Trim();
        var normalized = code?.ToUpperInvariant();
        if (normalized is "60008" or "FAIL_LOCK_BIN")
        {
            return ("WORKSITE_BUSY", "WORKSITE", "Çalışma alanı başka bir görev tarafından kullanılıyor.");
        }

        if (normalized is "60009" or "NO_PATH")
        {
            return ("PATH_UNAVAILABLE", "NAVIGATION", "Robot için güvenli bir ulaşım yolu bulunamadı.");
        }

        if (normalized is "60011" or "TASK_FAILED")
        {
            return ("EXECUTION_FAILED", "EXECUTION", "Robot görevi tamamlanamadı. Yetkili desteğe başvurun.");
        }

        if (IsTrafficWaiting(normalized))
        {
            return ("TRAFFIC_WAITING", "TRAFFIC", "Robot trafiği nedeniyle görev bekliyor.");
        }

        if (request.Status == TransportRequestStatus.Failed || request.RdsTask?.Status is 1004 or 1005 or 1006)
        {
            return ("EXECUTION_FAILED", "EXECUTION", "Robot görevi tamamlanamadı. Yetkili desteğe başvurun.");
        }

        return (null, null, null);
    }

    private static bool IsTrafficWaiting(string? failureCode) =>
        failureCode?.Trim().ToUpperInvariant() is "54002" or "54003";
}
