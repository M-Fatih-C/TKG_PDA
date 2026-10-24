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

    [Fact]
    public async Task Area_priorities_list_only_authoritative_mappings_and_can_be_saved_and_cleared()
    {
        await fixture.ResetAsync();
        var context = await fixture.ConfigureMachineAsync();
        var part = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.parts (part_code) VALUES (@code) RETURNING id",
            ("code", "AREA-PRIORITY-" + Guid.NewGuid().ToString("N")[..8]));
        var group = await fixture.ScalarAsync<Guid>(
            "INSERT INTO master.product_groups (variant_id, main_part_id, name) VALUES (@variant, @part, 'Alan önceliği testi') RETURNING id",
            ("variant", context.VariantId),
            ("part", part));
        var slot = (await fixture.LogisticsSlotsAsync(context.LayoutId, 1))[0];
        await fixture.ExecuteAsync(
            "INSERT INTO master.layout_slot_parts (layout_slot_id, part_id) VALUES (@slot, @part) ON CONFLICT DO NOTHING",
            ("slot", slot),
            ("part", part));
        var admin = await fixture.SeedIdentityAsync(role: RoleCodes.Admin, machineCode: "SH-01-A", bindDeviceToMachine: false);
        using var client = await fixture.AdminLoginClientAsync(admin);

        var initial = await client.GetFromJsonAsync<AreaPriorityRow[]>("/api/v1/admin/production-end-area-priorities");
        var row = Assert.Single(initial!, item => item.ProductGroupId == group && item.LayoutSlotId == slot && item.PartId == part);
        Assert.Equal("MAIN_PART", row.PartRole);
        Assert.Null(row.Priority);

        var saved = await client.PutAsJsonAsync(
            "/api/v1/admin/production-end-area-priorities",
            new SetProductionEndAreaPriorityHttpRequest(group, slot, part, 12));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var savedRows = await saved.Content.ReadFromJsonAsync<AreaPriorityRow[]>();
        Assert.Equal(12, Assert.Single(savedRows!, item => item.ProductGroupId == group && item.LayoutSlotId == slot && item.PartId == part).Priority);

        var cleared = await client.PutAsJsonAsync(
            "/api/v1/admin/production-end-area-priorities",
            new SetProductionEndAreaPriorityHttpRequest(group, slot, part, null));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM master.production_end_area_priorities WHERE product_group_id=@group AND layout_slot_id=@slot AND part_id=@part",
            ("group", group),
            ("slot", slot),
            ("part", part)));

        var invalid = await client.PutAsJsonAsync(
            "/api/v1/admin/production-end-area-priorities",
            new SetProductionEndAreaPriorityHttpRequest(group, Guid.NewGuid(), part, 5));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("PRODUCTION_END_AREA_MAPPING_INVALID", await ApiTestFixture.ErrorCodeAsync(invalid));
    }

    private sealed record PriorityRow(string OperationCode, int? Priority);
    private sealed record AreaPriorityRow(Guid ProductGroupId, Guid LayoutSlotId, Guid PartId, string PartRole, int? Priority);
}
