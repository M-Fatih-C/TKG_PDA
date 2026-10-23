using TkgPda.Application;
using TkgPda.Domain;
using Xunit;

namespace TkgPda.Application.Tests;

public sealed class ProductionSessionServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Missing_start_identity_never_reaches_store(int field)
    {
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        ids[field] = Guid.Empty;
        var store = new RecordingStore();
        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() => new ProductionSessionService(store)
            .StartAsync(new(ids[0], ids[1], ids[2], ids[3], ids[4], ids[5], "key")));
        Assert.Equal("ID_REQUIRED", error.Code);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(0, "IDEMPOTENCY_KEY_REQUIRED")]
    [InlineData(201, "IDEMPOTENCY_KEY_TOO_LONG")]
    public async Task Invalid_start_key_never_reaches_store(int length, string code)
    {
        var store = new RecordingStore();
        var command = Start() with { IdempotencyKey = new string('k', length) };
        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() => new ProductionSessionService(store).StartAsync(command));
        Assert.Equal(code, error.Code);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Exact_start_context_and_cancellation_are_forwarded()
    {
        var store = new RecordingStore();
        using var cancellation = new CancellationTokenSource();
        var command = Start();
        await new ProductionSessionService(store).StartAsync(command, cancellation.Token);
        Assert.Same(command, store.StartCommand);
        Assert.Equal(cancellation.Token, store.Token);
    }

    [Theory]
    [InlineData(0, "IDEMPOTENCY_KEY_REQUIRED")]
    [InlineData(201, "IDEMPOTENCY_KEY_TOO_LONG")]
    public async Task Invalid_end_key_never_reaches_store(int length, string code)
    {
        var store = new RecordingStore();
        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() => new ProductionSessionService(store)
            .EndAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('k', length))));
        Assert.Equal(code, error.Code);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task End_requires_an_exact_session_identity()
    {
        var store = new RecordingStore();
        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() => new ProductionSessionService(store)
            .EndAsync(new(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), "key")));
        Assert.Equal("ID_REQUIRED", error.Code);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task End_and_active_lookup_are_delegated_without_execution_commands()
    {
        var store = new RecordingStore();
        var service = new ProductionSessionService(store);
        var command = new EndProductionSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "key");
        await service.EndAsync(command);
        await service.FindActiveAsync(command.MachineId);
        Assert.Same(command, store.EndCommand);
        Assert.Equal(command.MachineId, store.MachineId);
        Assert.Equal(2, store.Calls);
    }

    private static StartProductionSession Start() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "key");

    private sealed class RecordingStore : IProductionSessionStore
    {
        public int Calls { get; private set; }
        public StartProductionSession? StartCommand { get; private set; }
        public EndProductionSession? EndCommand { get; private set; }
        public Guid MachineId { get; private set; }
        public CancellationToken Token { get; private set; }
        private static ProductionSessionResult Result => new(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), null, null, null), false);
        public Task<ProductionSessionRecord?> FindActiveAsync(Guid machineId, CancellationToken cancellationToken = default)
        {
            Calls++;
            MachineId = machineId;
            return Task.FromResult<ProductionSessionRecord?>(null);
        }
        public Task<ProductionSessionResult> StartAsync(StartProductionSession command, CancellationToken cancellationToken = default)
        {
            Calls++;
            StartCommand = command;
            Token = cancellationToken;
            return Task.FromResult(Result);
        }
        public Task<ProductionSessionResult> EndAsync(EndProductionSession command, CancellationToken cancellationToken = default)
        {
            Calls++;
            EndCommand = command;
            return Task.FromResult(Result);
        }
    }
}
