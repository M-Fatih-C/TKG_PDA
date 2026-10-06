using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;

namespace TkgPda.Infrastructure.Postgres;

public sealed class PostgresDeviceNotificationStore(NpgsqlDataSource dataSource, TimeProvider timeProvider) : IDeviceNotificationStore
{
    public async Task<DeviceNotificationRegistration> RegisterAsync(
        Guid userId,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        var credential = NotificationCredential.Create();
        var hash = NotificationCredential.Hash(credential);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO integration.device_notification_registrations
                (device_id, user_id, credential_hash, is_active, registered_at, updated_at, revoked_at)
            VALUES (@device, @user, @hash, true, clock_timestamp(), clock_timestamp(), NULL)
            ON CONFLICT (device_id) DO UPDATE
            SET user_id = EXCLUDED.user_id,
                credential_hash = EXCLUDED.credential_hash,
                is_active = true,
                updated_at = clock_timestamp(),
                revoked_at = NULL
            RETURNING id
            """,
            connection);
        command.Parameters.AddWithValue("device", deviceId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.Add(new NpgsqlParameter("hash", NpgsqlDbType.Bytea) { Value = hash });
        var id = await command.ExecuteScalarAsync(cancellationToken) is Guid found
            ? found
            : throw new InvalidOperationException("The notification registration was not stored.");
        return new DeviceNotificationRegistration(id, credential);
    }

    public async Task DeactivateAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE integration.device_notification_registrations
            SET is_active = false,
                revoked_at = clock_timestamp(),
                updated_at = clock_timestamp()
            WHERE device_id = @device AND user_id = @user AND is_active
            """,
            connection);
        command.Parameters.AddWithValue("device", deviceId);
        command.Parameters.AddWithValue("user", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DeviceNotificationDelivery?> WaitNextAsync(
        string rawCredential,
        TimeSpan hold,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + (hold < TimeSpan.Zero ? TimeSpan.Zero : hold);
        while (true)
        {
            var delivery = await TryClaimAsync(rawCredential, lease, cancellationToken);
            if (delivery is not null) return delivery;
            if (timeProvider.GetUtcNow() >= deadline) return null;
            var remaining = deadline - timeProvider.GetUtcNow();
            var delay = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
            if (delay <= TimeSpan.Zero) return null;
            await Task.Delay(delay, cancellationToken);
        }
    }

    public async Task<bool> AcknowledgeAsync(
        string rawCredential,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        var registrationId = await RequireEligibleRegistrationAsync(rawCredential, cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE integration.notification_outbox
            SET acknowledged_at = COALESCE(acknowledged_at, clock_timestamp())
            WHERE id = @id AND registration_id = @registration
            RETURNING id
            """,
            connection);
        command.Parameters.AddWithValue("id", notificationId);
        command.Parameters.AddWithValue("registration", registrationId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid;
    }

    private async Task<DeviceNotificationDelivery?> TryClaimAsync(
        string rawCredential,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        var registrationId = await RequireEligibleRegistrationAsync(rawCredential, cancellationToken);
        var leaseSeconds = Math.Max(1, (int)lease.TotalSeconds);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid id;
        string eventKey;
        string notificationType;
        string title;
        string body;
        string dataJson;
        await using (var claim = new NpgsqlCommand(
            """
            WITH candidate AS (
                SELECT o.id
                FROM integration.notification_outbox o
                WHERE o.registration_id = @registration
                  AND o.acknowledged_at IS NULL
                  AND (o.lease_until IS NULL OR o.lease_until <= clock_timestamp())
                ORDER BY o.created_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE integration.notification_outbox o
            SET lease_until = clock_timestamp() + make_interval(secs => @lease_seconds),
                delivery_attempt_count = o.delivery_attempt_count + 1,
                last_delivery_at = clock_timestamp()
            FROM candidate
            WHERE o.id = candidate.id
            RETURNING o.id, o.event_key, o.notification_type, o.title, o.body, o.data_json::text
            """,
            connection,
            transaction))
        {
            claim.Parameters.AddWithValue("registration", registrationId);
            claim.Parameters.AddWithValue("lease_seconds", leaseSeconds);
            await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                reader.Close();
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            id = reader.GetGuid(0);
            eventKey = reader.GetString(1);
            notificationType = reader.GetString(2);
            title = reader.GetString(3);
            body = reader.GetString(4);
            dataJson = reader.GetString(5);
            reader.Close();
        }

        await using (var seen = new NpgsqlCommand(
            """
            UPDATE integration.device_notification_registrations
            SET last_seen_at = clock_timestamp(), updated_at = clock_timestamp()
            WHERE id = @id
            """,
            connection,
            transaction))
        {
            seen.Parameters.AddWithValue("id", registrationId);
            await seen.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new DeviceNotificationDelivery(id, eventKey, notificationType, title, body, ReadData(dataJson));
    }

    private async Task<Guid> RequireEligibleRegistrationAsync(string rawCredential, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawCredential) || rawCredential.Length > 512)
        {
            throw new DeviceNotificationUnauthorizedException();
        }

        var hash = NotificationCredential.Hash(rawCredential.Trim());
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT reg.id,
                   reg.is_active,
                   u.is_active,
                   d.is_active,
                   EXISTS (
                       SELECT 1
                       FROM auth.user_roles ur
                       JOIN auth.roles role ON role.id = ur.role_id
                       WHERE ur.user_id = reg.user_id
                         AND role.code = ANY(@roles)
                   )
            FROM integration.device_notification_registrations reg
            JOIN auth.users u ON u.id = reg.user_id
            JOIN audit.client_devices d ON d.id = reg.device_id
            WHERE reg.credential_hash = @hash
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("hash", NpgsqlDbType.Bytea) { Value = hash });
        command.Parameters.Add(new NpgsqlParameter("roles", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = FinishedGoodsNotificationAuthorization.RecipientRoles
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new DeviceNotificationUnauthorizedException();
        }

        var id = reader.GetGuid(0);
        var registrationActive = reader.GetBoolean(1);
        var userActive = reader.GetBoolean(2);
        var deviceActive = reader.GetBoolean(3);
        var roleAllowed = reader.GetBoolean(4);
        if (!registrationActive)
        {
            throw new DeviceNotificationUnauthorizedException();
        }

        if (!userActive || !deviceActive || !roleAllowed)
        {
            throw new DeviceNotificationForbiddenException();
        }

        return id;
    }

    private static IReadOnlyDictionary<string, string> ReadData(string json)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return parsed ?? new Dictionary<string, string>();
    }
}

static class NotificationCredential
{
    public static string Create() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static byte[] Hash(string raw) => SHA256.HashData(Encoding.UTF8.GetBytes(raw));
}
