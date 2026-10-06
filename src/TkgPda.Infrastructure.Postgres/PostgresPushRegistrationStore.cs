using Npgsql;
using TkgPda.Application;

namespace TkgPda.Infrastructure.Postgres;

/// <summary>
/// One current FCM registration per device. The caller supplies the user and
/// device already taken from the authenticated field session.
/// </summary>
public sealed class PostgresPushRegistrationStore(NpgsqlDataSource dataSource) : IPushRegistrationStore
{
    public async Task RegisterAsync(
        Guid userId,
        Guid deviceId,
        string provider,
        string platform,
        string token,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Free the active-token uniqueness before this device claims it.
        await using (var release = new NpgsqlCommand(
            """
            UPDATE integration.device_push_registrations
            SET is_active = false,
                revoked_at = clock_timestamp(),
                updated_at = clock_timestamp()
            WHERE provider = @provider
              AND token = @token
              AND is_active
              AND device_id <> @device
            """,
            connection,
            transaction))
        {
            release.Parameters.AddWithValue("provider", provider);
            release.Parameters.AddWithValue("token", token);
            release.Parameters.AddWithValue("device", deviceId);
            await release.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var upsert = new NpgsqlCommand(
            """
            INSERT INTO integration.device_push_registrations
                (device_id, user_id, provider, platform, token, is_active, registered_at, updated_at, revoked_at)
            VALUES (@device, @user, @provider, @platform, @token, true, clock_timestamp(), clock_timestamp(), NULL)
            ON CONFLICT (device_id, provider) DO UPDATE
            SET user_id = EXCLUDED.user_id,
                platform = EXCLUDED.platform,
                token = EXCLUDED.token,
                is_active = true,
                updated_at = clock_timestamp(),
                revoked_at = NULL
            """,
            connection,
            transaction))
        {
            upsert.Parameters.AddWithValue("device", deviceId);
            upsert.Parameters.AddWithValue("user", userId);
            upsert.Parameters.AddWithValue("provider", provider);
            upsert.Parameters.AddWithValue("platform", platform);
            upsert.Parameters.AddWithValue("token", token);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeactivateAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE integration.device_push_registrations
            SET is_active = false,
                revoked_at = clock_timestamp(),
                updated_at = clock_timestamp()
            WHERE user_id = @user AND device_id = @device AND is_active
            """,
            connection);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("device", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
