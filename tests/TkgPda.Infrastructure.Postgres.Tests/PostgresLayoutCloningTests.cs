using Npgsql;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Infrastructure.Postgres.Tests;

public sealed class PostgresLayoutCloningTests
{
    private readonly string _connectionString = Environment.GetEnvironmentVariable("TKG_PDA_TEST_CONNECTION_STRING") ?? string.Empty;

    [PostgresFact]
    public async Task CreateLayoutVersionAsync_preserves_authoritative_layout_slot_parts_relationships()
    {
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        var store = new PostgresAdminConfigurationStore(dataSource);

        // Find an active layout that has slots with layout_slot_parts
        await using var connection = await dataSource.OpenConnectionAsync();
        Guid sourceLayoutId;
        long sourceXmin;
        int sourceBusinessVersion;
        int expectedSlotPartsCount;

        await using (var findCmd = new NpgsqlCommand(
            """
            SELECT l.id, (l.xmin::text)::bigint, l.version, count(lsp.part_id) as part_count
            FROM master.layouts l
            JOIN master.layout_slots ls ON ls.layout_id = l.id
            JOIN master.layout_slot_parts lsp ON lsp.layout_slot_id = ls.id
            WHERE l.status = 'PUBLISHED'
            GROUP BY l.id, l.xmin, l.version
            HAVING count(lsp.part_id) > 0
            LIMIT 1
            """,
            connection))
        {
            await using var reader = await findCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                // No published layout with slot parts in this DB run
                return;
            }

            sourceLayoutId = reader.GetGuid(0);
            sourceXmin = reader.GetInt64(1);
            sourceBusinessVersion = reader.GetInt32(2);
            expectedSlotPartsCount = Convert.ToInt32(reader.GetInt64(3));
        }

        Guid actorUserId;
        await using (var userCmd = new NpgsqlCommand("SELECT id FROM auth.users LIMIT 1", connection))
        {
            if (await userCmd.ExecuteScalarAsync() is not Guid userId)
            {
                return;
            }

            actorUserId = userId;
        }

        var context = new AdminMutationContext(
            actorUserId,
            Guid.NewGuid(),
            "127.0.0.1",
            "Cloning test");

        // Ensure no conflicting draft exists before testing
        await using (var cleanDraft = new NpgsqlCommand(
            """
            DELETE FROM master.layouts
            WHERE machine_id = (SELECT machine_id FROM master.layouts WHERE id = @id)
              AND code = (SELECT code FROM master.layouts WHERE id = @id)
              AND status = 'DRAFT'
            """,
            connection))
        {
            cleanDraft.Parameters.AddWithValue("id", sourceLayoutId);
            await cleanDraft.ExecuteNonQueryAsync();
        }

        AdminLayoutRecord cloned;
        try
        {
            cloned = await store.CreateLayoutVersionAsync(sourceLayoutId, sourceXmin, context);
        }
        catch (Exception ex) when (ex.Message.Contains("DRAFT_LAYOUT_ALREADY_EXISTS"))
        {
            return;
        }

        Assert.NotNull(cloned);
        Assert.Equal("DRAFT", cloned.Status);
        Assert.True(cloned.Version > sourceBusinessVersion);

        // Verify relational mapping in cloned layout
        await using (var verifyCmd = new NpgsqlCommand(
            """
            SELECT count(lsp.part_id)
            FROM master.layout_slots ls
            JOIN master.layout_slot_parts lsp ON lsp.layout_slot_id = ls.id
            WHERE ls.layout_id = @cloned_id
            """,
            connection))
        {
            verifyCmd.Parameters.AddWithValue("cloned_id", cloned.Id);
            var clonedSlotPartsCount = Convert.ToInt32(await verifyCmd.ExecuteScalarAsync());
            Assert.Equal(expectedSlotPartsCount, clonedSlotPartsCount);
        }

        // Clean up created draft
        await using (var delCmd = new NpgsqlCommand(
            "DELETE FROM master.layouts WHERE id = @id",
            connection))
        {
            delCmd.Parameters.AddWithValue("id", cloned.Id);
            await delCmd.ExecuteNonQueryAsync();
        }
    }
}
