using Microsoft.Extensions.Logging.Abstractions;
using TkgPda.Api;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class RdsAlarmObservationWorkerTests
{
    [Fact]
    public async Task Background_observation_persists_an_alarm_without_an_api_get()
    {
        var alarm = Alarm();
        var client = new ScriptedClient(alarm);
        var store = new MemoryObservationStore();
        var worker = Worker(client, store, enabled: true);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StartAsync(lifetime.Token);
        await WaitUntilAsync(() => store.OpenCount == 1);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, store.OpenCount);
        Assert.True(client.Calls >= 1);
        Assert.Equal(0, store.SlotMutations);
    }

    [Fact]
    public async Task Repeated_snapshot_does_not_duplicate_and_a_later_empty_snapshot_clears()
    {
        var alarm = Alarm();
        var client = new ScriptedClient(alarm);
        var store = new MemoryObservationStore();
        var service = Service(client, store);
        await service.ObserveCurrentAlarmsAsync(Guid.NewGuid(), CancellationToken.None);
        await service.ObserveCurrentAlarmsAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(1, store.OpenCount);
        Assert.Equal(1, store.Rows);

        client.NextAlarms = [];
        await service.ObserveCurrentAlarmsAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(0, store.OpenCount);
        Assert.Equal(1, store.Rows);
    }

    [Fact]
    public async Task Read_failure_does_not_clear_an_open_observation()
    {
        var client = new ScriptedClient(Alarm());
        var store = new MemoryObservationStore();
        var service = Service(client, store);
        await service.ObserveCurrentAlarmsAsync(Guid.NewGuid(), CancellationToken.None);
        client.FailNext = true;
        await Assert.ThrowsAsync<RdsReadException>(() => service.ObserveCurrentAlarmsAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(1, store.OpenCount);
        Assert.Equal(1, store.Failures);
    }

    [Fact]
    public async Task Disabled_rds_does_not_poll()
    {
        var client = new ScriptedClient(Alarm());
        var worker = Worker(client, new MemoryObservationStore(), enabled: false);
        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Worker_survives_a_transient_read_failure_and_continues()
    {
        var client = new ScriptedClient(Alarm()) { FailNext = true };
        var store = new MemoryObservationStore();
        var worker = Worker(client, store, enabled: true);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await worker.StartAsync(lifetime.Token);
        await WaitUntilAsync(() => client.Calls >= 2 && store.OpenCount == 1, TimeSpan.FromSeconds(11));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(client.Calls >= 2);
        Assert.Equal(1, store.OpenCount);
        Assert.Equal(1, store.Failures);
    }

    [Fact]
    public async Task Worker_stops_cleanly_on_cancellation()
    {
        var client = new ScriptedClient(Alarm()) { WaitForCancellation = true };
        var worker = Worker(client, new MemoryObservationStore(), enabled: true);
        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => client.Calls >= 1);
        var completed = worker.StopAsync(CancellationToken.None);
        await completed.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(completed.IsCompletedSuccessfully);
    }

    private static RdsAlarmObservationWorker Worker(ScriptedClient client, MemoryObservationStore store, bool enabled) =>
        new(Service(client, store), Options(enabled), NullLogger<RdsAlarmObservationWorker>.Instance);

    private static RdsIntegrationService Service(ScriptedClient client, MemoryObservationStore store) =>
        new(client, store, TimeProvider.System);

    private static RdsOptions Options(bool enabled) =>
        new(enabled, enabled ? "http://127.0.0.1:8080" : null, 5, AlarmObservationIntervalSeconds: 5);

    private static RdsRobotAlarmSnapshot Alarm() =>
        new("robot-1", "error", "54001", "brake", "2026-10-06T08:00:00Z", 1);

    private static async Task WaitUntilAsync(Func<bool> ready, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (!ready() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(ready());
    }

    private sealed class ScriptedClient(RdsRobotAlarmSnapshot alarm) : IRdsReadClient
    {
        public IReadOnlyList<RdsRobotAlarmSnapshot> NextAlarms { get; set; } = [alarm];
        public bool FailNext { get; set; }
        public bool WaitForCancellation { get; set; }
        public int Calls { get; private set; }
        public bool Enabled => true;
        public string? SafeBaseAddress => "http://127.0.0.1:8080";
        public DateTimeOffset? LastSuccessfulRead => null;

        public Task<RdsReadResult<IReadOnlyList<RdsWorkSiteSnapshot>>> GetWorkSitesAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RdsReadResult<RdsWindTaskPage>> QueryWindTasksAsync(RdsWindTaskQuery query, Guid correlationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<RdsReadResult<RdsRobotAlarmReport>> GetRobotAlarmsAsync(Guid correlationId, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (FailNext)
            {
                FailNext = false;
                throw new RdsReadException("RDS_UNAVAILABLE", "read failed", "/api/agv-report/core", correlationId);
            }

            return new RdsReadResult<RdsRobotAlarmReport>(
                new RdsRobotAlarmReport(NextAlarms, []),
                0,
                "ok",
                "/api/agv-report/core",
                correlationId,
                DateTimeOffset.UtcNow);
        }
    }

    private sealed class MemoryObservationStore : IRdsIntegrationStore
    {
        private readonly List<RobotAlarmObservationRecord> rows = [];
        public int Failures { get; private set; }
        public int SlotMutations { get; private set; }
        public int OpenCount => rows.Count(row => row.ClearedAt is null);
        public int Rows => rows.Count;

        public Task ObserveAlarmSnapshotAsync(
            IReadOnlyList<RdsRobotAlarmSnapshot> alarms,
            Guid correlationId,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
        {
            var open = rows.Where(row => row.ClearedAt is null).ToArray();
            var plan = RobotAlarmObservationRules.Reconcile(open, alarms, observedAt, correlationId);
            foreach (var item in plan.Upserts)
            {
                var index = rows.FindIndex(row => row.Id == item.Id);
                if (index >= 0)
                {
                    rows[index] = item;
                }
                else
                {
                    rows.Add(item);
                }
            }

            foreach (var id in plan.ClearIds)
            {
                var index = rows.FindIndex(row => row.Id == id && row.ClearedAt is null);
                if (index >= 0)
                {
                    rows[index] = rows[index] with { ClearedAt = observedAt };
                }
            }

            return Task.CompletedTask;
        }

        public Task ObserveAlarmReadFailureAsync(
            Guid correlationId,
            DateTimeOffset observedAt,
            string errorCode,
            string message,
            CancellationToken cancellationToken = default)
        {
            Failures++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RdsNavigationProfileRecord>> ListNavigationProfilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RdsNavigationProfileRecord>>([]);

        public Task<(int Created, int Updated)> SyncWorkSitesAsync(Guid navigationProfileId, IReadOnlyList<RdsWorkSiteSnapshot> workSites, DateTimeOffset syncedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult((0, 0));

        public Task<IReadOnlyList<RdsMirroredWorkSiteRecord>> ListWorkSitesAsync(RdsWorkSiteFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RdsMirroredWorkSiteRecord>>([]);

        public Task<IReadOnlyList<RdsSlotMappingRecord>> ListSlotMappingsAsync(Guid? layoutId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RdsSlotMappingRecord>>([]);

        public Task<RdsSlotMappingRecord> SetSlotMappingAsync(Guid slotId, Guid rdsWorkSiteId, AdminMutationContext context, CancellationToken cancellationToken = default)
        {
            SlotMutations++;
            throw new NotSupportedException();
        }

        public Task RemoveSlotMappingAsync(Guid slotId, AdminMutationContext context, CancellationToken cancellationToken = default)
        {
            SlotMutations++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RdsActiveLockFingerprint>> GetActiveTkgLockFingerprintsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RdsActiveLockFingerprint>>([]);
    }
}
