using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using TkgPda.Api;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class FinishedGoodsAreasApiTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Nineteen_areas_keep_null_capacity_and_replay_does_not_rename_them()
    {
        await fixture.ResetAsync();
        var rows = await ReadAreasAsync();

        Assert.Equal(19, rows.Count);
        for (var index = 0; index < 19; index++)
        {
            var number = index + 1;
            Assert.Equal($"FG_AREA_{number:00}", rows[index].Code);
            Assert.Equal($"Alan {number}", rows[index].DisplayName);
            Assert.True(rows[index].CapacityUnknown);
            Assert.True(rows[index].Enabled);
        }

        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM ops.store_location_delivery_slots d
            JOIN ops.store_locations l ON l.id = d.store_location_id
            WHERE l.code LIKE 'FG_AREA_%'
            """));

        await fixture.ExecuteAsync(
            "UPDATE ops.store_locations SET display_name = @name WHERE code = 'FG_AREA_01'",
            ("name", "Müşteri Gerçek Alan"));
        await fixture.ExecuteAsync(ReadMigration("015_finished_goods_areas.sql"));

        Assert.Equal(19L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ops.store_locations WHERE code LIKE 'FG_AREA_%'"));
        Assert.Equal("Müşteri Gerçek Alan", await fixture.ScalarAsync<string>(
            "SELECT display_name FROM ops.store_locations WHERE code = 'FG_AREA_01'"));
        Assert.Equal("Alan 2", await fixture.ScalarAsync<string>(
            "SELECT display_name FROM ops.store_locations WHERE code = 'FG_AREA_02'"));

        var identity = await fixture.SeedIdentityAsync(
            role: RoleCodes.FinishedGoodsWarehouseOperator,
            machineCode: null);
        using var client = await fixture.LoginClientAsync(identity);
        var response = await client.GetAsync("/api/v1/warehouse/store/locations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var codes = document.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("code").GetString())
            .Where(code => code?.StartsWith("FG_AREA_", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(Enumerable.Range(1, 19).Select(number => $"FG_AREA_{number:00}").ToArray(), codes);
        var sample = document.RootElement.EnumerateArray().First(item => item.GetProperty("code").GetString() == "FG_AREA_02");
        Assert.Equal(JsonValueKind.Null, sample.GetProperty("capacity").ValueKind);
        Assert.True(sample.GetProperty("isEnabled").GetBoolean());
    }

    private async Task<List<AreaRow>> ReadAreasAsync()
    {
        var rows = new List<AreaRow>();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT code, display_name, capacity IS NULL, is_enabled
            FROM ops.store_locations
            WHERE code LIKE 'FG_AREA_%'
            ORDER BY code
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new AreaRow(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        }

        return rows;
    }

    private static string ReadMigration(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "db", "migrations", fileName)))
        {
            dir = dir.Parent;
        }

        if (dir is null) throw new InvalidOperationException($"Could not find {fileName}.");
        return File.ReadAllText(Path.Combine(dir.FullName, "db", "migrations", fileName));
    }

    private sealed record AreaRow(string Code, string DisplayName, bool CapacityUnknown, bool Enabled);
}
