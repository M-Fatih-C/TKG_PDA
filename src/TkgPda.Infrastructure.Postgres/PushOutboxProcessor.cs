using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;

namespace TkgPda.Infrastructure.Postgres;

/// <summary>
/// Sends pending push outbox rows. A row is claimed with FOR UPDATE SKIP LOCKED
/// and held until the provider call returns, so two hosts cannot deliver it.
/// The token is never written to a log or to last_error.
/// </summary>
public sealed class PushOutboxProcessor(NpgsqlDataSource dataSource, IPushNotificationSender sender)
{
    public const int MaxAttempts = 8;

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        var handled = 0;
        for (var i = 0; i < 20; i++)
        {
            if (!await ProcessOneAsync(cancellationToken)) break;
            handled++;
        }

        return handled;
    }

    private async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        Guid? outboxId = null;
        Guid userId = Guid.Empty;
        string token = "";
        string title = "";
        string body = "";
        string dataJson = "";
        var active = false;
        await using (var claim = new NpgsqlCommand(
            """
            SELECT o.id, o.title, o.body, o.data_json::text, r.user_id, r.token, r.is_active
            FROM integration.push_outbox o
            JOIN integration.device_push_registrations r ON r.id = o.registration_id
            WHERE o.sent_at IS NULL
              AND (o.next_attempt_at IS NULL OR o.next_attempt_at <= clock_timestamp())
            ORDER BY o.created_at
            LIMIT 1
            FOR UPDATE OF o SKIP LOCKED
            """,
            connection,
            transaction))
        {
            await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                outboxId = reader.GetGuid(0);
                title = reader.GetString(1);
                body = reader.GetString(2);
                dataJson = reader.GetString(3);
                userId = reader.GetGuid(4);
                token = reader.GetString(5);
                active = reader.GetBoolean(6);
            }

            reader.Close();
        }

        if (outboxId is not { } id)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        if (!active)
        {
            await CloseAsync(connection, transaction, id, "REGISTRATION_INACTIVE", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        if (!await RecipientStillAuthorizedAsync(connection, transaction, userId, cancellationToken))
        {
            await CloseAsync(connection, transaction, id, "RECIPIENT_NOT_AUTHORIZED", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        PushSendOutcome outcome;
        try
        {
            outcome = await sender.SendAsync(
                token,
                new PushNotificationMessage(title, body, ReadData(dataJson)),
                cancellationToken);
        }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await MarkTransientAsync(connection, transaction, id, exception.GetType().Name, token, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return true;
            }

        switch (outcome)
        {
            case PushSendOutcome.Sent:
                await using (var sent = new NpgsqlCommand(
                    """
                    UPDATE integration.push_outbox
                    SET sent_at = clock_timestamp(), last_error = NULL
                    WHERE id = @id
                    """,
                    connection,
                    transaction))
                {
                    sent.Parameters.AddWithValue("id", id);
                    await sent.ExecuteNonQueryAsync(cancellationToken);
                }
                break;
            case PushSendOutcome.InvalidToken:
                await using (var revoke = new NpgsqlCommand(
                    """
                    UPDATE integration.device_push_registrations
                    SET is_active = false,
                        revoked_at = clock_timestamp(),
                        updated_at = clock_timestamp()
                    WHERE token = @token AND provider = 'FCM' AND is_active
                    """,
                    connection,
                    transaction))
                {
                    revoke.Parameters.AddWithValue("token", token);
                    await revoke.ExecuteNonQueryAsync(cancellationToken);
                }
                await CloseAsync(connection, transaction, id, "INVALID_TOKEN", cancellationToken);
                break;
            default:
                await MarkTransientAsync(connection, transaction, id, "TRANSIENT_FAILURE", token, cancellationToken);
                break;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<bool> RecipientStillAuthorizedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT 1
            FROM auth.user_roles ur
            JOIN auth.roles role ON role.id = ur.role_id
            WHERE ur.user_id = @user AND role.code = ANY(@roles)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.Add(new NpgsqlParameter("roles", FinishedGoodsPushAuthorization.RecipientRoles));
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task CloseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid outboxId,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE integration.push_outbox
            SET next_attempt_at = 'infinity'::timestamptz,
                last_error = @error
            WHERE id = @id AND sent_at IS NULL
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", outboxId);
        command.Parameters.AddWithValue("error", reason);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkTransientAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid outboxId,
        string detail,
        string token,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE integration.push_outbox
            SET attempt_count = attempt_count + 1,
                last_error = @error,
                next_attempt_at = CASE
                    WHEN attempt_count + 1 >= @max_attempts THEN 'infinity'::timestamptz
                    ELSE clock_timestamp() + make_interval(secs => least(30 * power(2, attempt_count), 900)::double precision)
                END
            WHERE id = @id AND sent_at IS NULL
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", outboxId);
        command.Parameters.AddWithValue("max_attempts", MaxAttempts);
        command.Parameters.AddWithValue("error", SafeError(detail, token));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static string SafeError(string detail, string token)
    {
        var redacted = string.IsNullOrEmpty(token)
            ? detail
            : detail.Replace(token, "[redacted]", StringComparison.Ordinal);
        return redacted.Length <= 500 ? redacted : redacted[..500];
    }

    private static IReadOnlyDictionary<string, string> ReadData(string json)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return parsed ?? new Dictionary<string, string>();
    }
}

/// <summary>
/// Used when push is switched off. The worker is not started in that case;
/// calling the sender is a configuration mistake rather than a silent success.
/// </summary>
public sealed class DisabledPushNotificationSender : IPushNotificationSender
{
    public Task<PushSendOutcome> SendAsync(
        string token,
        PushNotificationMessage message,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Push notifications are disabled.");
}
