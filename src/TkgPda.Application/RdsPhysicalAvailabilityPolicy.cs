namespace TkgPda.Application;

/// <summary>SEER RdsApi_en: filled is occupancy, never material load or identity.</summary>
public sealed record RdsPhysicalEvidence(int? Type, int? Filled, int? Locked,
    int? Disabled, int? SyncFailed, DateTimeOffset? LastSyncAt);

public sealed class RdsPhysicalAvailabilityPolicy(TimeSpan? freshness = null, TimeProvider? clock = null)
{
    // Same default as Rds:WorkSiteFreshnessSeconds; production receives that setting.
    public const int DefaultFreshnessSeconds = 300;
    private readonly TimeSpan freshness = freshness ?? TimeSpan.FromSeconds(DefaultFreshnessSeconds);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public string State(RdsPhysicalEvidence evidence)
    {
        if (evidence.Type != 1) return "NOT_PHYSICAL";
        if (evidence.LastSyncAt is null) return "NEVER_SYNCED";
        if (clock.GetUtcNow() - evidence.LastSyncAt > freshness) return "STALE";
        if (evidence.SyncFailed == 1) return "SYNC_FAILED";
        if (evidence.Disabled == 1) return "DISABLED";
        if (evidence.Locked == 1) return "LOCKED";
        if (evidence.SyncFailed != 0 || evidence.Disabled != 0 || evidence.Locked != 0
            || evidence.Filled is not (0 or 1)) return "UNKNOWN";
        return evidence.Filled == 0 ? "AVAILABLE" : "OCCUPIED";
    }

    public bool IsDestinationAvailable(RdsPhysicalEvidence evidence) => State(evidence) == "AVAILABLE";

    // Another NAV is corroborating/veto evidence, never an alternative binding.
    public bool ContradictsEmptyDestination(RdsPhysicalEvidence evidence) =>
        evidence.Type == 1 && evidence.LastSyncAt is { } synced
        && clock.GetUtcNow() - synced <= freshness && evidence.SyncFailed == 0
        && (evidence.Filled == 1 || evidence.Locked == 1);

    public string Consistency(string occupancy, RdsPhysicalEvidence evidence)
    {
        if (evidence.Type != 1 || evidence.LastSyncAt is not { } synced
            || clock.GetUtcNow() - synced > freshness || evidence.SyncFailed != 0
            || evidence.Filled is not (0 or 1)) return "PHYSICAL_STATE_UNKNOWN";
        return occupancy switch
        {
            "VACANT" => evidence.Filled == 0 ? "CONSISTENT" : "PHYSICAL_STATE_MISMATCH",
            "OCCUPIED" => evidence.Filled == 1 ? "CONSISTENT" : "PHYSICAL_STATE_MISMATCH",
            _ => "PHYSICAL_STATE_UNKNOWN"
        };
    }

    public static string? ErrorCode(string state) => state switch
    {
        "AVAILABLE" or "OCCUPIED" => null,
        "NOT_PHYSICAL" => "RDS_WORKSITE_NOT_PHYSICAL",
        "NEVER_SYNCED" or "STALE" => "RDS_WORKSITE_STALE",
        "DISABLED" => "RDS_WORKSITE_DISABLED",
        "LOCKED" => "RDS_WORKSITE_LOCKED",
        "SYNC_FAILED" => "RDS_WORKSITE_SYNC_FAILED",
        _ => "RDS_WORKSITE_STATE_UNKNOWN"
    };
}
