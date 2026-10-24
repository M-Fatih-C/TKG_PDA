using System.Text.Json;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Application.Tests;

public sealed class M4DiscoveryServiceTests
{
    [Fact]
    public async Task Capture_validates_persists_and_returns_a_fresh_summary()
    {
        var now = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
        var snapshot = Snapshot(now);
        var client = new FakeClient(snapshot);
        var store = new FakeStore();
        var service = new M4DiscoveryService(client, store, new FixedTimeProvider(now));

        var result = await service.CaptureAsync(
            new M4DiscoveryCommand(SceneId: " scene-1 "),
            snapshot.CorrelationId);

        Assert.Equal("scene-1", client.LastCommand!.SceneId);
        Assert.Same(snapshot, store.Saved);
        Assert.False(result.Stale);
        Assert.Equal(M4DiscoveryStatuses.Success, result.Status);
    }

    [Fact]
    public async Task Latest_marks_expired_evidence_stale_without_changing_the_saved_snapshot()
    {
        var capturedAt = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
        var snapshot = Snapshot(capturedAt);
        var store = new FakeStore { Saved = snapshot };
        var service = new M4DiscoveryService(
            new FakeClient(snapshot),
            store,
            new FixedTimeProvider(capturedAt.AddMinutes(6)));

        var result = await service.GetLatestAsync();

        Assert.NotNull(result);
        Assert.True(result.Stale);
        Assert.Equal(snapshot.FreshUntil, result.FreshUntil);
    }

    [Theory]
    [InlineData("bad/scene")]
    [InlineData("bad\\scene")]
    [InlineData("bad\nscene")]
    public async Task Unsafe_scene_identity_is_rejected_before_client_or_store_access(string sceneId)
    {
        var now = DateTimeOffset.UtcNow;
        var client = new FakeClient(Snapshot(now));
        var store = new FakeStore();
        var service = new M4DiscoveryService(client, store, new FixedTimeProvider(now));

        var error = await Assert.ThrowsAsync<M4DiscoveryRuleException>(() =>
            service.CaptureAsync(new M4DiscoveryCommand(SceneId: sceneId), Guid.NewGuid()));

        Assert.Equal("M4_DISCOVERY_SCOPE_INVALID", error.Code);
        Assert.Null(client.LastCommand);
        Assert.Null(store.Saved);
    }

    private static M4DiscoverySnapshot Snapshot(DateTimeOffset capturedAt)
    {
        using var document = JsonDocument.Parse("{}");
        return new M4DiscoverySnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            M4DiscoveryStatuses.Success,
            "scene-1",
            "OneDemo",
            capturedAt,
            capturedAt.AddMinutes(5),
            [new M4DiscoveredScene("scene-1", "OneDemo", false)],
            [],
            [],
            [new M4DiscoveryResource(
                M4DiscoveryResourceKinds.SceneList,
                null,
                true,
                null,
                null,
                document.RootElement.Clone())]);
    }

    private sealed class FakeClient(M4DiscoverySnapshot snapshot) : IM4DiscoveryClient
    {
        public M4DiscoveryCommand? LastCommand { get; private set; }

        public Task<M4DiscoverySnapshot> CaptureAsync(
            M4DiscoveryCommand command,
            Guid correlationId,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FakeStore : IM4DiscoveryStore
    {
        public M4DiscoverySnapshot? Saved { get; set; }

        public Task SaveAsync(M4DiscoverySnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Saved = snapshot;
            return Task.CompletedTask;
        }

        public Task<M4DiscoverySnapshot?> GetLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Saved);

        public Task<M4DiscoverySnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Saved?.Id == id ? Saved : null);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
