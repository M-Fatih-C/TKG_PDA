using System.Text.Json;

namespace TkgPda.Application;

public static class M4DiscoveryResourceKinds
{
    public const string SceneList = "SCENE_LIST";
    public const string RobotList = "ROBOT_LIST";
    public const string SceneSchema = "SCENE_SCHEMA";
    public const string SceneSnapshot = "SCENE_SNAPSHOT";
}

public static class M4DiscoveryStatuses
{
    public const string Success = "SUCCESS";
    public const string Partial = "PARTIAL";
    public const string Failed = "FAILED";
}

public sealed record M4DiscoveryCommand(
    string? SceneId = null,
    string? SceneName = null,
    bool IncludeSceneSnapshot = true)
{
    public M4DiscoveryCommand Validate()
    {
        var sceneId = Normalize(SceneId);
        var sceneName = Normalize(SceneName);

        if (sceneId is { Length: > 200 } || sceneName is { Length: > 200 })
        {
            throw new M4DiscoveryRuleException(
                "M4_DISCOVERY_SCOPE_INVALID",
                "M4 scene identity must not exceed 200 characters.");
        }

        if (ContainsUnsafePathCharacter(sceneId) || ContainsUnsafePathCharacter(sceneName))
        {
            throw new M4DiscoveryRuleException(
                "M4_DISCOVERY_SCOPE_INVALID",
                "M4 scene identity contains unsupported path characters.");
        }

        return this with { SceneId = sceneId, SceneName = sceneName };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ContainsUnsafePathCharacter(string? value) =>
        value?.Contains('/') == true
        || value?.Contains('\\') == true
        || value?.Any(char.IsControl) == true;
}

public sealed record M4DiscoveredScene(
    string? SceneId,
    string? SceneName,
    bool? Disabled);

public sealed record M4DiscoveredRobot(
    string? RobotId,
    string? RobotName,
    bool? Online,
    bool? Simulated,
    string? State);

public sealed record M4DiscoveredEndpoint(
    string? ExternalId,
    string? Name,
    string Kind);

public sealed record M4DiscoveryResource(
    string Kind,
    string? ExternalScopeId,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    JsonElement RawPayload);

public sealed record M4DiscoverySnapshot(
    Guid Id,
    Guid CorrelationId,
    string Status,
    string? RequestedSceneId,
    string? RequestedSceneName,
    DateTimeOffset CapturedAt,
    DateTimeOffset FreshUntil,
    IReadOnlyList<M4DiscoveredScene> Scenes,
    IReadOnlyList<M4DiscoveredRobot> Robots,
    IReadOnlyList<M4DiscoveredEndpoint> Endpoints,
    IReadOnlyList<M4DiscoveryResource> Resources)
{
    public bool IsStale(DateTimeOffset now) => now >= FreshUntil;

    public M4DiscoverySummary ToSummary(DateTimeOffset now) => new(
        Id,
        CorrelationId,
        Status,
        RequestedSceneId,
        RequestedSceneName,
        CapturedAt,
        FreshUntil,
        IsStale(now),
        Scenes,
        Robots,
        Endpoints,
        Resources.Select(resource => new M4DiscoveryResourceSummary(
            resource.Kind,
            resource.ExternalScopeId,
            resource.Success,
            resource.ErrorCode,
            resource.ErrorMessage)).ToArray());
}

public sealed record M4DiscoveryResourceSummary(
    string Kind,
    string? ExternalScopeId,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record M4DiscoverySummary(
    Guid Id,
    Guid CorrelationId,
    string Status,
    string? RequestedSceneId,
    string? RequestedSceneName,
    DateTimeOffset CapturedAt,
    DateTimeOffset FreshUntil,
    bool Stale,
    IReadOnlyList<M4DiscoveredScene> Scenes,
    IReadOnlyList<M4DiscoveredRobot> Robots,
    IReadOnlyList<M4DiscoveredEndpoint> Endpoints,
    IReadOnlyList<M4DiscoveryResourceSummary> Resources);

public sealed class M4DiscoveryRuleException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public interface IM4DiscoveryClient
{
    Task<M4DiscoverySnapshot> CaptureAsync(
        M4DiscoveryCommand command,
        Guid correlationId,
        CancellationToken cancellationToken = default);
}

public interface IM4DiscoveryStore
{
    Task SaveAsync(M4DiscoverySnapshot snapshot, CancellationToken cancellationToken = default);
    Task<M4DiscoverySnapshot?> GetLatestAsync(CancellationToken cancellationToken = default);
    Task<M4DiscoverySnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class M4DiscoveryService(
    IM4DiscoveryClient client,
    IM4DiscoveryStore store,
    TimeProvider timeProvider)
{
    public async Task<M4DiscoverySummary> CaptureAsync(
        M4DiscoveryCommand command,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validated = command.Validate();
        var snapshot = await client.CaptureAsync(validated, correlationId, cancellationToken);
        await store.SaveAsync(snapshot, cancellationToken);
        return snapshot.ToSummary(timeProvider.GetUtcNow());
    }

    public async Task<M4DiscoverySummary?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await store.GetLatestAsync(cancellationToken);
        return snapshot?.ToSummary(timeProvider.GetUtcNow());
    }

    public Task<M4DiscoverySnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new M4DiscoveryRuleException("M4_DISCOVERY_ID_REQUIRED", "A discovery capture id is required.");
        }

        return store.GetAsync(id, cancellationToken);
    }
}
