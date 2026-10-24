using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;

namespace TkgPda.Infrastructure.Postgres;

public sealed class PostgresM4DiscoveryStore(NpgsqlDataSource dataSource) : IM4DiscoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SaveAsync(M4DiscoverySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO integration.m4_discovery_snapshots (
                id,
                correlation_id,
                status,
                requested_scene_id,
                requested_scene_name,
                captured_at,
                fresh_until,
                snapshot_json
            )
            VALUES (@id, @correlationId, @status, @sceneId, @sceneName, @capturedAt, @freshUntil, @snapshotJson)
            ON CONFLICT (id) DO NOTHING;
            """,
            connection);
        command.Parameters.AddWithValue("id", snapshot.Id);
        command.Parameters.AddWithValue("correlationId", snapshot.CorrelationId);
        command.Parameters.AddWithValue("status", snapshot.Status);
        command.Parameters.AddWithValue("sceneId", (object?)snapshot.RequestedSceneId ?? DBNull.Value);
        command.Parameters.AddWithValue("sceneName", (object?)snapshot.RequestedSceneName ?? DBNull.Value);
        command.Parameters.AddWithValue("capturedAt", snapshot.CapturedAt);
        command.Parameters.AddWithValue("freshUntil", snapshot.FreshUntil);
        command.Parameters.Add("snapshotJson", NpgsqlDbType.Jsonb).Value = json;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<M4DiscoverySnapshot?> GetLatestAsync(CancellationToken cancellationToken = default) =>
        ReadSingleAsync(
            """
            SELECT snapshot_json::text
            FROM integration.m4_discovery_snapshots
            ORDER BY captured_at DESC, id DESC
            LIMIT 1;
            """,
            id: null,
            cancellationToken);

    public Task<M4DiscoverySnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ReadSingleAsync(
            """
            SELECT snapshot_json::text
            FROM integration.m4_discovery_snapshots
            WHERE id = @id;
            """,
            id,
            cancellationToken);

    private async Task<M4DiscoverySnapshot?> ReadSingleAsync(
        string sql,
        Guid? id,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        if (id.HasValue)
        {
            command.Parameters.AddWithValue("id", id.Value);
        }

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json
            ? JsonSerializer.Deserialize<M4DiscoverySnapshot>(json, JsonOptions)
            : null;
    }
}
