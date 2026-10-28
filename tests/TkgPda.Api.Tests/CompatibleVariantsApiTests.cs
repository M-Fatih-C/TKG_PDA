using System.Net;
using System.Net.Http.Json;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Api.Tests;

// WP-02 — machine + part -> compatible variant projection, derived only from
// existing normalized master data (master.variants / product_groups /
// product_group_parts). No Flutter/RDS involvement.
public sealed class CompatibleVariantsApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    private const string MachineCode = "SH-01-A";

    [Fact]
    public async Task Customer_seed_main_part_resolves_in_its_parent_context()
    {
        await fixture.ResetAsync();
        var machineId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM master.machines WHERE code = @machine_code",
            ("machine_code", MachineCode));
        var variantId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM master.variants WHERE machine_id = @machine_id AND code = '1'",
            ("machine_id", machineId));
        var mainPartId = await fixture.ScalarAsync<Guid>(
            """
            SELECT main_part_id
            FROM master.product_groups
            WHERE variant_id = @variant_id AND is_active
            ORDER BY display_order
            LIMIT 1
            """,
            ("variant_id", variantId));

        var variants = await QueryAsync(machineId, mainPartId, mainPartId);

        Assert.Contains(variants, item => item.VariantId == variantId);
    }

    [Fact]
    public async Task Main_part_resolves_its_variant()
    {
        var scene = await SeedSceneAsync();

        var variants = await QueryAsync(scene.MachineId, scene.MainPartId, scene.MainPartId);

        var only = Assert.Single(variants);
        Assert.Equal(scene.V1Id, only.VariantId);
        Assert.Equal("MAIN", only.RelationType);
    }

    [Fact]
    public async Task Related_part_resolves_its_variant()
    {
        var scene = await SeedSceneAsync();

        var variants = await QueryAsync(scene.MachineId, scene.MainPartId, scene.RelatedPartId);

        var only = Assert.Single(variants);
        Assert.Equal(scene.V1Id, only.VariantId);
        Assert.Equal(scene.MainGroupId, only.ProductGroupId);
        Assert.Equal("RELATED", only.RelationType);
    }

    [Fact]
    public async Task Alt_part_resolves_its_variant()
    {
        var scene = await SeedSceneAsync();

        var variants = await QueryAsync(scene.MachineId, scene.MainPartId, scene.AltPartId);

        var only = Assert.Single(variants);
        Assert.Equal(scene.V1Id, only.VariantId);
        Assert.Equal("ALT_PART", only.RelationType);
    }

    [Fact]
    public async Task Main_part_used_by_multiple_variants_returns_all_contextual_variants()
    {
        var scene = await SeedSceneAsync();

        var variants = await QueryAsync(scene.MachineId, scene.SharedPartId, scene.SharedPartId);

        Assert.Equal(
            new[] { scene.V2Id, scene.V3Id },
            variants.Select(v => v.VariantId).ToArray());
    }

    [Fact]
    public async Task Child_relationship_does_not_escape_the_selected_main_part_context()
    {
        var scene = await SeedSceneAsync();

        var variants = await QueryAsync(scene.MachineId, scene.MainPartId, scene.SharedPartId);

        var only = Assert.Single(variants);
        Assert.Equal(scene.V1Id, only.VariantId);
        Assert.Equal(scene.MainGroupId, only.ProductGroupId);
        Assert.Equal("RELATED", only.RelationType);
    }

    [Fact]
    public async Task Duplicated_child_under_the_selected_main_returns_only_that_relationship()
    {
        var scene = await SeedSceneAsync();

        var variants = await QueryAsync(scene.MachineId, scene.MainPartId, scene.DuplicatedPartId);

        var only = Assert.Single(variants);
        Assert.Equal(scene.V1Id, only.VariantId);
        Assert.Equal(scene.MainGroupId, only.ProductGroupId);
        Assert.Equal("RELATED", only.RelationType);
    }

    [Fact]
    public async Task Same_child_under_another_main_preserves_that_product_group()
    {
        var scene = await SeedSceneAsync();

        var variants = await QueryAsync(scene.MachineId, scene.SecondMainPartId, scene.DuplicatedPartId);

        var only = Assert.Single(variants);
        Assert.Equal(scene.V1Id, only.VariantId);
        Assert.Equal(scene.SecondGroupId, only.ProductGroupId);
        Assert.Equal("ALT_PART", only.RelationType);
    }

    [Fact]
    public async Task Part_with_no_mapping_returns_an_empty_list()
    {
        var scene = await SeedSceneAsync();

        var variants = await QueryAsync(scene.MachineId, scene.MainPartId, scene.OrphanPartId);

        Assert.Empty(variants);
    }

    [Fact]
    public async Task Missing_main_part_context_is_rejected_explicitly()
    {
        var scene = await SeedSceneAsync();
        var identity = await fixture.SeedIdentityAsync(machineCode: MachineCode);
        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.GetAsync(
            $"/api/v1/machines/{scene.MachineId}/parts/{scene.RelatedPartId}/compatible-variants");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MAIN_PART_CONTEXT_REQUIRED", await ApiTestFixture.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Unauthorized_machine_is_rejected()
    {
        var scene = await SeedSceneAsync();
        var identity = await fixture.SeedIdentityAsync(machineCode: null);
        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.GetAsync(
            $"/api/v1/machines/{scene.MachineId}/parts/{scene.MainPartId}/compatible-variants?mainPartId={scene.MainPartId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var errorCode = await ApiTestFixture.ErrorCodeAsync(response);
        Assert.True(errorCode is "MACHINE_ACCESS_DENIED" or "DEVICE_MACHINE_BINDING_REQUIRED");
    }

    [Fact]
    public async Task Authorized_machine_returns_the_compatible_variants()
    {
        var scene = await SeedSceneAsync();

        var variants = await QueryAsync(scene.MachineId, scene.MainPartId, scene.MainPartId);

        Assert.NotEmpty(variants);
    }

    [Fact]
    public async Task Ordering_is_deterministic_by_display_order()
    {
        var scene = await SeedSceneAsync();

        var first = await QueryAsync(scene.MachineId, scene.SharedPartId, scene.SharedPartId);
        var second = await QueryAsync(scene.MachineId, scene.SharedPartId, scene.SharedPartId);

        Assert.Equal(
            first.Select(v => v.VariantId).ToArray(),
            second.Select(v => v.VariantId).ToArray());
        Assert.Equal(new[] { scene.V2Id, scene.V3Id }, first.Select(v => v.VariantId).ToArray());
    }

    private async Task<List<CompatibleVariantRecord>> QueryAsync(Guid machineId, Guid mainPartId, Guid partId)
    {
        var identity = await fixture.SeedIdentityAsync(machineCode: MachineCode);
        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.GetAsync($"/api/v1/machines/{machineId}/parts/{partId}/compatible-variants?mainPartId={mainPartId}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<CompatibleVariantRecord>>()
            ?? throw new InvalidOperationException("Compatible-variants response was empty.");
    }

    private sealed record Scene(
        Guid MachineId,
        Guid V1Id,
        Guid V2Id,
        Guid V3Id,
        Guid MainPartId,
        Guid MainGroupId,
        Guid SecondMainPartId,
        Guid SecondGroupId,
        Guid RelatedPartId,
        Guid AltPartId,
        Guid SharedPartId,
        Guid DuplicatedPartId,
        Guid OrphanPartId);

    private async Task<Scene> SeedSceneAsync()
    {
        await fixture.ResetAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var machineId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM master.machines WHERE code = @code",
            ("code", MachineCode));

        var v1 = await InsertVariantAsync(machineId, $"P02-{suffix}-V1", 10);
        var v2 = await InsertVariantAsync(machineId, $"P02-{suffix}-V2", 5);
        var v3 = await InsertVariantAsync(machineId, $"P02-{suffix}-V3", 20);

        var mainPart = await InsertPartAsync($"P02-{suffix}-MAIN");
        var relatedPart = await InsertPartAsync($"P02-{suffix}-REL");
        var altPart = await InsertPartAsync($"P02-{suffix}-ALT");
        var sharedPart = await InsertPartAsync($"P02-{suffix}-SHARED");
        var duplicatedPart = await InsertPartAsync($"P02-{suffix}-DUP");
        var orphanPart = await InsertPartAsync($"P02-{suffix}-ORPHAN");
        var pg1SecondMain = await InsertPartAsync($"P02-{suffix}-PG1B-MAIN");

        // V1: one product group carries MAIN plus RELATED/ALT/duplicated children;
        // a second product group in the same variant carries the duplicated part
        // under a different relation, proving parent-main context is preserved.
        var pg1 = await InsertProductGroupAsync(v1, mainPart);
        await InsertGroupPartAsync(pg1, relatedPart, "RELATED");
        await InsertGroupPartAsync(pg1, altPart, "ALT_PART");
        await InsertGroupPartAsync(pg1, duplicatedPart, "RELATED");
        await InsertGroupPartAsync(pg1, sharedPart, "RELATED");

        var pg1B = await InsertProductGroupAsync(v1, pg1SecondMain);
        await InsertGroupPartAsync(pg1B, duplicatedPart, "ALT_PART");

        // V2 and V3 each expose the shared part as their own product group's main part.
        await InsertProductGroupAsync(v2, sharedPart);
        await InsertProductGroupAsync(v3, sharedPart);

        return new Scene(
            machineId,
            v1,
            v2,
            v3,
            mainPart,
            pg1,
            pg1SecondMain,
            pg1B,
            relatedPart,
            altPart,
            sharedPart,
            duplicatedPart,
            orphanPart);
    }

    private Task<Guid> InsertVariantAsync(Guid machineId, string code, int displayOrder) => fixture.ScalarAsync<Guid>(
        """
        INSERT INTO master.variants (machine_id, code, name, display_order)
        VALUES (@machine_id, @code, @code, @display_order)
        RETURNING id
        """,
        ("machine_id", machineId),
        ("code", code),
        ("display_order", displayOrder));

    private Task<Guid> InsertPartAsync(string code) => fixture.ScalarAsync<Guid>(
        "INSERT INTO master.parts (part_code) VALUES (@code) RETURNING id",
        ("code", code));

    private Task<Guid> InsertProductGroupAsync(Guid variantId, Guid mainPartId) => fixture.ScalarAsync<Guid>(
        """
        INSERT INTO master.product_groups (variant_id, main_part_id)
        VALUES (@variant_id, @main_part_id)
        RETURNING id
        """,
        ("variant_id", variantId),
        ("main_part_id", mainPartId));

    private Task InsertGroupPartAsync(Guid productGroupId, Guid partId, string relationType) => fixture.ExecuteAsync(
        """
        INSERT INTO master.product_group_parts (product_group_id, part_id, relation_type)
        VALUES (@product_group_id, @part_id, @relation_type)
        """,
        ("product_group_id", productGroupId),
        ("part_id", partId),
        ("relation_type", relationType));
}
