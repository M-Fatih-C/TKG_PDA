namespace TkgPda.Application;

/// <summary>
/// One TKG observation of an alarm that appeared in a successful RDS snapshot.
/// This is not an RDS incident history and it never changes occupancy or load.
/// </summary>
public sealed record RobotAlarmObservationRecord(
    Guid Id,
    string Fingerprint,
    string RobotId,
    string Severity,
    string AlarmCode,
    string? Description,
    string? OccurredAtRaw,
    int? OccurrenceCount,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ClearedAt,
    Guid LastCorrelationId);

public sealed record RobotAlarmObservationPlan(
    IReadOnlyList<RobotAlarmObservationRecord> Upserts,
    IReadOnlyList<Guid> ClearIds);

public static class RobotAlarmObservationRules
{
    public static string Fingerprint(RdsRobotAlarmSnapshot alarm) =>
        string.Join(
            '\u001f',
            alarm.RobotId.Trim(),
            alarm.Severity.Trim(),
            alarm.Code.Trim(),
            alarm.Description?.Trim() ?? "",
            alarm.OccurredAt?.Trim() ?? "");

    public static RobotAlarmObservationPlan Reconcile(
        IReadOnlyList<RobotAlarmObservationRecord> openObservations,
        IReadOnlyList<RdsRobotAlarmSnapshot> snapshot,
        DateTimeOffset observedAt,
        Guid correlationId)
    {
        var openByFingerprint = openObservations
            .Where(item => item.ClearedAt is null)
            .GroupBy(item => item.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var upserts = new List<RobotAlarmObservationRecord>();
        foreach (var alarm in snapshot)
        {
            var fingerprint = Fingerprint(alarm);
            if (!seen.Add(fingerprint))
            {
                continue;
            }

            if (openByFingerprint.TryGetValue(fingerprint, out var existing))
            {
                upserts.Add(existing with
                {
                    OccurrenceCount = alarm.OccurrenceCount,
                    LastSeenAt = observedAt,
                    LastCorrelationId = correlationId
                });
            }
            else
            {
                upserts.Add(new RobotAlarmObservationRecord(
                    Guid.NewGuid(),
                    fingerprint,
                    alarm.RobotId.Trim(),
                    alarm.Severity.Trim(),
                    alarm.Code.Trim(),
                    string.IsNullOrWhiteSpace(alarm.Description) ? null : alarm.Description.Trim(),
                    string.IsNullOrWhiteSpace(alarm.OccurredAt) ? null : alarm.OccurredAt.Trim(),
                    alarm.OccurrenceCount,
                    observedAt,
                    observedAt,
                    null,
                    correlationId));
            }
        }

        var clearIds = openByFingerprint.Values
            .Where(item => !seen.Contains(item.Fingerprint))
            .Select(item => item.Id)
            .ToArray();
        return new RobotAlarmObservationPlan(upserts, clearIds);
    }
}
