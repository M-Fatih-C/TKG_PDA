using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class M4DiscoveryApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => fixture.ResetAsync();

    [Fact]
    public async Task Capture_is_admin_only_and_persists_raw_readonly_evidence()
    {
        var operatorIdentity = await fixture.SeedIdentityAsync();
        using var operatorClient = await fixture.LoginClientAsync(operatorIdentity);
        var denied = await operatorClient.PostAsJsonAsync(
            "/api/v1/admin/fleet/discovery/sync",
            new M4DiscoveryCommand(SceneId: "scene-test", SceneName: "OneDemo"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, fixture.M4DiscoveryClient.Calls);

        await fixture.ResetAsync();
        var adminIdentity = await fixture.SeedIdentityAsync(RoleCodes.Admin, machineCode: null);
        using var adminClient = await fixture.AdminLoginClientAsync(adminIdentity);
        var captured = await adminClient.PostAsJsonAsync(
            "/api/v1/admin/fleet/discovery/sync",
            new M4DiscoveryCommand(SceneId: "scene-test", SceneName: "OneDemo"));
        captured.EnsureSuccessStatusCode();
        var summary = await captured.Content.ReadFromJsonAsync<M4DiscoverySummary>();

        Assert.NotNull(summary);
        Assert.Equal(M4DiscoveryStatuses.Success, summary.Status);
        Assert.False(summary.Stale);
        Assert.Single(summary.Scenes);
        Assert.Single(summary.Robots);
        Assert.Single(summary.Endpoints);
        Assert.Equal(1, fixture.M4DiscoveryClient.Calls);
        Assert.Equal(1, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM integration.m4_discovery_snapshots WHERE id = @id",
            ("id", summary.Id)));

        var latestResponse = await adminClient.GetAsync("/api/v1/admin/fleet/discovery/latest");
        latestResponse.EnsureSuccessStatusCode();
        var latest = await latestResponse.Content.ReadFromJsonAsync<M4DiscoverySummary>();
        Assert.Equal(summary.Id, latest?.Id);

        var detailResponse = await adminClient.GetAsync($"/api/v1/admin/fleet/discovery/captures/{summary.Id}");
        detailResponse.EnsureSuccessStatusCode();
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        Assert.Equal("scene-test", detail.RootElement.GetProperty("requestedSceneId").GetString());
        var raw = detail.RootElement.GetProperty("resources")[0].GetProperty("rawPayload");
        Assert.Equal("scene-test", raw[0].GetProperty("id").GetString());

        foreach (var resource in new[] { "scenes", "robots", "endpoints" })
        {
            var projectionResponse = await adminClient.GetAsync($"/api/v1/admin/fleet/{resource}");
            projectionResponse.EnsureSuccessStatusCode();
            using var projection = JsonDocument.Parse(await projectionResponse.Content.ReadAsStringAsync());
            Assert.Single(projection.RootElement.GetProperty("items").EnumerateArray());
        }
    }

    [Fact]
    public async Task Invalid_scope_is_rejected_before_discovery_and_missing_capture_is_not_found()
    {
        var adminIdentity = await fixture.SeedIdentityAsync(RoleCodes.Admin, machineCode: null);
        using var adminClient = await fixture.AdminLoginClientAsync(adminIdentity);

        var invalid = await adminClient.PostAsJsonAsync(
            "/api/v1/admin/fleet/discovery/sync",
            new M4DiscoveryCommand(SceneId: "bad/scene"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("M4_DISCOVERY_SCOPE_INVALID", await ApiTestFixture.ErrorCodeAsync(invalid));
        Assert.Equal(0, fixture.M4DiscoveryClient.Calls);

        var missing = await adminClient.GetAsync($"/api/v1/admin/fleet/discovery/captures/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
