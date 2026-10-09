using System.Net;
using System.Net.Http.Json;
using TkgPda.Api;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class OperationalEvidenceApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Production_end_priority_starts_unset_and_does_not_invent_a_number()
    {
        await fixture.ResetAsync();
        var admin = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
        using var client = await fixture.AdminLoginClientAsync(admin);

        var initial = await client.GetFromJsonAsync<PriorityRow[]>("/api/v1/admin/production-end-priorities");
        Assert.NotNull(initial);
        Assert.Equal(4, initial.Length);
        Assert.All(initial, row => Assert.Null(row.Priority));

        var saved = await client.PutAsJsonAsync(
            "/api/v1/admin/production-end-priorities",
            new SetProductionEndPriorityHttpRequest("BRING_FULL", 40));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var rows = await saved.Content.ReadFromJsonAsync<PriorityRow[]>();
        Assert.Equal(40, Assert.Single(rows!, row => row.OperationCode == "BRING_FULL").Priority);
        Assert.Null(Assert.Single(rows!, row => row.OperationCode == "REMOVE_FULL").Priority);

        var cleared = await client.PutAsJsonAsync(
            "/api/v1/admin/production-end-priorities",
            new SetProductionEndPriorityHttpRequest("BRING_FULL", null));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM master.production_end_priorities WHERE operation_code = 'BRING_FULL'"));
    }

    [Fact]
    public async Task Unconfigured_production_end_priority_fails_closed()
    {
        await fixture.ResetAsync();
        var admin = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
        using var client = await fixture.AdminLoginClientAsync(admin);
        var response = await client.GetAsync("/api/v1/admin/production-end-priorities/BRING_EMPTY/required");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PRODUCTION_END_PRIORITY_NOT_CONFIGURED", body);
    }

    private sealed record PriorityRow(string OperationCode, int? Priority);
}
