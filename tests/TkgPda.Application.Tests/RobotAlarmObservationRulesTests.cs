using TkgPda.Application;
using Xunit;

namespace TkgPda.Application.Tests;

public sealed class RobotAlarmObservationRulesTests
{
    private static readonly DateTimeOffset FirstSeen = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = FirstSeen.AddMinutes(5);

    [Fact]
    public void First_snapshot_creates_one_observation_per_distinct_alarm()
    {
        var plan = RobotAlarmObservationRules.Reconcile([], [Alarm("fatal", 1), Alarm("error", 1)], FirstSeen, Guid.NewGuid());

        Assert.Equal(2, plan.Upserts.Count);
        Assert.Empty(plan.ClearIds);
        Assert.NotEqual(plan.Upserts[0].Fingerprint, plan.Upserts[1].Fingerprint);
    }

    [Fact]
    public void The_same_snapshot_updates_the_open_observation()
    {
        var correlation = Guid.NewGuid();
        var created = RobotAlarmObservationRules.Reconcile([], [Alarm("error", 1)], FirstSeen, correlation).Upserts[0];
        var again = RobotAlarmObservationRules.Reconcile([created], [Alarm("error", 4)], Later, Guid.NewGuid());

        var updated = Assert.Single(again.Upserts);
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(created.Fingerprint, updated.Fingerprint);
        Assert.Equal(4, updated.OccurrenceCount);
        Assert.Equal(Later, updated.LastSeenAt);
        Assert.Equal(created.FirstSeenAt, updated.FirstSeenAt);
        Assert.Empty(again.ClearIds);
    }

    [Fact]
    public void A_successful_snapshot_without_the_alarm_clears_it()
    {
        var created = RobotAlarmObservationRules.Reconcile([], [Alarm("warning", 2)], FirstSeen, Guid.NewGuid()).Upserts[0];
        var cleared = RobotAlarmObservationRules.Reconcile([created], [], Later, Guid.NewGuid());

        Assert.Empty(cleared.Upserts);
        Assert.Equal(created.Id, Assert.Single(cleared.ClearIds));
    }

    [Fact]
    public void Severities_stay_distinct()
    {
        var plan = RobotAlarmObservationRules.Reconcile(
            [],
            [Alarm("error", 1), Alarm("fatal", 1)],
            FirstSeen,
            Guid.NewGuid());

        Assert.Equal(2, plan.Upserts.Select(item => item.Fingerprint).Distinct().Count());
    }

    private static RdsRobotAlarmSnapshot Alarm(string severity, int times) =>
        new("robot-1", severity, "54001", "observed", "2026-10-06T08:00:00Z", times);
}
