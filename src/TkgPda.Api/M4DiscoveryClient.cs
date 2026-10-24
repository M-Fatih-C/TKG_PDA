using System.Text.Json;
using TkgPda.Application;

namespace TkgPda.Api;

public sealed class M4DiscoveryClient(
    M4HttpClient httpClient,
    M4Options options,
    TimeProvider timeProvider,
    ILogger<M4DiscoveryClient> logger) : IM4DiscoveryClient
{
    private static readonly JsonElement EmptyPayload = JsonDocument.Parse("{}").RootElement.Clone();

    public async Task<M4DiscoverySnapshot> CaptureAsync(
        M4DiscoveryCommand command,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            throw new M4HttpException("M4_DISABLED", "M4 integration is disabled.");
        }
        if (!options.ReadEnabled)
        {
            throw new M4HttpException("M4_READ_DISABLED", "M4 read access is disabled.");
        }

        command = command.Validate();
        var capturedAt = timeProvider.GetUtcNow();
        var resources = new List<M4DiscoveryResource>(4);

        var sceneList = await CaptureAsync(
            M4DiscoveryResourceKinds.SceneList,
            externalScopeId: null,
            () => httpClient.GetAsync("/api/fleet/scenes/list", correlationId, cancellationToken));
        resources.Add(sceneList);

        var scenes = sceneList.Success
            ? M4DiscoveryJsonProjector.ProjectScenes(sceneList.RawPayload)
            : [];

        var effectiveSceneId = command.SceneId ?? ResolveSceneId(command.SceneName, scenes);
        var effectiveSceneName = command.SceneName ?? ResolveSceneName(effectiveSceneId, scenes);
        var scope = effectiveSceneId ?? effectiveSceneName;

        if (scope is not null)
        {
            var selector = effectiveSceneId is not null
                ? $"sceneId={Uri.EscapeDataString(effectiveSceneId)}"
                : $"sceneName={Uri.EscapeDataString(effectiveSceneName!)}";
            resources.Add(await CaptureAsync(
                M4DiscoveryResourceKinds.RobotList,
                scope,
                () => httpClient.GetAsync(
                    $"/api/fleet/robots/all-all?{selector}&rawAll=false",
                    correlationId,
                    cancellationToken)));

            if (effectiveSceneId is not null)
            {
                var encodedSceneId = Uri.EscapeDataString(effectiveSceneId);
                resources.Add(await CaptureAsync(
                    M4DiscoveryResourceKinds.SceneSchema,
                    effectiveSceneId,
                    () => httpClient.GetAsync(
                        $"/api/fleet/scenes/schema/{encodedSceneId}",
                        correlationId,
                        cancellationToken)));

                if (command.IncludeSceneSnapshot)
                {
                    resources.Add(await CaptureAsync(
                        M4DiscoveryResourceKinds.SceneSnapshot,
                        effectiveSceneId,
                        () => httpClient.PostQueryAsync(
                            $"/api/fleet/scenes/{encodedSceneId}/query-all",
                            new { },
                            correlationId,
                            cancellationToken)));
                }
            }
        }

        var robots = resources
            .Where(resource => resource.Kind == M4DiscoveryResourceKinds.RobotList && resource.Success)
            .SelectMany(resource => M4DiscoveryJsonProjector.ProjectRobots(resource.RawPayload))
            .ToArray();
        var endpoints = resources
            .Where(resource => resource.Kind is M4DiscoveryResourceKinds.SceneSchema or M4DiscoveryResourceKinds.SceneSnapshot
                               && resource.Success)
            .SelectMany(resource => M4DiscoveryJsonProjector.ProjectEndpoints(resource.RawPayload))
            .DistinctBy(endpoint => (endpoint.Kind, endpoint.ExternalId, endpoint.Name))
            .ToArray();

        var succeeded = resources.Count(resource => resource.Success);
        var status = succeeded == resources.Count
            ? M4DiscoveryStatuses.Success
            : succeeded == 0
                ? M4DiscoveryStatuses.Failed
                : M4DiscoveryStatuses.Partial;

        return new M4DiscoverySnapshot(
            Guid.NewGuid(),
            correlationId,
            status,
            command.SceneId,
            command.SceneName,
            capturedAt,
            capturedAt.AddSeconds(options.DiscoveryFreshnessSeconds),
            scenes,
            robots,
            endpoints,
            resources);
    }

    private async Task<M4DiscoveryResource> CaptureAsync(
        string kind,
        string? externalScopeId,
        Func<Task<M4JsonResponse>> read)
    {
        try
        {
            var response = await read();
            return new M4DiscoveryResource(
                kind,
                externalScopeId,
                Success: true,
                ErrorCode: null,
                ErrorMessage: null,
                response.Body.Clone());
        }
        catch (M4HttpException error)
        {
            logger.LogWarning(
                "M4 discovery resource {ResourceKind} failed with {Code} for scope {ExternalScopeId}.",
                kind,
                error.Code,
                externalScopeId);
            return new M4DiscoveryResource(
                kind,
                externalScopeId,
                Success: false,
                error.Code,
                error.Message,
                EmptyPayload);
        }
    }

    private static string? ResolveSceneId(string? requestedName, IReadOnlyList<M4DiscoveredScene> scenes)
    {
        if (requestedName is null)
        {
            return null;
        }

        var matches = scenes
            .Where(scene => string.Equals(scene.SceneName, requestedName, StringComparison.OrdinalIgnoreCase))
            .Select(scene => scene.SceneId)
            .Where(value => value is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string? ResolveSceneName(string? requestedId, IReadOnlyList<M4DiscoveredScene> scenes)
    {
        if (requestedId is null)
        {
            return null;
        }

        var matches = scenes
            .Where(scene => string.Equals(scene.SceneId, requestedId, StringComparison.Ordinal))
            .Select(scene => scene.SceneName)
            .Where(value => value is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

internal static class M4DiscoveryJsonProjector
{
    private const int MaxProjectedItems = 5000;
    private static readonly string[] CollectionWrappers = ["data", "items", "list", "records", "result"];

    public static IReadOnlyList<M4DiscoveredScene> ProjectScenes(JsonElement root) =>
        CandidateItems(root, ["scenes", "sceneList"])
            .Select(item => new M4DiscoveredScene(
                Text(item, "sceneId", "id", "_id"),
                Text(item, "sceneName", "name", "displayName"),
                NullableBool(item, "disabled") ?? Negate(NullableBool(item, "enabled"))))
            .Where(scene => scene.SceneId is not null || scene.SceneName is not null)
            .Distinct()
            .Take(MaxProjectedItems)
            .ToArray();

    public static IReadOnlyList<M4DiscoveredRobot> ProjectRobots(JsonElement root) =>
        CandidateItems(root, ["robots", "robotList"])
            .Select(item => new M4DiscoveredRobot(
                Text(item, "robotId", "vehicleId", "id", "uuid"),
                Text(item, "robotName", "name", "vehicleName"),
                NullableBool(item, "online", "isOnline"),
                NullableBool(item, "simulated", "mock", "isSimulated"),
                Text(item, "state", "status", "dispatchState", "workState")))
            .Where(robot => robot.RobotId is not null || robot.RobotName is not null)
            .Distinct()
            .Take(MaxProjectedItems)
            .ToArray();

    public static IReadOnlyList<M4DiscoveredEndpoint> ProjectEndpoints(JsonElement root)
    {
        var output = new List<M4DiscoveredEndpoint>();
        Traverse(root, output, depth: 0);
        return output
            .Where(endpoint => endpoint.ExternalId is not null || endpoint.Name is not null)
            .Distinct()
            .Take(MaxProjectedItems)
            .ToArray();
    }

    private static IEnumerable<JsonElement> CandidateItems(JsonElement root, IReadOnlyList<string> namedCollections)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        foreach (var property in root.EnumerateObject())
        {
            var isNamedCollection = namedCollections.Contains(property.Name, StringComparer.OrdinalIgnoreCase);
            var isWrapper = CollectionWrappers.Contains(property.Name, StringComparer.OrdinalIgnoreCase);
            if ((isNamedCollection || isWrapper) && property.Value.ValueKind == JsonValueKind.Array)
            {
                return property.Value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .ToArray();
            }
            if ((isNamedCollection || isWrapper) && property.Value.ValueKind == JsonValueKind.Object)
            {
                var nested = CandidateItems(property.Value, namedCollections).ToArray();
                if (nested.Length > 0)
                {
                    return nested;
                }
            }
        }

        return [];
    }

    private static void Traverse(JsonElement element, ICollection<M4DiscoveredEndpoint> output, int depth)
    {
        if (depth > 12 || output.Count >= MaxProjectedItems || element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            var kind = EndpointKind(property.Name);
            if (kind is not null && property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    output.Add(new M4DiscoveredEndpoint(
                        Text(item, "locationId", "pointId", "binId", "id", "code"),
                        Text(item, "locationName", "pointName", "binName", "name", "displayName"),
                        kind));
                    Traverse(item, output, depth + 1);
                }
            }
            else if (property.Value.ValueKind == JsonValueKind.Object)
            {
                Traverse(property.Value, output, depth + 1);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    Traverse(item, output, depth + 1);
                }
            }
        }
    }

    private static string? EndpointKind(string propertyName) => propertyName.ToLowerInvariant() switch
    {
        "points" or "pointlist" => "POINT",
        "bins" or "binlist" or "storagebins" or "storagebinlist" => "STORAGE_BIN",
        "locations" or "locationlist" => "LOCATION",
        _ => null
    };

    private static string? Text(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryProperty(item, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
            else if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetRawText();
            }
        }
        return null;
    }

    private static bool? NullableBool(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryProperty(item, name, out var value))
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number != 0;
            }
        }
        return null;
    }

    private static bool TryProperty(JsonElement item, string name, out JsonElement value)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static bool? Negate(bool? value) => value.HasValue ? !value.Value : null;
}
