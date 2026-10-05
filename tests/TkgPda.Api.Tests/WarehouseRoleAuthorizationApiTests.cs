using System.Net;
using TkgPda.Api;
using Xunit;

namespace TkgPda.Api.Tests;

/// <summary>
/// WP-02: Warehouse Operator Role Split and Endpoint Authorization Tests.
/// Validates authorization rules across SEMI_FINISHED_WAREHOUSE_OPERATOR,
/// FINISHED_GOODS_WAREHOUSE_OPERATOR, LINE_OPERATOR, ADMIN, SUPERVISOR,
/// and legacy WAREHOUSE_OPERATOR.
/// </summary>
public sealed class WarehouseRoleAuthorizationApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    private const string PreparationAreasUrl = "/api/v1/warehouse/preparation/areas";
    private const string FinishedGoodsLocationsUrl = "/api/v1/warehouse/store/locations";

    [Fact]
    public async Task Semi_finished_warehouse_operator_allowed_on_preparation_and_forbidden_on_finished_goods()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.SemiFinishedWarehouseOperator);
        using var client = await fixture.LoginClientAsync(identity);

        // Preparation areas -> Allowed (200 OK)
        var prepResponse = await client.GetAsync(PreparationAreasUrl);
        Assert.Equal(HttpStatusCode.OK, prepResponse.StatusCode);

        // Finished goods store -> Forbidden (403)
        var storeResponse = await client.GetAsync(FinishedGoodsLocationsUrl);
        Assert.Equal(HttpStatusCode.Forbidden, storeResponse.StatusCode);
        Assert.Equal("ROLE_NOT_AUTHORIZED", await ApiTestFixture.ErrorCodeAsync(storeResponse));
    }

    [Fact]
    public async Task Finished_goods_warehouse_operator_allowed_on_finished_goods_and_forbidden_on_preparation()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.FinishedGoodsWarehouseOperator);
        using var client = await fixture.LoginClientAsync(identity);

        // Finished goods store -> Allowed (200 OK)
        var storeResponse = await client.GetAsync(FinishedGoodsLocationsUrl);
        Assert.Equal(HttpStatusCode.OK, storeResponse.StatusCode);

        // Preparation areas -> Forbidden (403)
        var prepResponse = await client.GetAsync(PreparationAreasUrl);
        Assert.Equal(HttpStatusCode.Forbidden, prepResponse.StatusCode);
        Assert.Equal("ROLE_NOT_AUTHORIZED", await ApiTestFixture.ErrorCodeAsync(prepResponse));
    }

    [Fact]
    public async Task Line_operator_is_forbidden_from_both_warehouse_endpoints()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.LineOperator);
        using var client = await fixture.LoginClientAsync(identity);

        // Preparation areas -> Forbidden (403)
        var prepResponse = await client.GetAsync(PreparationAreasUrl);
        Assert.Equal(HttpStatusCode.Forbidden, prepResponse.StatusCode);
        Assert.Equal("ROLE_NOT_AUTHORIZED", await ApiTestFixture.ErrorCodeAsync(prepResponse));

        // Finished goods store -> Forbidden (403)
        var storeResponse = await client.GetAsync(FinishedGoodsLocationsUrl);
        Assert.Equal(HttpStatusCode.Forbidden, storeResponse.StatusCode);
        Assert.Equal("ROLE_NOT_AUTHORIZED", await ApiTestFixture.ErrorCodeAsync(storeResponse));
    }

    [Fact]
    public async Task Admin_and_supervisor_can_access_both_warehouse_endpoints()
    {
        await fixture.ResetAsync();
        var admin = await fixture.SeedIdentityAsync(role: RoleCodes.Admin);
        using var adminClient = await fixture.LoginClientAsync(admin);

        var adminPrep = await adminClient.GetAsync(PreparationAreasUrl);
        Assert.Equal(HttpStatusCode.OK, adminPrep.StatusCode);
        var adminStore = await adminClient.GetAsync(FinishedGoodsLocationsUrl);
        Assert.Equal(HttpStatusCode.OK, adminStore.StatusCode);

        var supervisor = await fixture.SeedIdentityAsync(role: RoleCodes.Supervisor);
        using var supervisorClient = await fixture.LoginClientAsync(supervisor);

        var supPrep = await supervisorClient.GetAsync(PreparationAreasUrl);
        Assert.Equal(HttpStatusCode.OK, supPrep.StatusCode);
        var supStore = await supervisorClient.GetAsync(FinishedGoodsLocationsUrl);
        Assert.Equal(HttpStatusCode.OK, supStore.StatusCode);
    }

    [Fact]
    public async Task Legacy_warehouse_operator_can_access_both_warehouse_endpoints()
    {
        await fixture.ResetAsync();
        var identity = await fixture.SeedIdentityAsync(role: RoleCodes.WarehouseOperator);
        using var client = await fixture.LoginClientAsync(identity);

        // Backward compatibility: legacy WAREHOUSE_OPERATOR has access to both
        var prepResponse = await client.GetAsync(PreparationAreasUrl);
        Assert.Equal(HttpStatusCode.OK, prepResponse.StatusCode);

        var storeResponse = await client.GetAsync(FinishedGoodsLocationsUrl);
        Assert.Equal(HttpStatusCode.OK, storeResponse.StatusCode);
    }
}
