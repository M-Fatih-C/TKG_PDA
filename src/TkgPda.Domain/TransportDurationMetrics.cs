namespace TkgPda.Domain;

/// <summary>
/// Durations are measured only between the canonical lifecycle events.
/// A missing boundary stays unavailable. Dispatch is not robot start.
/// </summary>
public readonly record struct TransportDurationResult(TimeSpan? Total, TimeSpan? Warehouse, TimeSpan? Robot);

public static class TransportDurationMetrics
{
    public static TransportDurationResult Calculate(IEnumerable<(string EventType, DateTimeOffset OccurredAt)> events)
    {
        var ordered = events.OrderBy(item => item.OccurredAt).ToArray();
        var created = First(ordered, TransportEventTypes.RequestCreated);
        var ready = First(ordered, TransportEventTypes.WarehousePreparationReady);
        var started = First(ordered, TransportEventTypes.RobotTaskStarted);
        var robotCompleted = First(ordered, TransportEventTypes.RobotTaskCompleted);
        var completed = First(ordered, TransportEventTypes.RequestCompleted);
        return new TransportDurationResult(
            Between(created, completed),
            Between(created, ready),
            started is null ? null : Between(started, robotCompleted));
    }

    private static DateTimeOffset? First((string EventType, DateTimeOffset OccurredAt)[] events, string eventType)
    {
        foreach (var item in events)
        {
            if (item.EventType == eventType) return item.OccurredAt;
        }

        return null;
    }

    private static TimeSpan? Between(DateTimeOffset? start, DateTimeOffset? end) =>
        start is null || end is null || end < start ? null : end.Value - start.Value;
}
