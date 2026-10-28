using System.Data;
using Npgsql;
using TkgPda.Application;

namespace TkgPda.Infrastructure.Postgres;

public sealed class PostgresFieldSessionStore(NpgsqlDataSource dataSource) : IFieldSessionStore
{
    public async Task<AcquireSessionResult> AcquireLoginSessionAsync(
        Guid userId,
        Guid deviceId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        DateTimeOffset tokenExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // 1. Lock the user row for serialization of concurrent logins for the same user
        await using (var lockCmd = new NpgsqlCommand("SELECT id FROM auth.users WHERE id = @user_id FOR UPDATE", connection, transaction))
        {
            lockCmd.Parameters.AddWithValue("user_id", userId);
            var locked = await lockCmd.ExecuteScalarAsync(cancellationToken);
            if (locked is null)
            {
                throw new InvalidOperationException($"User '{userId}' was not found during session acquisition.");
            }
        }

        // 2. Expire any stale sessions for this user (where lease or token has expired)
        await using (var expireCmd = new NpgsqlCommand(
            """
            UPDATE auth.user_sessions
            SET revoked_at = @now,
                revocation_reason = CASE
                    WHEN token_expires_at <= @now THEN 'TOKEN_EXPIRED'
                    ELSE 'LEASE_EXPIRED'
                END
            WHERE user_id = @user_id
              AND revoked_at IS NULL
              AND (lease_expires_at <= @now OR token_expires_at <= @now)
            """, connection, transaction))
        {
            expireCmd.Parameters.AddWithValue("user_id", userId);
            expireCmd.Parameters.AddWithValue("now", now);
            await expireCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 3. Check for any remaining active unrevoked session for this user
        Guid? existingSessionId = null;
        Guid? existingDeviceId = null;
        string? existingDeviceCode = null;

        await using (var checkCmd = new NpgsqlCommand(
            """
            SELECT s.id, s.device_id, d.device_code
            FROM auth.user_sessions s
            LEFT JOIN audit.client_devices d ON d.id = s.device_id
            WHERE s.user_id = @user_id
              AND s.revoked_at IS NULL
            FOR UPDATE OF s
            """, connection, transaction))
        {
            checkCmd.Parameters.AddWithValue("user_id", userId);
            await using var reader = await checkCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existingSessionId = reader.GetGuid(0);
                existingDeviceId = reader.GetGuid(1);
                existingDeviceCode = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }

        // 4. If an active session exists on a different device -> reject with Conflict
        if (existingSessionId.HasValue && existingDeviceId != deviceId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return AcquireSessionResult.ActiveSessionOnDifferentDevice(
                existingSessionId.Value,
                existingDeviceId!.Value,
                existingDeviceCode);
        }

        // 5. If an active session exists on the SAME device -> revoke old session
        if (existingSessionId.HasValue && existingDeviceId == deviceId)
        {
            await using var replaceCmd = new NpgsqlCommand(
                """
                UPDATE auth.user_sessions
                SET revoked_at = @now,
                    revocation_reason = 'REPLACED_SAME_DEVICE_LOGIN'
                WHERE id = @session_id
                """, connection, transaction);
            replaceCmd.Parameters.AddWithValue("now", now);
            replaceCmd.Parameters.AddWithValue("session_id", existingSessionId.Value);
            await replaceCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 6. Create new user_session
        var newSessionId = Guid.NewGuid();
        var leaseExpiresAt = now.Add(leaseDuration);
        if (leaseExpiresAt > tokenExpiresAt)
        {
            leaseExpiresAt = tokenExpiresAt;
        }

        UserSessionRecord session;
        await using (var insertCmd = new NpgsqlCommand(
            """
            INSERT INTO auth.user_sessions (
                id, user_id, device_id, client_type,
                created_at, last_seen_at, lease_expires_at, token_expires_at,
                revoked_at, revocation_reason
            )
            VALUES (
                @id, @user_id, @device_id, 'PDA',
                @now, @now, @lease_expires_at, @token_expires_at,
                NULL, NULL
            )
            RETURNING id, user_id, device_id, client_type,
                      created_at, last_seen_at, lease_expires_at, token_expires_at,
                      revoked_at, revocation_reason
            """, connection, transaction))
        {
            insertCmd.Parameters.AddWithValue("id", newSessionId);
            insertCmd.Parameters.AddWithValue("user_id", userId);
            insertCmd.Parameters.AddWithValue("device_id", deviceId);
            insertCmd.Parameters.AddWithValue("now", now);
            insertCmd.Parameters.AddWithValue("lease_expires_at", leaseExpiresAt);
            insertCmd.Parameters.AddWithValue("token_expires_at", tokenExpiresAt);

            await using var reader = await insertCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Failed to insert new field user session.");
            }

            session = MapUserSession(reader);
        }

        await transaction.CommitAsync(cancellationToken);
        return AcquireSessionResult.Succeeded(session);
    }

    public async Task<UserSessionRecord?> FindActiveSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT id, user_id, device_id, client_type,
                   created_at, last_seen_at, lease_expires_at, token_expires_at,
                   revoked_at, revocation_reason
            FROM auth.user_sessions
            WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapUserSession(reader);
    }

    public async Task<UserSessionRecord?> HeartbeatAsync(
        Guid sessionId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var newLeaseExpiresAt = now.Add(leaseDuration);
        await using var command = dataSource.CreateCommand(
            """
            UPDATE auth.user_sessions
            SET last_seen_at = @now,
                lease_expires_at = LEAST(@new_lease_expires_at, token_expires_at)
            WHERE id = @id
              AND revoked_at IS NULL
              AND lease_expires_at > @now
              AND token_expires_at > @now
            RETURNING id, user_id, device_id, client_type,
                      created_at, last_seen_at, lease_expires_at, token_expires_at,
                      revoked_at, revocation_reason
            """);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("new_lease_expires_at", newLeaseExpiresAt);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapUserSession(reader);
    }

    public async Task<bool> RevokeSessionAsync(
        Guid sessionId,
        Guid userId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE auth.user_sessions
            SET revoked_at = @now,
                revocation_reason = @reason
            WHERE id = @id
              AND user_id = @user_id
              AND revoked_at IS NULL
            """);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("now", now);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    private static UserSessionRecord MapUserSession(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
}
