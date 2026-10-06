using TkgPda.Domain;
using Xunit;

namespace TkgPda.Domain.Tests;

public sealed class TransportDurationMetricsTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-06T08:00:00Z");

    [Fact]
    public void Complete_evidence_separates_total_warehouse_and_robot()
    {
        var result = TransportDurationMetrics.Calculate(
        [
            (TransportEventTypes.RequestCreated, T0),
            (TransportEventTypes.WarehousePreparationReady, T0.AddMinutes(4)),
            (TransportEventTypes.RobotTaskDispatched, T0.AddMinutes(5)),
            (TransportEventTypes.RobotTaskStarted, T0.AddMinutes(6)),
            (TransportEventTypes.RobotTaskCompleted, T0.AddMinutes(9)),
            (TransportEventTypes.RequestCompleted, T0.AddMinutes(10))
        ]);

        Assert.Equal(TimeSpan.FromMinutes(10), result.Total);
        Assert.Equal(TimeSpan.FromMinutes(4), result.Warehouse);
        Assert.Equal(TimeSpan.FromMinutes(3), result.Robot);
    }

    [Fact]
    public void Missing_robot_start_leaves_robot_duration_unavailable()
    {
        var result = TransportDurationMetrics.Calculate(
        [
            (TransportEventTypes.RequestCreated, T0),
            (TransportEventTypes.RobotTaskDispatched, T0.AddMinutes(1)),
            (TransportEventTypes.RobotTaskCompleted, T0.AddMinutes(8)),
            (TransportEventTypes.RequestCompleted, T0.AddMinutes(8))
        ]);

        Assert.Equal(TimeSpan.FromMinutes(8), result.Total);
        Assert.Null(result.Warehouse);
        Assert.Null(result.Robot);
    }
}
