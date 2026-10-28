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

    [Fact]
    public async Task Concurrent_snapshots_keep_one_open_fingerprint_and_ignore_a_stale_clear()
    {
        await fixture.ResetAsync();
        var before = await fixture.ScalarAsync<string>(
            "SELECT COALESCE(string_agg(occupancy_state || '/' || load_state || '/' || COALESCE(current_part_id::text, ''), ',' ORDER BY layout_slot_id::text), '') FROM ops.slot_runtime_state");
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresRdsIntegrationStore(dataSource);
        var seen = DateTimeOffset.Parse("2026-10-06T09:00:00Z");
        var alarm = new RdsRobotAlarmSnapshot("robot-2", "fatal", "54002", "estop", "2026-10-06T09:00:00Z", 1);

        await Task.WhenAll(
            store.ObserveAlarmSnapshotAsync([alarm], Guid.NewGuid(), seen),
            store.ObserveAlarmSnapshotAsync([alarm], Guid.NewGuid(), seen));

        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM integration.robot_alarm_observations WHERE cleared_at IS NULL"));

        await Task.WhenAll(
            store.ObserveAlarmSnapshotAsync([alarm with { OccurrenceCount = 2 }], Guid.NewGuid(), seen.AddMinutes(1)),
            store.ObserveAlarmSnapshotAsync([], Guid.NewGuid(), seen.AddSeconds(-30)));

        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM integration.robot_alarm_observations WHERE cleared_at IS NULL"));
        Assert.Equal(before, await fixture.ScalarAsync<string>(
            "SELECT COALESCE(string_agg(occupancy_state || '/' || load_state || '/' || COALESCE(current_part_id::text, ''), ',' ORDER BY layout_slot_id::text), '') FROM ops.slot_runtime_state"));
    }

    [Fact]
    public async Task Newer_empty_snapshot_blocks_an_older_alarm()
    {
        var store = await FreshStoreAsync();
        var ten = At("10:00:00Z");
        await store.ObserveAlarmSnapshotAsync([], Guid.NewGuid(), ten);
        await store.ObserveAlarmSnapshotAsync([Alarm("A")], Guid.NewGuid(), At("09:59:00Z"));

        Assert.Equal(0L, await OpenCountAsync());
        Assert.Equal(ten, await WatermarkAsync());
    }

    [Fact]
    public async Task Newer_alarm_survives_an_older_empty_snapshot()
    {
        var store = await FreshStoreAsync();
        var ten = At("10:00:00Z");
        await store.ObserveAlarmSnapshotAsync([Alarm("A")], Guid.NewGuid(), ten);
        await store.ObserveAlarmSnapshotAsync([], Guid.NewGuid(), At("09:59:00Z"));

        Assert.Equal(1L, await OpenCountAsync());
        Assert.Equal(ten, await WatermarkAsync());
    }

    [Fact]
    public async Task Newer_empty_snapshot_clears_an_older_alarm()
    {
        var store = await FreshStoreAsync();
        var ten = At("10:00:00Z");
        await store.ObserveAlarmSnapshotAsync([Alarm("A")], Guid.NewGuid(), At("09:59:00Z"));
        await store.ObserveAlarmSnapshotAsync([], Guid.NewGuid(), ten);

        Assert.Equal(0L, await OpenCountAsync());
        Assert.Equal(ten, await fixture.ScalarAsync<DateTimeOffset>(
            "SELECT cleared_at FROM integration.robot_alarm_observations"));
        Assert.Equal(ten, await WatermarkAsync());
    }

    [Fact]
    public async Task The_same_snapshot_timestamp_is_idempotent()
    {
        var store = await FreshStoreAsync();
        var ten = At("10:00:00Z");
        var alarm = Alarm("A");
        await store.ObserveAlarmSnapshotAsync([alarm], Guid.NewGuid(), ten);
        await store.ObserveAlarmSnapshotAsync([alarm], Guid.NewGuid(), ten);

        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.robot_alarm_observations"));
        Assert.Equal(1L, await OpenCountAsync());
        Assert.Equal(ten, await WatermarkAsync());
    }

    [Fact]
    public async Task A_failed_read_does_not_advance_the_successful_watermark()
    {
        var store = await FreshStoreAsync();
        var ten = At("10:00:00Z");
        await store.ObserveAlarmSnapshotAsync([Alarm("A")], Guid.NewGuid(), ten);
        await store.ObserveAlarmReadFailureAsync(Guid.NewGuid(), At("10:01:00Z"), "RDS_UNAVAILABLE", "read failed");

        Assert.Equal(1L, await OpenCountAsync());
        Assert.Equal(ten, await WatermarkAsync());
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM integration.robot_alarm_read_failures"));
    }

    private async Task<PostgresRdsIntegrationStore> FreshStoreAsync()
    {
        await fixture.ResetAsync();
        return new PostgresRdsIntegrationStore(NpgsqlDataSource.Create(fixture.ConnectionString));
    }

    private static DateTimeOffset At(string clock) => DateTimeOffset.Parse($"2026-10-06T{clock}");

    private static RdsRobotAlarmSnapshot Alarm(string code) =>
        new("robot-watermark", "error", code, "estop", "2026-10-06T10:00:00Z", 1);

    private Task<long> OpenCountAsync() => fixture.ScalarAsync<long>(
        "SELECT count(*) FROM integration.robot_alarm_observations WHERE cleared_at IS NULL");

    private Task<DateTimeOffset> WatermarkAsync() => fixture.ScalarAsync<DateTimeOffset>(
        "SELECT last_successful_snapshot_at FROM integration.robot_alarm_observation_state");
}
