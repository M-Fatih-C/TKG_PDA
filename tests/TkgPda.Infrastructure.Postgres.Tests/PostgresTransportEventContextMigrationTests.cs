using Npgsql;
using Xunit;

namespace TkgPda.Infrastructure.Postgres.Tests;

/// <summary>
/// WP-06 migration 014 (ops.transport_request_events.device_id / correlation_id).
/// The pre-014 shape is recreated inside one transaction that is always rolled
/// back, so the shared test database is never left altered.
/// </summary>
public sealed class PostgresTransportEventContextMigrationTests
{
    private readonly string _connectionString =
        Environment.GetEnvironmentVariable("TKG_PDA_TEST_CONNECTION_STRING") ?? string.Empty;

    [PostgresFact]
    public async Task Migration_014_applies_to_existing_history_without_loss_and_replays_cleanly()
    {
        var migrationSql = await File.ReadAllTextAsync(LocateMigration());
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // A historical request with events written before the context columns existed.
        var requestId = await ScalarAsync<Guid>(
            connection, transaction,
            """
            INSERT INTO ops.transport_requests (request_origin, machine_id, variant_id, layout_id, task_type_id)
            SELECT 'API', m.id, v.id, l.id, (SELECT id FROM master.task_types ORDER BY code LIMIT 1)
            FROM master.machines m
            JOIN master.variants v ON v.machine_id = m.id
            JOIN master.variant_layout_assignments a ON a.variant_id = v.id AND a.machine_id = m.id
            JOIN master.layouts l ON l.id = a.layout_id AND l.machine_id = m.id
            ORDER BY m.code, v.code
            LIMIT 1
            RETURNING id
            """);
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO ops.transport_request_events (transport_request_id, event_type, new_status, message, data_json)
            VALUES
                ('@@REQUEST@@', 'REQUEST_CREATED', 'CREATED', 'historical create', '{"origin":"legacy"}'::jsonb),
                ('@@REQUEST@@', 'VALIDATION_STARTED', 'VALIDATING', 'historical validate', '{}'::jsonb)
            """.Replace("@@REQUEST@@", requestId.ToString()));
        await ExecuteAsync(
            connection, transaction,
            "ALTER TABLE ops.transport_request_events DROP COLUMN device_id, DROP COLUMN correlation_id");
        Assert.Equal(0, await ColumnCountAsync(connection, transaction));

        // First apply onto the populated table.
        await ExecuteAsync(connection, transaction, migrationSql);
        Assert.Equal(2, await ColumnCountAsync(connection, transaction));
        Assert.Equal(2, await ScalarAsync<long>(
            connection, transaction,
            $"""
            SELECT count(*) FROM ops.transport_request_events
            WHERE transport_request_id = '{requestId}' AND device_id IS NULL AND correlation_id IS NULL
            """));
        Assert.Equal("legacy", await ScalarAsync<string>(
            connection, transaction,
            $"""
            SELECT data_json ->> 'origin' FROM ops.transport_request_events
            WHERE transport_request_id = '{requestId}' AND event_type = 'REQUEST_CREATED'
            """));
        Assert.Equal(3, await ScalarAsync<long>(
            connection, transaction,
            """
            SELECT count(*) FROM pg_indexes
            WHERE schemaname = 'ops' AND tablename = 'transport_request_events'
              AND indexname IN ('ix_transport_events_type_created', 'ix_transport_events_correlation_id', 'ix_transport_events_device_created')
            """));

        // New rows can carry context; historical rows stay untouched on replay.
        var correlationId = Guid.NewGuid();
        await ExecuteAsync(
            connection, transaction,
            $"""
            INSERT INTO ops.transport_request_events (transport_request_id, event_type, correlation_id)
            VALUES ('{requestId}', 'ROBOT_TASK_DISPATCHED', '{correlationId}')
            """);
        await ExecuteAsync(connection, transaction, migrationSql);
        Assert.Equal(3, await ScalarAsync<long>(
            connection, transaction,
            $"SELECT count(*) FROM ops.transport_request_events WHERE transport_request_id = '{requestId}'"));
        Assert.Equal(2, await ScalarAsync<long>(
            connection, transaction,
            $"""
            SELECT count(*) FROM ops.transport_request_events
            WHERE transport_request_id = '{requestId}' AND device_id IS NULL AND correlation_id IS NULL
            """));
        Assert.Equal(correlationId, await ScalarAsync<Guid>(
            connection, transaction,
            $"""
            SELECT correlation_id FROM ops.transport_request_events
            WHERE transport_request_id = '{requestId}' AND event_type = 'ROBOT_TASK_DISPATCHED'
            """));

        await transaction.RollbackAsync();
    }

    private static async Task<long> ColumnCountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction) =>
        await ScalarAsync<long>(
            connection, transaction,
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'ops' AND table_name = 'transport_request_events'
              AND column_name IN ('device_id', 'correlation_id')
            """);

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static string LocateMigration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "db", "migrations", "014_transport_event_context.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("db/migrations/014_transport_event_context.sql was not found above the test output directory.");
    }
}
