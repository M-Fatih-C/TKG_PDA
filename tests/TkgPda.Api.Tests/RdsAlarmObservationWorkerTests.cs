using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task Read_failure_persists_the_rds_code_and_other_exceptions_use_the_type_name()
    {
        var client = new ScriptedClient(Alarm()) { FailNext = true };
        var store = new MemoryObservationStore();
        var service = Service(client, store);
        var correlation = Guid.NewGuid();
        await Assert.ThrowsAsync<RdsReadException>(() => service.ObserveCurrentAlarmsAsync(correlation, CancellationToken.None));
        Assert.Equal("RDS_UNAVAILABLE", store.LastErrorCode);

        client.FailWith = new InvalidOperationException("not an rds read");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ObserveCurrentAlarmsAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(nameof(InvalidOperationException), store.LastErrorCode);
    }

    [Fact]
    public async Task History_write_failure_keeps_the_live_snapshot_and_the_worker_continues()
    {
        var alarm = Alarm();
        var client = new ScriptedClient(alarm);
        var store = new MemoryObservationStore { ThrowOnNextSnapshot = true };
        var logs = new CaptureLogger();
        var correlation = Guid.NewGuid();
        var service = Service(client, store, logs);
        var result = await service.ObserveCurrentAlarmsAsync(correlation, CancellationToken.None);

        Assert.Equal(alarm.Code, Assert.Single(result.Data.Alarms).Code);
        Assert.Equal(0, store.OpenCount);
        Assert.Contains(logs.Warnings, line =>
            line.Contains(correlation.ToString(), StringComparison.Ordinal)
            && line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal)
            && !line.Contains("Host=", StringComparison.Ordinal));

        store.ThrowOnNextSnapshot = false;
        var worker = new RdsAlarmObservationWorker(service, Options(true), NullLogger<RdsAlarmObservationWorker>.Instance);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await worker.StartAsync(lifetime.Token);
        await WaitUntilAsync(() => store.OpenCount == 1, TimeSpan.FromSeconds(7));
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(1, store.OpenCount);
    }

    private static RdsAlarmObservationWorker Worker(ScriptedClient client, MemoryObservationStore store, bool enabled) =>
        new(Service(client, store), Options(enabled), NullLogger<RdsAlarmObservationWorker>.Instance);

    private static RdsIntegrationService Service(
        ScriptedClient client,
        MemoryObservationStore store,
        ILogger<RdsIntegrationService>? logger = null) =>
        new(client, store, TimeProvider.System, logger);

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
        public Exception? FailWith { get; set; }
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

            if (FailWith is not null)
            {
                var error = FailWith;
                FailWith = null;
                throw error;
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
        public string? LastErrorCode { get; private set; }
        public bool ThrowOnNextSnapshot { get; set; }
        public int OpenCount => rows.Count(row => row.ClearedAt is null);
        public int Rows => rows.Count;

        public Task ObserveAlarmSnapshotAsync(
            IReadOnlyList<RdsRobotAlarmSnapshot> alarms,
            Guid correlationId,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnNextSnapshot)
            {
                ThrowOnNextSnapshot = false;
                throw new InvalidOperationException("history store unavailable");
            }

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
            LastErrorCode = errorCode;
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

    private sealed class CaptureLogger : ILogger<RdsIntegrationService>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
