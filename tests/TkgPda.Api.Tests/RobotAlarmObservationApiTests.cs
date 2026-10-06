using System.Net;
using System.Net.Http.Json;
using Npgsql;
using TkgPda.Api;
using TkgPda.Application;
using TkgPda.Infrastructure.Postgres;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class RobotAlarmObservationApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Successful_snapshots_upsert_and_clear_without_touching_slot_state()
    {
        await fixture.ResetAsync();
        var beforeSlots = await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.slot_runtime_state");
        var beforeOccupancy = await fixture.ScalarAsync<string>(
            "SELECT COALESCE(string_agg(occupancy_state || '/' || load_state, ',' ORDER BY layout_slot_id::text), '') FROM ops.slot_runtime_state");
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);
        var firstSeen = DateTimeOffset.Parse("2026-10-06T08:00:00Z");
        var correlation = Guid.NewGuid();
        await store.ObserveAlarmSnapshotAsync(
            [new RdsRobotAlarmSnapshot("robot-1", "error", "54001", "brake", "2026-10-06T08:00:00Z", 1)],
            correlation,
            firstSeen);
        await store.ObserveAlarmSnapshotAsync(
            [new RdsRobotAlarmSnapshot("robot-1", "error", "54001", "brake", "2026-10-06T08:00:00Z", 3)],
            Guid.NewGuid(),
            firstSeen.AddMinutes(2));

        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.robot_alarm_observations"));
        Assert.Equal(3, await fixture.ScalarAsync<int>("SELECT occurrence_count FROM integration.robot_alarm_observations"));
        Assert.True(await fixture.ScalarAsync<bool>("SELECT cleared_at IS NULL FROM integration.robot_alarm_observations"));

        await store.ObserveAlarmReadFailureAsync(Guid.NewGuid(), firstSeen.AddMinutes(3), "RDS_UNAVAILABLE", "read failed");
        Assert.True(await fixture.ScalarAsync<bool>("SELECT cleared_at IS NULL FROM integration.robot_alarm_observations"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.robot_alarm_read_failures"));

        await store.ObserveAlarmSnapshotAsync([], Guid.NewGuid(), firstSeen.AddMinutes(4));
        Assert.True(await fixture.ScalarAsync<bool>("SELECT cleared_at IS NOT NULL FROM integration.robot_alarm_observations"));
        Assert.Equal(beforeSlots, await fixture.ScalarAsync<long>("SELECT count(*) FROM ops.slot_runtime_state"));
        Assert.Equal(beforeOccupancy, await fixture.ScalarAsync<string>(
            "SELECT COALESCE(string_agg(occupancy_state || '/' || load_state, ',' ORDER BY layout_slot_id::text), '') FROM ops.slot_runtime_state"));

        var admin = await fixture.SeedIdentityAsync(RoleCodes.Admin, machineCode: null);
        using var client = await fixture.AdminLoginClientAsync(admin);
        var history = await client.GetFromJsonAsync<RobotAlarmObservationRecord[]>("/api/v1/admin/rds/alarm-history?limit=10");
        Assert.NotNull(history);
        Assert.Single(history);
        Assert.Equal("error", history[0].Severity);
        Assert.NotNull(history[0].ClearedAt);

        var line = await fixture.SeedIdentityAsync(machineCode: "SH-01-A");
        using var denied = await fixture.LoginClientAsync(line);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync("/api/v1/admin/rds/alarm-history")).StatusCode);
    }
}
