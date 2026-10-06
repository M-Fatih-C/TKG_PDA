using System.Text.Json;

namespace TkgPda.Application;

public sealed record RdsWorkSiteSnapshot(
    string SiteId,
    string? Area,
    string? GroupName,
    int? Type,
    int? Filled,
    int? Locked,
    string? LockedBy,
    int? Disabled,
    int? SyncFailed,
    string? Content,
    string? AgvId,
    JsonElement RawState);

public sealed record RdsWindTaskQuery(
    int CurrentPage = 1,
    int PageSize = 20,
    string? TaskRecordId = null,
    string? TaskLabel = null,
    int? Status = null,
    string? AgvId = null);

public sealed record RdsWindTaskSnapshot(
    string? TaskRecordId,
    string? TaskLabel,
    int? Status,
    string? AgvId,
    string? OutOrderNo,
    JsonElement RawState,
    string? EndedReason = null);

public sealed record RdsWindTaskPage(
    int TotalCount,
    int CurrentPage,
    int PageSize,
    int TotalPage,
    IReadOnlyList<RdsWindTaskSnapshot> Tasks);

public sealed record RdsReadResult<T>(
    T Data,
    int BusinessCode,
    string BusinessMessage,
    string Endpoint,
    Guid CorrelationId,
    DateTimeOffset ReadAt);

// Customer Monitoring — Robot Alarm Visibility: flattened current alarm
// entries from GET /api/agv-report/core. Severity comes from the RDS bucket
// (fatals/errors/warnings/notices) the item was found in. An alarm is
// active only while it appears in the current snapshot; RDS exposes no
// explicit active/cleared flag or incident history for it.
public sealed record RdsRobotAlarmSnapshot(
    string RobotId,
    string Severity,
    string Code,
    string? Description,
    string? OccurredAt,
    int? OccurrenceCount);

// Per-robot state carried by the same GET /api/agv-report/core response,
// kept separate from alarm items since it describes the robot, not an alarm.
public sealed record RdsRobotStateSnapshot(
    string RobotId,
    string? Model,
    bool? Dispatchable,
    string? UndispatchableReason,
    double? BatteryLevel = null,
    bool? IsCharging = null,
    string? CurrentStation = null,
    string? LastStation = null,
    string? CurrentMap = null,
    /// <summary>
    /// Full SEER lock telemetry, carried verbatim so that Admin correlation can
    /// reuse the SAME <see cref="RdsCoreRobotLockInfo.CorrelatesWith"/> contract the
    /// execution recovery path uses, instead of maintaining a second, weaker
    /// definition of "TKG owns this lock". Null means the lock state could not be
    /// observed at all — which is UNKNOWN, not UNLOCKED.
    /// </summary>
    RdsCoreRobotLockInfo? LockInfo = null,
    string? ActiveOrderId = null,
    string? ActiveOrderState = null,
    /// <summary>
    /// Technical diagnostic telemetry reported directly by SEER robot (e.g. rbk / basic_info isLoaded).
    /// MUST NEVER be inferred or used to mutate TKG domain LoadState (EMPTY / FULL / UNKNOWN).
    /// Authoritative container load state is owned solely by TKG Inventory / Transport domains.
    /// </summary>
    bool? RdsReportedPayloadLoaded = null,
    /// <summary>Null when connectivity telemetry is absent — UNKNOWN, never assumed ONLINE.</summary>
    bool? Online = null,
    bool? Blocked = null,
    bool? Emergency = null,
    double? Vx = null,
    double? Vy = null,
    int? TaskStatus = null,
    /// <summary>Both SEER identity spaces. See <see cref="RdsRobotIdentity"/>.</summary>
    RdsRobotIdentity? Identity = null)
{
    /// <summary>Null when lock telemetry is absent (UNKNOWN), not false.</summary>
    public bool? IsLocked => LockInfo?.Locked;

    public string? LockNickName => LockInfo?.NickName;

    /// <summary>
    /// Identity for correlation. Falls back to treating <see cref="RobotId"/> as the
    /// vehicle-space identifier, which is what the readers now produce.
    /// </summary>
    public RdsRobotIdentity ResolvedIdentity => Identity ?? new RdsRobotIdentity(null, RobotId);
}

public sealed record RdsRobotAlarmReport(
    IReadOnlyList<RdsRobotAlarmSnapshot> Alarms,
    IReadOnlyList<RdsRobotStateSnapshot> Robots);


public interface IRdsReadClient
{
    bool Enabled { get; }
    string? SafeBaseAddress { get; }
    DateTimeOffset? LastSuccessfulRead { get; }

    Task<RdsReadResult<IReadOnlyList<RdsWorkSiteSnapshot>>> GetWorkSitesAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default);

    Task<RdsReadResult<RdsWindTaskPage>> QueryWindTasksAsync(
        RdsWindTaskQuery query,
        Guid correlationId,
        CancellationToken cancellationToken = default);

    Task<RdsReadResult<RdsRobotAlarmReport>> GetRobotAlarmsAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default);
}

public sealed class RdsReadException(
    string code,
    string message,
    string endpoint,
    Guid correlationId,
    int? businessCode = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public string Endpoint { get; } = endpoint;
    public Guid CorrelationId { get; } = correlationId;
    public int? BusinessCode { get; } = businessCode;
}

public sealed class RdsIntegrationRuleException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Admin/diagnostic RDS runtime status. Never served to the Operator PDA.
///
/// <see cref="DispatchEnabled"/> and <see cref="CoreBaseAddressDisplaySafe"/> exist
/// because a live commissioning test lost time to exactly this blind spot: RDSCore
/// was connected, the robot was online and dispatchable, the request reached QUEUED,
/// and nothing moved — because the running API had Rds:DispatchEnabled=false. The
/// status surface reported "Enabled" and "Reachable" and gave no way to see that the
/// execution gate was shut, or which RDSCore endpoint execution would target.
///
/// These are two separate facts and both matter:
///   Enabled           — RDS integration is active at all (reads/telemetry)
///   Reachable         — the read endpoint answered
///   DispatchEnabled   — TKG is currently permitted to send execution mutations
/// A closed dispatch gate is a normal, safe operating state, not a fault.
/// </summary>
public sealed record RdsConnectionStatusRecord(
    bool Enabled,
    bool Reachable,
    bool Stale,
    DateTimeOffset? LastSuccessfulRead,
    string? BaseAddressDisplaySafe,
    int? BusinessStatus,
    string? BusinessMessage,
    string? ErrorCode,
    string? ErrorMessage,
    bool DispatchEnabled = false,
    string? CoreBaseAddressDisplaySafe = null);

public sealed record RdsNavigationProfileRecord(
    Guid Id,
    Guid MachineId,
    string MachineCode,
    string Code,
    string Name,
    int Version,
    string Status,
    string? RobotMapName = null);

public sealed record RdsMirroredWorkSiteRecord(
    Guid Id,
    Guid NavigationProfileId,
    string SiteId,
    string? Area,
    string? GroupName,
    int? Type,
    int? Filled,
    int? Locked,
    int? Disabled,
    int? SyncFailed,
    string? Content,
    string? AgvId,
    DateTimeOffset? LastSyncAt,
    bool ExecutionReady);

public sealed record RdsWorkSiteFilter(
    Guid NavigationProfileId,
    string? SiteId = null,
    string? Area = null,
    string? GroupName = null,
    int? Type = null,
    int? Filled = null,
    int? Locked = null,
    int? Disabled = null,
    int? SyncFailed = null);

public sealed record RdsSlotMappingRecord(
    Guid SlotId,
    Guid LayoutId,
    string SlotCode,
    string SlotDisplayName,
    string SlotRole,
    Guid MachineId,
    Guid? RdsWorkSiteId,
    string? RdsSiteId,
    Guid? NavigationProfileId,
    int? RdsType,
    int? Filled,
    int? Locked,
    int? Disabled,
    int? SyncFailed,
    DateTimeOffset? LastSyncAt,
    bool ExecutionReady);

public sealed record RdsWorkSiteSyncRecord(
    Guid NavigationProfileId,
    int Received,
    int Created,
    int Updated,
    DateTimeOffset SyncedAt,
    int BusinessCode,
    string BusinessMessage);

public sealed record RdsRobotAlarmAdminRecord(
    string RobotId,
    string? RobotModel,
    string Severity,
    string Code,
    string? Description,
    int? OccurrenceCount,
    string? OccurredAt,
    bool? Dispatchable,
    string? UndispatchableReason);

/// <summary>
/// A TKG map-preparation lock that TKG recorded and has not recorded a release for.
/// <see cref="CapturedLockInfo"/> is the verbatim SEER fingerprint observed at the
/// moment TKG acquired the lock; correlation reuses
/// <see cref="RdsCoreRobotLockInfo.CorrelatesWith"/> so Admin cannot hold a second,
/// weaker definition of ownership. A null fingerprint can never correlate.
/// </summary>
public sealed record RdsActiveLockFingerprint(
    string? Vehicle,
    string? RequiredMap,
    RdsCoreRobotLockInfo? CapturedLockInfo);

/// <summary>
/// Per-severity alarm counts. SEER severity buckets are preserved rather than
/// collapsed, so a benign <c>notice</c> can never be presented as an error.
/// </summary>
public sealed record RdsRobotAlarmCounts(int Fatal, int Error, int Warning, int Notice)
{
    public static readonly RdsRobotAlarmCounts Empty = new(0, 0, 0, 0);

    public int Total => Fatal + Error + Warning + Notice;

    /// <summary>Counts that justify a danger indicator. Warnings and notices do not.</summary>
    public int Critical => Fatal + Error;
}

public sealed record RdsRobotAdminRecord(
    string RobotId,
    string? Model,
    /// <summary>SEER internal robot identity, when the payload carries it.</summary>
    string? Uuid,
    /// <summary>RDS dispatch vehicle identifier, when the payload carries it.</summary>
    string? VehicleId,
    // 5 orthogonal dimensions. Each may be UNKNOWN when telemetry cannot prove it.
    string Connectivity,
    string Dispatch,
    string? UndispatchableReason,
    string Motion,
    string Execution,
    string Control,
    // Lock details. Null means the lock state could not be observed (UNKNOWN).
    bool? IsLocked,
    string? LockNickName,
    // Telemetry & diagnostics
    double? BatteryLevel,
    bool? IsCharging,
    string? CurrentStation,
    string? LastStation,
    string? CurrentMap,
    string? ActiveOrderId,
    string? ActiveOrderState,
    /// <summary>
    /// Technical diagnostic telemetry reported directly by SEER robot.
    /// Strictly isolated from TKG domain LoadState.
    /// </summary>
    bool? RdsReportedPayloadLoaded,
    RdsRobotAlarmCounts Alarms,
    bool? Online,
    bool? Dispatchable);

public sealed record RdsRobotAlarmOperatorSummary(
    bool HasActiveRobotAlarm,
    string? HighestSeverity,
    int ActiveAlarmCount);

/// <summary>
/// Operator-safe robot view. The API — not the UI — enforces this boundary: no SEER
/// uuid, no raw vehicle_id, no map, no WorkSite or station code, no pose, no RDS
/// order or block identifier and no lock fingerprint is serialized to a Line or
/// Warehouse Operator. Only the verified friendly model name, connectivity, battery
/// and a business-level fault flag are exposed, which is exactly what the Operator
/// robot status bar renders.
/// </summary>
public sealed record OperatorRobotSummaryRecord(
    string? DisplayName,
    bool? Online,
    double? BatteryLevel,
    bool HasActiveAlarm);

public interface IRdsIntegrationStore
{
    Task<IReadOnlyList<RdsNavigationProfileRecord>> ListNavigationProfilesAsync(CancellationToken cancellationToken = default);
    Task<(int Created, int Updated)> SyncWorkSitesAsync(Guid navigationProfileId, IReadOnlyList<RdsWorkSiteSnapshot> workSites, DateTimeOffset syncedAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RdsMirroredWorkSiteRecord>> ListWorkSitesAsync(RdsWorkSiteFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RdsSlotMappingRecord>> ListSlotMappingsAsync(Guid? layoutId, CancellationToken cancellationToken = default);
    Task<RdsSlotMappingRecord> SetSlotMappingAsync(Guid slotId, Guid rdsWorkSiteId, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task RemoveSlotMappingAsync(Guid slotId, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RdsActiveLockFingerprint>> GetActiveTkgLockFingerprintsAsync(CancellationToken cancellationToken = default);

    Task ObserveAlarmSnapshotAsync(
        IReadOnlyList<RdsRobotAlarmSnapshot> alarms,
        Guid correlationId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task ObserveAlarmReadFailureAsync(
        Guid correlationId,
        DateTimeOffset observedAt,
        string errorCode,
        string message,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task<IReadOnlyList<RobotAlarmObservationRecord>> ListAlarmHistoryAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RobotAlarmObservationRecord>>([]);
}


public interface IRdsWorkSiteReconciler
{
    Task<RdsWorkSiteSyncRecord> SyncWorkSitesAsync(
        Guid navigationProfileId,
        Guid correlationId,
        CancellationToken cancellationToken = default);
}

public sealed class RdsIntegrationService(IRdsReadClient client, IRdsIntegrationStore store, TimeProvider timeProvider) : IRdsWorkSiteReconciler
{
    private readonly SemaphoreSlim syncGate = new(1, 1);

    public async Task<RdsConnectionStatusRecord> GetStatusAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        if (!client.Enabled)
        {
            return new(false, false, true, client.LastSuccessfulRead, client.SafeBaseAddress, null, null, "RDS_DISABLED", "RDS read integration is disabled.");
        }

        try
        {
            var result = await client.GetWorkSitesAsync(correlationId, cancellationToken);
            return new(true, true, false, result.ReadAt, client.SafeBaseAddress, result.BusinessCode, result.BusinessMessage, null, null);
        }
        catch (RdsReadException error)
        {
            return new(true, false, true, client.LastSuccessfulRead, client.SafeBaseAddress, error.BusinessCode, error.Message, error.Code, "RDS read service is unavailable; last persisted evidence was retained.");
        }
    }

    public Task<IReadOnlyList<RdsNavigationProfileRecord>> ListNavigationProfilesAsync(CancellationToken cancellationToken = default) =>
        store.ListNavigationProfilesAsync(cancellationToken);

    public Task<IReadOnlyList<RdsMirroredWorkSiteRecord>> ListWorkSitesAsync(RdsWorkSiteFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.NavigationProfileId == Guid.Empty)
        {
            throw new RdsIntegrationRuleException("NAVIGATION_PROFILE_REQUIRED", "An explicit navigation profile is required.");
        }

        return store.ListWorkSitesAsync(filter, cancellationToken);
    }

    public Task<IReadOnlyList<RdsSlotMappingRecord>> ListSlotMappingsAsync(Guid? layoutId, CancellationToken cancellationToken = default) =>
        store.ListSlotMappingsAsync(layoutId, cancellationToken);

    public async Task<RdsWorkSiteSyncRecord> SyncWorkSitesAsync(Guid navigationProfileId, Guid correlationId, CancellationToken cancellationToken = default)
    {
        if (navigationProfileId == Guid.Empty)
        {
            throw new RdsIntegrationRuleException("NAVIGATION_PROFILE_REQUIRED", "An explicit navigation profile is required.");
        }

        if (!client.Enabled)
        {
            throw new RdsReadException("RDS_DISABLED", "RDS read integration is disabled.", "/api/work-sites/sites", correlationId);
        }

        if (!await syncGate.WaitAsync(0, cancellationToken))
        {
            throw new RdsIntegrationRuleException("RDS_SYNC_IN_PROGRESS", "A WorkSite synchronization is already running.");
        }

        try
        {
            // Read RDS completely before opening the TKG transaction. A failed read never erases the mirror.
            var result = await client.GetWorkSitesAsync(correlationId, cancellationToken);
            var syncedAt = timeProvider.GetUtcNow();
            var counts = await store.SyncWorkSitesAsync(navigationProfileId, result.Data, syncedAt, cancellationToken);
            return new(navigationProfileId, result.Data.Count, counts.Created, counts.Updated, syncedAt, result.BusinessCode, result.BusinessMessage);
        }
        finally
        {
            syncGate.Release();
        }
    }

    public Task<RdsSlotMappingRecord> SetSlotMappingAsync(Guid slotId, Guid rdsWorkSiteId, AdminMutationContext context, CancellationToken cancellationToken = default)
    {
        if (slotId == Guid.Empty || rdsWorkSiteId == Guid.Empty)
        {
            throw new RdsIntegrationRuleException("ID_REQUIRED", "Slot and RDS WorkSite identifiers are required.");
        }

        return store.SetSlotMappingAsync(slotId, rdsWorkSiteId, context, cancellationToken);
    }

    public Task RemoveSlotMappingAsync(Guid slotId, AdminMutationContext context, CancellationToken cancellationToken = default)
    {
        if (slotId == Guid.Empty)
        {
            throw new RdsIntegrationRuleException("ID_REQUIRED", "Slot identifier is required.");
        }

        return store.RemoveSlotMappingAsync(slotId, context, cancellationToken);
    }

    // Customer Monitoring — Robot Alarm Visibility: Admin sees the full
    // current-alarm detail; robot state (model/dispatchable/reason) is
    // joined in from the same read since it is per-robot, not per-alarm.
    public async Task<IReadOnlyList<RdsRobotAlarmAdminRecord>> ListRobotAlarmsForAdminAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        var result = await ReadAlarmSnapshotAsync(correlationId, cancellationToken);
        var robotsById = result.Data.Robots.ToDictionary(robot => robot.RobotId, StringComparer.Ordinal);
        return result.Data.Alarms
            .Select(alarm =>
            {
                robotsById.TryGetValue(alarm.RobotId, out var robot);
                return new RdsRobotAlarmAdminRecord(
                    alarm.RobotId,
                    robot?.Model,
                    alarm.Severity,
                    alarm.Code,
                    alarm.Description,
                    alarm.OccurrenceCount,
                    alarm.OccurredAt,
                    robot?.Dispatchable,
                    robot?.UndispatchableReason);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<RdsRobotAdminRecord>> ListRobotsForAdminAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        var result = await ReadAlarmSnapshotAsync(correlationId, cancellationToken);
        var alarmsByRobot = result.Data.Alarms
            .GroupBy(a => a.RobotId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, CountBySeverity, StringComparer.Ordinal);

        IReadOnlyList<RdsActiveLockFingerprint> activeTkgLocks;
        try
        {
            activeTkgLocks = await store.GetActiveTkgLockFingerprintsAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A fingerprint read failure must never upgrade control confidence: with
            // no fingerprints nothing can correlate, so locks read EXTERNAL_OR_OTHER.
            // Cancellation is deliberately excluded so a cancelled Admin poll
            // propagates instead of being reported as a successful empty read.
            activeTkgLocks = [];
        }

        return result.Data.Robots
            .Select(robot => ProjectRobotForAdmin(robot, alarmsByRobot, activeTkgLocks))
            .OrderBy(r => r.RobotId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Operator projection. Deliberately built from the same single upstream read as
    /// the Admin projection, and deliberately narrow: see
    /// <see cref="OperatorRobotSummaryRecord"/> for the fields the boundary permits.
    /// </summary>
    public async Task<IReadOnlyList<OperatorRobotSummaryRecord>> ListRobotsForOperatorAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        var result = await ReadAlarmSnapshotAsync(correlationId, cancellationToken);
        var alarmsByRobot = result.Data.Alarms
            .GroupBy(a => a.RobotId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, CountBySeverity, StringComparer.Ordinal);

        return result.Data.Robots
            // Ordering key is internal and is never serialized.
            .OrderBy(robot => robot.RobotId, StringComparer.OrdinalIgnoreCase)
            .Select(robot =>
            {
                var alarms = alarmsByRobot.TryGetValue(robot.RobotId, out var counts) ? counts : RdsRobotAlarmCounts.Empty;
                return new OperatorRobotSummaryRecord(
                    robot.Model,
                    robot.Online,
                    robot.BatteryLevel,
                    // Only fatal/error is a fault for the operator; notices and
                    // warnings must not raise a robot-fault indicator.
                    alarms.Critical > 0);
            })
            .ToList();
    }

    private async Task<RdsReadResult<RdsRobotAlarmReport>> ReadAlarmSnapshotAsync(
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.GetRobotAlarmsAsync(correlationId, cancellationToken);
            try
            {
                await store.ObserveAlarmSnapshotAsync(result.Data.Alarms, correlationId, result.ReadAt, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A history-write failure must not hide the current snapshot or clear alarms.
            }

            return result;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            try
            {
                await store.ObserveAlarmReadFailureAsync(
                    correlationId,
                    timeProvider.GetUtcNow(),
                    error.GetType().Name,
                    error.Message,
                    cancellationToken);
            }
            catch (Exception persistError) when (persistError is not OperationCanceledException)
            {
            }

            throw;
        }
    }

    /// <summary>Velocity below this magnitude is treated as stationary.</summary>
    private const double MotionVelocityEpsilon = 0.001;

    private static RdsRobotAlarmCounts CountBySeverity(IEnumerable<RdsRobotAlarmSnapshot> alarms)
    {
        int fatal = 0, error = 0, warning = 0, notice = 0;
        foreach (var alarm in alarms)
        {
            switch (alarm.Severity?.Trim().ToLowerInvariant())
            {
                case "fatal": fatal++; break;
                case "error": error++; break;
                case "warning": warning++; break;
                case "notice": notice++; break;
            }
        }
        return new(fatal, error, warning, notice);
    }

    private static RdsRobotAdminRecord ProjectRobotForAdmin(
        RdsRobotStateSnapshot robot,
        IReadOnlyDictionary<string, RdsRobotAlarmCounts> alarmsByRobot,
        IReadOnlyList<RdsActiveLockFingerprint> activeTkgLocks)
    {
        var alarms = alarmsByRobot.TryGetValue(robot.RobotId, out var counts) ? counts : RdsRobotAlarmCounts.Empty;
        var identity = robot.ResolvedIdentity;

        // Dimension 1 — Connectivity. Absent evidence is UNKNOWN, never ONLINE.
        var connectivity = robot.Online switch
        {
            true => "ONLINE",
            false => "OFFLINE",
            null => "UNKNOWN"
        };

        // Dimension 2 — Dispatch. Absent evidence is UNKNOWN, not UNDISPATCHABLE.
        var dispatch = robot.Dispatchable switch
        {
            true => "DISPATCHABLE",
            false => "UNDISPATCHABLE",
            null => "UNKNOWN"
        };

        // Dimension 3 — Motion, derived only from direct SEER evidence.
        // rbk_report.task_status is deliberately NOT consulted: this repository has
        // no authoritative mapping for its numeric values, and guessing would
        // manufacture motion states. Unproven motion stays UNKNOWN.
        string motion;
        if (robot.Online == false)
        {
            motion = "UNKNOWN";
        }
        else if (robot.Blocked == true)
        {
            motion = "BLOCKED";
        }
        else if (robot.Emergency == true)
        {
            motion = "PAUSED";
        }
        else if (robot.Vx is { } vx && robot.Vy is { } vy)
        {
            motion = Math.Abs(vx) > MotionVelocityEpsilon || Math.Abs(vy) > MotionVelocityEpsilon
                ? "MOVING"
                : "IDLE";
        }
        else
        {
            motion = "UNKNOWN";
        }

        // Dimension 4 — Execution.
        string execution;
        if (string.IsNullOrWhiteSpace(robot.ActiveOrderId))
        {
            execution = "IDLE";
        }
        else if (string.IsNullOrWhiteSpace(robot.ActiveOrderState))
        {
            execution = "UNKNOWN";
        }
        else
        {
            execution = robot.ActiveOrderState.Trim().ToUpperInvariant() switch
            {
                "RUNNING" => "RUNNING",
                "WAITING" or "QUEUED" => "WAITING",
                "SUSPENDED" or "PAUSED" => "SUSPENDED",
                "FINISHED" or "COMPLETED" => "FINISHED",
                "FAILED" or "ERROR" or "TERMINATED" or "ABORTED" or "STOPPED" => "ERROR",
                _ => "UNKNOWN"
            };
        }

        // Dimension 5 — Control. Fails closed: only a captured TKG fingerprint that
        // still correlates with the CURRENT lock, under the same contract the
        // execution recovery path uses, may be labelled TKG_CORRELATED.
        string control;
        if (robot.LockInfo is null)
        {
            // No lock telemetry at all — the lock state itself is unproven.
            control = "UNKNOWN";
        }
        else if (!robot.LockInfo.Locked)
        {
            control = "UNLOCKED";
        }
        else
        {
            var correlated = activeTkgLocks.Any(fingerprint =>
                identity.Matches(fingerprint.Vehicle)
                && fingerprint.CapturedLockInfo is not null
                && fingerprint.CapturedLockInfo.CorrelatesWith(robot.LockInfo));

            control = correlated ? "TKG_CORRELATED" : "EXTERNAL_OR_OTHER";
        }

        return new RdsRobotAdminRecord(
            robot.RobotId,
            robot.Model,
            identity.Uuid,
            identity.VehicleId,
            connectivity,
            dispatch,
            robot.UndispatchableReason,
            motion,
            execution,
            control,
            robot.IsLocked,
            robot.LockNickName,
            robot.BatteryLevel,
            robot.IsCharging,
            robot.CurrentStation,
            robot.LastStation,
            robot.CurrentMap,
            robot.ActiveOrderId,
            robot.ActiveOrderState,
            robot.RdsReportedPayloadLoaded,
            alarms,
            robot.Online,
            robot.Dispatchable);
    }


    // Coarse Operator projection: no verified TransportRequest -> assigned
    // robot mapping exists yet (RdsTaskProjection.AgvId is not confirmed to
    // share the agv-report uuid/vehicle_id identity space, and no store
    // query scopes active tasks by machine), so per-machine filtering is
    // deliberately not attempted here to avoid inventing that relationship.
    public async Task<RdsRobotAlarmOperatorSummary> GetRobotAlarmSummaryForOperatorAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        var result = await ReadAlarmSnapshotAsync(correlationId, cancellationToken);
        var alarms = result.Data.Alarms;
        var highestSeverity = alarms.Count == 0
            ? null
            : alarms
                .Select(alarm => alarm.Severity)
                .OrderByDescending(severity => Array.IndexOf(SeverityRank, severity))
                .First();
        return new RdsRobotAlarmOperatorSummary(alarms.Count > 0, highestSeverity, alarms.Count);
    }

    private static readonly string[] SeverityRank = ["notice", "warning", "error", "fatal"];
}
