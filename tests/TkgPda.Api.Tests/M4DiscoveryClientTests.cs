using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class M4DiscoveryClientTests
{
    [Fact]
    public async Task Capture_projects_scene_robot_and_endpoint_evidence_without_mutation_routes()
    {
        var handler = new RouteHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/fleet/scenes/list" => Json(HttpStatusCode.OK,
                """[{"id":"scene-1","name":"OneDemo","disabled":false}]"""),
            "/api/fleet/robots/all-all" => Json(HttpStatusCode.OK,
                """{"robots":[{"id":"robot-1","name":"Box-01","online":true,"mock":true,"status":"Idle"}]}"""),
            "/api/fleet/scenes/schema/scene-1" => Json(HttpStatusCode.OK,
                """{"maps":[{"points":[{"id":"point-1","name":"P1"}],"storageBins":[{"id":"bin-1","name":"B1"}]}]}"""),
            "/api/fleet/scenes/scene-1/query-all" => Json(HttpStatusCode.OK,
                """{"locations":[{"id":"location-1","name":"L1"}]}"""),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        var client = DiscoveryClient(handler);

        var result = await client.CaptureAsync(
            new M4DiscoveryCommand(SceneName: "OneDemo"),
            Guid.NewGuid());

        Assert.Equal(M4DiscoveryStatuses.Success, result.Status);
        var scene = Assert.Single(result.Scenes);
        Assert.Equal("scene-1", scene.SceneId);
        Assert.False(scene.Disabled);
        var robot = Assert.Single(result.Robots);
        Assert.Equal("Box-01", robot.RobotName);
        Assert.True(robot.Online);
        Assert.True(robot.Simulated);
        Assert.Equal(3, result.Endpoints.Count);
        Assert.Contains(result.Endpoints, endpoint => endpoint.Kind == "POINT" && endpoint.ExternalId == "point-1");
        Assert.Contains(result.Endpoints, endpoint => endpoint.Kind == "STORAGE_BIN" && endpoint.ExternalId == "bin-1");
        Assert.Contains(result.Endpoints, endpoint => endpoint.Kind == "LOCATION" && endpoint.ExternalId == "location-1");
        Assert.Equal(4, result.Resources.Count);
        Assert.All(handler.Requests, request =>
            Assert.DoesNotContain("/orders/", request.RequestUri!.AbsolutePath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resource_failure_is_preserved_as_partial_unknown_evidence()
    {
        var handler = new RouteHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/fleet/scenes/list" => Json(HttpStatusCode.OK,
                """[{"id":"scene-1","name":"OneDemo","disabled":false}]"""),
            "/api/fleet/robots/all-all" => Json(HttpStatusCode.ServiceUnavailable,
                """{"code":"temporarilyUnavailable","message":"retry later"}"""),
            "/api/fleet/scenes/schema/scene-1" => Json(HttpStatusCode.OK, "{}"),
            "/api/fleet/scenes/scene-1/query-all" => Json(HttpStatusCode.OK, "{}"),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        var client = DiscoveryClient(handler);

        var result = await client.CaptureAsync(
            new M4DiscoveryCommand(SceneId: "scene-1"),
            Guid.NewGuid());

        Assert.Equal(M4DiscoveryStatuses.Partial, result.Status);
        Assert.Empty(result.Robots);
        var robotResource = Assert.Single(result.Resources, resource => resource.Kind == M4DiscoveryResourceKinds.RobotList);
        Assert.False(robotResource.Success);
        Assert.Equal("M4_TRANSIENT_HTTP_FAILURE", robotResource.ErrorCode);
    }

    [Fact]
    public async Task List_only_capture_does_not_guess_a_scene()
    {
        var handler = new RouteHandler(_ => Json(HttpStatusCode.OK,
            """[{"id":"scene-1","name":"OneDemo","disabled":false}]"""));
        var client = DiscoveryClient(handler);

        var result = await client.CaptureAsync(new M4DiscoveryCommand(), Guid.NewGuid());

        Assert.Equal(M4DiscoveryStatuses.Success, result.Status);
        Assert.Single(result.Scenes);
        Assert.Empty(result.Robots);
        Assert.Empty(result.Endpoints);
        Assert.Single(handler.Requests);
        Assert.Equal("/api/fleet/scenes/list", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Nested_data_and_list_envelopes_are_projected()
    {
        var handler = new RouteHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/fleet/scenes/list" => Json(HttpStatusCode.OK,
                """{"data":{"list":[{"sceneId":"scene-1","sceneName":"OneDemo","enabled":true}]}}"""),
            "/api/fleet/robots/all-all" => Json(HttpStatusCode.OK,
                """{"data":{"robots":[{"robotId":"robot-1","robotName":"Box-01"}]}}"""),
            "/api/fleet/scenes/schema/scene-1" => Json(HttpStatusCode.OK, "{}"),
            "/api/fleet/scenes/scene-1/query-all" => Json(HttpStatusCode.OK, "{}"),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        var client = DiscoveryClient(handler);

        var result = await client.CaptureAsync(
            new M4DiscoveryCommand(SceneName: "OneDemo"),
            Guid.NewGuid());

        Assert.Equal("scene-1", Assert.Single(result.Scenes).SceneId);
        Assert.Equal("robot-1", Assert.Single(result.Robots).RobotId);
    }

    [Fact]
    public async Task Disabled_integration_is_rejected_without_recording_failed_evidence()
    {
        var handler = new RouteHandler(_ => Json(HttpStatusCode.OK, "[]"));
        var enabled = EnabledOptions();
        var disabled = enabled with { Enabled = false, ReadEnabled = false, AppId = null, AppKey = null };
        var client = DiscoveryClient(handler, disabled);

        var error = await Assert.ThrowsAsync<M4HttpException>(() =>
            client.CaptureAsync(new M4DiscoveryCommand(), Guid.NewGuid()));

        Assert.Equal("M4_DISABLED", error.Code);
        Assert.Empty(handler.Requests);
    }

    private static M4DiscoveryClient DiscoveryClient(HttpMessageHandler handler, M4Options? configuredOptions = null)
    {
        var options = configuredOptions ?? EnabledOptions();
        var authentication = new M4AuthenticationHandler(options) { InnerHandler = handler };
        var httpClient = new HttpClient(authentication)
        {
            BaseAddress = options.BaseAddress,
            Timeout = TimeSpan.FromSeconds(5)
        };
        var transport = new M4HttpClient(
            httpClient,
            options,
            TimeProvider.System,
            NullLogger<M4HttpClient>.Instance);
        return new M4DiscoveryClient(
            transport,
            options,
            TimeProvider.System,
            NullLogger<M4DiscoveryClient>.Instance);
    }

    private static M4Options EnabledOptions() => new(
            Enabled: true,
            ReadEnabled: true,
            DispatchEnabled: false,
            BaseUrl: "http://127.0.0.1:5800",
            AllowedOrigins: ["http://127.0.0.1:5800"],
            AllowInsecureRemoteHttp: false,
            TimeoutSeconds: 5,
            MaxReadRetries: 0,
            RetryBaseDelayMilliseconds: 0,
            MaxResponseBytes: 2 * 1024 * 1024,
            DiscoveryFreshnessSeconds: 300,
            AppId: "unit-app",
            AppKey: "unit-secret");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body)
    };

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(route(request));
        }
    }
}
