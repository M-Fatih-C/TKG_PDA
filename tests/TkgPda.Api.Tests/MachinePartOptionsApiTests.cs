using System.Net;
using System.Net.Http.Json;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Api.Tests;

// WP-03A — machine-level "Main Part -> Related/Alt Part" projection, derived
// only from existing normalized master data (master.product_groups /
// master.product_group_parts), deduplicated across the machine's variants.
public sealed class MachinePartOptionsApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    private const string MachineCode = "SH-01-A";
    private const string OtherMachineCode = "SH-01-B";

    [Fact]
    public async Task Machine_returns_its_main_part_options()
    {
        var scene = await SeedSceneAsync();

        var options = await QueryAsync(scene.MachineId);

        Assert.Contains(options, o => o.PartId == scene.MainPartId);
        Assert.Contains(options, o => o.PartId == scene.OrphanMainPartId);
    }

    [Fact]
    public async Task Related_parts_appear_under_the_correct_main_part()
    {
        var scene = await SeedSceneAsync();

        var options = await QueryAsync(scene.MachineId);

        var main = options.Single(o => o.PartId == scene.MainPartId);
        Assert.Contains(main.RelatedParts, p => p.PartId == scene.RelatedPartId && p.RelationType == "RELATED");
    }

    [Fact]
    public async Task Alt_parts_appear_under_the_correct_main_part()
    {
        var scene = await SeedSceneAsync();

        var options = await QueryAsync(scene.MachineId);

        var main = options.Single(o => o.PartId == scene.MainPartId);
        Assert.Contains(main.RelatedParts, p => p.PartId == scene.AltPartId && p.RelationType == "ALT_PART");
    }

    [Fact]
    public async Task Relationships_across_multiple_variants_are_unioned()
    {
        var scene = await SeedSceneAsync();

        var options = await QueryAsync(scene.MachineId);

        var main = options.Single(o => o.PartId == scene.MainPartId);
        // RelatedPartId is only linked in variant 1's product group; AltPartId
        // only in variant 2's — both must be present in the single unioned entry.
        Assert.Contains(main.RelatedParts, p => p.PartId == scene.RelatedPartId);
        Assert.Contains(main.RelatedParts, p => p.PartId == scene.AltPartId);
    }

    [Fact]
    public async Task Duplicate_relationships_across_variants_are_deduplicated()
    {
        var scene = await SeedSceneAsync();

        var options = await QueryAsync(scene.MachineId);

        var main = options.Single(o => o.PartId == scene.MainPartId);
        // RelatedPartId is linked to MainPartId from both variant 1 and
        // variant 2's product groups; it must appear exactly once.
        Assert.Single(main.RelatedParts, p => p.PartId == scene.RelatedPartId);
        // The main-part list itself must not duplicate MainPartId either,
        // even though it is the main part of two different product groups.
        Assert.Single(options, o => o.PartId == scene.MainPartId);
    }

    [Fact]
    public async Task Another_machines_data_does_not_leak()
    {
        var scene = await SeedSceneAsync();

        var options = await QueryAsync(scene.MachineId);

        Assert.DoesNotContain(options, o => o.PartId == scene.OtherMachineMainPartId);
        Assert.DoesNotContain(
            options.SelectMany(o => o.RelatedParts),
            p => p.PartId == scene.OtherMachineMainPartId);
    }

    [Fact]
    public async Task Unauthorized_machine_is_rejected()
    {
        var scene = await SeedSceneAsync();
        var identity = await fixture.SeedIdentityAsync(machineCode: null);
        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.GetAsync($"/api/v1/machines/{scene.MachineId}/part-options");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var errorCode = await ApiTestFixture.ErrorCodeAsync(response);
        Assert.True(errorCode is "MACHINE_ACCESS_DENIED" or "DEVICE_MACHINE_BINDING_REQUIRED");
    }

    [Fact]
    public async Task Main_part_with_no_related_parts_returns_an_empty_list()
    {
        var scene = await SeedSceneAsync();

        var options = await QueryAsync(scene.MachineId);

        var orphan = options.Single(o => o.PartId == scene.OrphanMainPartId);
        Assert.Empty(orphan.RelatedParts);
    }

    [Fact]
    public async Task Ordering_is_deterministic()
    {
        var scene = await SeedSceneAsync();

        var first = await QueryAsync(scene.MachineId);
        var second = await QueryAsync(scene.MachineId);

        // The endpoint is machine-wide, so real customer master data for
        // SH-01-A is legitimately present too; scope the ordering assertion
        // to this scene's own (freshly generated, never-colliding) part IDs.
        Assert.Equal(first.Select(o => o.PartId), second.Select(o => o.PartId));
        var knownMainPartIds = new HashSet<Guid> { scene.MainPartId, scene.OrphanMainPartId };
        Assert.Equal(
            new[] { scene.MainPartId, scene.OrphanMainPartId },
            first.Where(o => knownMainPartIds.Contains(o.PartId)).Select(o => o.PartId).ToArray());

        var main = first.Single(o => o.PartId == scene.MainPartId);
        Assert.Equal(
            new[] { scene.AltPartId, scene.DuplicatedNonMainPartId, scene.RelatedPartId },
            main.RelatedParts.Select(p => p.PartId).ToArray());
    }

    private async Task<List<MachineMainPartOptionRecord>> QueryAsync(Guid machineId)
    {
        var identity = await fixture.SeedIdentityAsync(machineCode: MachineCode);
        using var client = await fixture.LoginClientAsync(identity);

        var response = await client.GetAsync($"/api/v1/machines/{machineId}/part-options");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<MachineMainPartOptionRecord>>()
            ?? throw new InvalidOperationException("Machine part-options response was empty.");
    }

    private sealed record Scene(
        Guid MachineId,
        Guid MainPartId,
        Guid OrphanMainPartId,
        Guid RelatedPartId,
        Guid AltPartId,
        Guid DuplicatedNonMainPartId,
        Guid OtherMachineMainPartId);

    private async Task<Scene> SeedSceneAsync()
    {
        await fixture.ResetAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var machineId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM master.machines WHERE code = @code",
            ("code", MachineCode));
        var otherMachineId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM master.machines WHERE code = @code",
            ("code", OtherMachineCode));

        var v1 = await InsertVariantAsync(machineId, $"P03A-{suffix}-V1", 10);
        var v2 = await InsertVariantAsync(machineId, $"P03A-{suffix}-V2", 20);
        var otherVariant = await InsertVariantAsync(otherMachineId, $"P03A-{suffix}-OTHER", 10);

        var mainPart = await InsertPartAsync($"P03A-{suffix}-MAIN");
        var orphanMainPart = await InsertPartAsync($"P03A-{suffix}-ORPHAN-MAIN");
        var relatedPart = await InsertPartAsync($"P03A-{suffix}-REL");
        var altPart = await InsertPartAsync($"P03A-{suffix}-ALT");
        var duplicatedNonMainPart = await InsertPartAsync($"P03A-{suffix}-DUPCHECK");
        var otherMachineMainPart = await InsertPartAsync($"P03A-{suffix}-LEAK-MAIN");

        // MainPart is the main part of a product group in BOTH v1 and v2:
        // the machine-level projection must still show it once (dedup), with
        // its related/alt parts unioned across both variants.
        var pgV1 = await InsertProductGroupAsync(v1, mainPart);
        await InsertGroupPartAsync(pgV1, relatedPart, "RELATED");
        await InsertGroupPartAsync(pgV1, duplicatedNonMainPart, "RELATED");

        var pgV2 = await InsertProductGroupAsync(v2, mainPart);
        await InsertGroupPartAsync(pgV2, relatedPart, "RELATED"); // duplicate of pgV1's link
        await InsertGroupPartAsync(pgV2, altPart, "ALT_PART");

        // A main part with no children at all -> empty RelatedParts, not absence.
        await InsertProductGroupAsync(v1, orphanMainPart);

        // Different machine entirely -> must never surface in this machine's projection.
        await InsertProductGroupAsync(otherVariant, otherMachineMainPart);

        return new Scene(machineId, mainPart, orphanMainPart, relatedPart, altPart, duplicatedNonMainPart, otherMachineMainPart);
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
