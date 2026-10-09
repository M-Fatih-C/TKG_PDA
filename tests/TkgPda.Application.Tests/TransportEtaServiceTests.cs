using System.Text.Json;
using TkgPda.Domain;
using Xunit;

namespace TkgPda.Application.Tests;

public sealed class TransportEtaServiceTests
{
    private readonly TransportEtaService service = new();

    [Fact]
    public void Terminal_requests_return_unavailable_eta()
    {
        var terminalStatuses = new[]
        {
            TransportRequestStatus.Completed,
            TransportRequestStatus.Failed,
            TransportRequestStatus.Cancelled
        };

        foreach (var status in terminalStatuses)
        {
            var projection = service.Project(
                status,
                rdsStatus: null,
                agvId: "sim_01",
                startedAt: DateTimeOffset.UtcNow,
                queuePosition: 1);

            Assert.Equal("UNAVAILABLE", projection.State);
            Assert.Null(projection.Seconds);
        }
    }

    [Theory]
    [InlineData(1001)] // Stopped
    [InlineData(1003)] // Success
    [InlineData(1004)] // Failed
    [InlineData(1005)] // Exception
    [InlineData(1006)] // Cancelled
    public void Terminal_rds_statuses_return_unavailable_eta(int rdsStatus)
    {
        var projection = service.Project(
            TransportRequestStatus.Running,
            rdsStatus,
            agvId: "sim_01",
            startedAt: DateTimeOffset.UtcNow,
            queuePosition: 1);

        Assert.Equal("UNAVAILABLE", projection.State);
        Assert.Null(projection.Seconds);
    }

    [Fact]
    public void Explicit_remaining_time_in_last_status_json_is_used()
    {
        using var doc = JsonDocument.Parse("""{"remaining_time": 47.8}""");

        var projection = service.Project(
            TransportRequestStatus.Running,
            rdsStatus: 1000,
            agvId: "sim_01",
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            queuePosition: 1,
            lastStatusJson: doc.RootElement);

        Assert.Equal("AVAILABLE", projection.State);
        Assert.Equal(48, projection.Seconds);
    }

    [Fact]
    public void Running_request_without_telemetry_does_not_guess_from_started_at()
    {
        var now = DateTimeOffset.UtcNow;
        var startedAt = now.AddSeconds(-40); // 40 seconds elapsed

        var projection = service.Project(
            TransportRequestStatus.Running,
            rdsStatus: 1000,
            agvId: "sim_01",
            startedAt: startedAt,
            queuePosition: 1,
            now: now);

        Assert.Equal("UNAVAILABLE", projection.State);
        Assert.Null(projection.Seconds);
    }

    [Fact]
    public void Long_running_request_without_telemetry_has_no_invented_floor()
    {
        var now = DateTimeOffset.UtcNow;
        var startedAt = now.AddSeconds(-200); // 200 seconds elapsed

        var projection = service.Project(
            TransportRequestStatus.Running,
            rdsStatus: 1000,
            agvId: "sim_01",
            startedAt: startedAt,
            queuePosition: 1,
            now: now);

        Assert.Equal("UNAVAILABLE", projection.State);
        Assert.Null(projection.Seconds);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Queue_position_without_telemetry_is_not_an_eta(int queuePosition)
    {
        var projection = service.Project(
            TransportRequestStatus.Queued,
            rdsStatus: null,
            agvId: null,
            startedAt: null,
            queuePosition: queuePosition);

        Assert.Equal("UNAVAILABLE", projection.State);
        Assert.Null(projection.Seconds);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"remaining_time\": -1}")]
    [InlineData("{\"remaining_time\": 1e100}")]
    [InlineData("{\"remainingTime\": 2147483648}")]
    [InlineData("{\"remaining_time\": \"30\"}")]
    public void Missing_or_invalid_telemetry_returns_unavailable(string json)
    {
        using var document = JsonDocument.Parse(json);
        var eta = service.Project(TransportRequestStatus.Running, 1000, "robot", DateTimeOffset.UtcNow, 1, document.RootElement);
        Assert.Equal("UNAVAILABLE", eta.State);
        Assert.Null(eta.Seconds);
    }

    [Theory]
    [InlineData("{\"remainingTime\": 0}", 0)]
    [InlineData("{\"remaining_time\": 2147483647}", int.MaxValue)]
    public void Valid_telemetry_preserves_zero_and_integer_boundary(string json, int seconds)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(seconds, service.Project(TransportRequestStatus.Running, 1000, null, null, null, document.RootElement).Seconds);
    }

    [Fact]
    public void From_reliable_remaining_time_handles_valid_and_invalid_values()
    {
        var valid = service.FromReliableRemainingTime(65);
        Assert.Equal("AVAILABLE", valid.State);
        Assert.Equal(65, valid.Seconds);

        var negative = service.FromReliableRemainingTime(-5);
        Assert.Equal("UNAVAILABLE", negative.State);
        Assert.Null(negative.Seconds);

        var nullValue = service.FromReliableRemainingTime(null);
        Assert.Equal("UNAVAILABLE", nullValue.State);
        Assert.Null(nullValue.Seconds);
    }

    [Fact]
    public void Waiting_preparation_yields_unavailable_eta()
    {
        var projection = service.Project(
            TransportRequestStatus.Queued,
            rdsStatus: null,
            agvId: null,
            startedAt: null,
            queuePosition: 1,
            readinessState: TransportReadinessState.WaitingPreparation);

        Assert.Equal("UNAVAILABLE", projection.State);
        Assert.Null(projection.Seconds);
    }
}
