using Xunit;

namespace TkgPda.Api.Tests;

public sealed class AltPartContainerPolicyTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Blank_alt_part_containers_become_euro_pallet_without_overwriting_explicit_values()
    {
        await fixture.ResetAsync();
        var blankCandidate = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM master.product_group_parts WHERE relation_type = 'ALT_PART' ORDER BY id LIMIT 1");
        var explicitCandidate = await fixture.ScalarAsync<Guid>(
            """
            SELECT group_part.id
            FROM master.product_group_parts group_part
            JOIN master.container_types container ON container.id = group_part.container_type_id
            WHERE group_part.relation_type = 'ALT_PART'
              AND container.code <> 'EURO_PALET'
              AND group_part.id <> @blank
            ORDER BY group_part.id
            LIMIT 1
            """,
            ("blank", blankCandidate));
        var explicitContainer = await fixture.ScalarAsync<Guid>(
            "SELECT container_type_id FROM master.product_group_parts WHERE id = @id",
            ("id", explicitCandidate));
        await fixture.ExecuteAsync(
            "UPDATE master.product_group_parts SET container_type_id = NULL WHERE id = @id",
            ("id", blankCandidate));

        var seed = SeedFile("006_blank_alt_part_euro_pallet.sql");
        await fixture.ExecuteScriptAsync(seed);
        await fixture.ExecuteScriptAsync(seed);

        Assert.Equal("EURO_PALET", await fixture.ScalarAsync<string>(
            """
            SELECT container.code
            FROM master.product_group_parts group_part
            JOIN master.container_types container ON container.id = group_part.container_type_id
            WHERE group_part.id = @id
            """,
            ("id", blankCandidate)));
        Assert.Equal(explicitContainer, await fixture.ScalarAsync<Guid>(
            "SELECT container_type_id FROM master.product_group_parts WHERE id = @id",
            ("id", explicitCandidate)));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM master.product_group_parts WHERE relation_type = 'ALT_PART' AND container_type_id IS NULL"));
    }

    private static string SeedFile(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "db", "seeds", name)))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException($"Seed file '{name}' was not found.")
            : Path.Combine(directory.FullName, "db", "seeds", name);
    }
}
