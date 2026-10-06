using Npgsql;
using TkgPda.Application;

namespace TkgPda.Infrastructure.Postgres;

public sealed partial class PostgresRdsIntegrationStore
{
    public async Task ObserveAlarmSnapshotAsync(
        IReadOnlyList<RdsRobotAlarmSnapshot> alarms,
        Guid correlationId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var open = await ListOpenAsync(connection, transaction, cancellationToken);
        var plan = RobotAlarmObservationRules.Reconcile(open, alarms, observedAt, correlationId);
        foreach (var item in plan.Upserts)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO integration.robot_alarm_observations (
                    id, fingerprint, robot_id, severity, alarm_code, description, occurred_at_raw,
                    occurrence_count, first_seen_at, last_seen_at, cleared_at, last_correlation_id)
                VALUES (
                    @id, @fingerprint, @robot, @severity, @code, @description, @occurred,
                    @count, @first_seen, @last_seen, NULL, @correlation)
                ON CONFLICT (fingerprint) WHERE cleared_at IS NULL DO UPDATE
                SET occurrence_count = EXCLUDED.occurrence_count,
                    last_seen_at = EXCLUDED.last_seen_at,
                    last_correlation_id = EXCLUDED.last_correlation_id
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("id", item.Id);
            command.Parameters.AddWithValue("fingerprint", item.Fingerprint);
            command.Parameters.AddWithValue("robot", item.RobotId);
            command.Parameters.AddWithValue("severity", item.Severity);
            command.Parameters.AddWithValue("code", item.AlarmCode);
            command.Parameters.AddWithValue("description", (object?)item.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("occurred", (object?)item.OccurredAtRaw ?? DBNull.Value);
            command.Parameters.AddWithValue("count", (object?)item.OccurrenceCount ?? DBNull.Value);
            command.Parameters.AddWithValue("first_seen", item.FirstSeenAt);
            command.Parameters.AddWithValue("last_seen", item.LastSeenAt);
            command.Parameters.AddWithValue("correlation", item.LastCorrelationId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (plan.ClearIds.Count > 0)
        {
            await using var clear = new NpgsqlCommand(
                """
                UPDATE integration.robot_alarm_observations
                SET cleared_at = @seen
                WHERE id = ANY(@ids) AND cleared_at IS NULL
                """,
                connection,
                transaction);
            clear.Parameters.AddWithValue("seen", observedAt);
            clear.Parameters.AddWithValue("ids", plan.ClearIds.ToArray());
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ObserveAlarmReadFailureAsync(
        Guid correlationId,
        DateTimeOffset observedAt,
        string errorCode,
        string message,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO integration.robot_alarm_read_failures (correlation_id, observed_at, error_code, message)
            VALUES (@correlation, @seen, @code, @message)
            """);
        command.Parameters.AddWithValue("correlation", correlationId);
        command.Parameters.AddWithValue("seen", observedAt);
        command.Parameters.AddWithValue("code", errorCode);
        command.Parameters.AddWithValue("message", message.Length > 500 ? message[..500] : message);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RobotAlarmObservationRecord>> ListAlarmHistoryAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var bounded = Math.Clamp(limit, 1, 200);
        await using var command = dataSource.CreateCommand(
            """
            SELECT id, fingerprint, robot_id, severity, alarm_code, description, occurred_at_raw,
                   occurrence_count, first_seen_at, last_seen_at, cleared_at, last_correlation_id
            FROM integration.robot_alarm_observations
            ORDER BY last_seen_at DESC, id
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("limit", bounded);
        var rows = new List<RobotAlarmObservationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadObservation(reader));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<RobotAlarmObservationRecord>> ListOpenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, fingerprint, robot_id, severity, alarm_code, description, occurred_at_raw,
                   occurrence_count, first_seen_at, last_seen_at, cleared_at, last_correlation_id
            FROM integration.robot_alarm_observations
            WHERE cleared_at IS NULL
            ORDER BY first_seen_at, id
            FOR UPDATE
            """,
            connection,
            transaction);
        var rows = new List<RobotAlarmObservationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadObservation(reader));
        }

        return rows;
    }

    private static RobotAlarmObservationRecord ReadObservation(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetInt32(7),
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.GetFieldValue<DateTimeOffset>(9),
        reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
        reader.GetGuid(11));
}
