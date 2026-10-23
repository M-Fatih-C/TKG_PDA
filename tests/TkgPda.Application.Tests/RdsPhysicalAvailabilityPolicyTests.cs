using TkgPda.Application;
using Xunit;

namespace TkgPda.Application.Tests;

public sealed class RdsPhysicalAvailabilityPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-09T12:00:00Z");
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private readonly RdsPhysicalAvailabilityPolicy policy = new(TimeSpan.FromSeconds(300), new Clock());

    [Theory]
    [InlineData(1, 0, 0, 0, 0, 0, "AVAILABLE")]
    [InlineData(1, 1, 0, 0, 0, 0, "OCCUPIED")]
    [InlineData(1, null, 0, 0, 0, 0, "UNKNOWN")]
    [InlineData(1, 2, 0, 0, 0, 0, "UNKNOWN")]
    [InlineData(1, -1, 0, 0, 0, 0, "UNKNOWN")]
    [InlineData(1, 0, null, 0, 0, 0, "UNKNOWN")]
    [InlineData(1, 0, 2, 0, 0, 0, "UNKNOWN")]
    [InlineData(1, 0, 1, 0, 0, 0, "LOCKED")]
    [InlineData(1, 0, 0, 1, 0, 0, "DISABLED")]
    [InlineData(1, 0, 0, null, 0, 0, "UNKNOWN")]
    [InlineData(1, 0, 0, 0, 1, 0, "SYNC_FAILED")]
    [InlineData(1, 0, 0, 0, null, 0, "UNKNOWN")]
    [InlineData(1, 0, 0, 0, 0, null, "NEVER_SYNCED")]
    [InlineData(1, 0, 0, 0, 0, 301, "STALE")]
    [InlineData(1, 0, 0, 0, 0, 300, "AVAILABLE")]
    [InlineData(0, 0, 0, 0, 0, 0, "NOT_PHYSICAL")]
    [InlineData(null, 0, 0, 0, 0, 0, "NOT_PHYSICAL")]
    public void Destination_requires_complete_source_proven_physical_evidence(int? type, int? filled,
        int? locked, int? disabled, int? syncFailed, int? age, string expected)
    {
        var evidence = new RdsPhysicalEvidence(type, filled, locked, disabled, syncFailed,
            age.HasValue ? Now.AddSeconds(-age.Value) : null);
        Assert.Equal(expected, policy.State(evidence));
        Assert.Equal(expected == "AVAILABLE", policy.IsDestinationAvailable(evidence));
    }

    [Theory]
    [InlineData("VACANT", 0, 0, "CONSISTENT")]
    [InlineData("OCCUPIED", 1, 0, "CONSISTENT")]
    [InlineData("VACANT", 1, 0, "PHYSICAL_STATE_MISMATCH")]
    [InlineData("OCCUPIED", 0, 0, "PHYSICAL_STATE_MISMATCH")]
    [InlineData("UNKNOWN", 1, 0, "PHYSICAL_STATE_UNKNOWN")]
    [InlineData("VACANT", null, 0, "PHYSICAL_STATE_UNKNOWN")]
    [InlineData("VACANT", 0, 301, "PHYSICAL_STATE_UNKNOWN")]
    public void Reconciliation_reports_evidence_without_mutating_business_state(string occupancy,
        int? filled, int age, string expected) => Assert.Equal(expected,
            policy.Consistency(occupancy, new(1, filled, 0, 0, 0, Now.AddSeconds(-age))));

    [Fact]
    public void Only_current_valid_other_profile_evidence_can_veto_empty_destination()
    {
        var occupied = new RdsPhysicalEvidence(1, 1, 0, 0, 0, Now);
        Assert.True(policy.ContradictsEmptyDestination(occupied));
        Assert.True(policy.ContradictsEmptyDestination(occupied with { Filled = 0, Locked = 1 }));
        Assert.False(policy.ContradictsEmptyDestination(occupied with { LastSyncAt = Now.AddSeconds(-301) }));
        Assert.False(policy.ContradictsEmptyDestination(occupied with { SyncFailed = 1 }));
        Assert.False(policy.ContradictsEmptyDestination(occupied with { Type = 0 }));
    }
}
