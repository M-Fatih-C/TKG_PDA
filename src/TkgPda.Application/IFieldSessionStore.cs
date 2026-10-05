namespace TkgPda.Application;

public sealed record UserSessionRecord(
    Guid Id,
    Guid UserId,
    Guid DeviceId,
    string ClientType,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset TokenExpiresAt,
    DateTimeOffset? RevokedAt,
    string? RevocationReason);

public enum AcquireSessionStatus
{
    Success,
    ActiveSessionExists
}

public sealed record AcquireSessionResult(
    AcquireSessionStatus Status,
    UserSessionRecord? Session = null,
    Guid? ExistingSessionId = null,
    Guid? ExistingDeviceId = null,
    string? ExistingDeviceCode = null)
{
    public static AcquireSessionResult Succeeded(UserSessionRecord session) =>
        new(AcquireSessionStatus.Success, Session: session);

    public static AcquireSessionResult ActiveSessionOnDifferentDevice(
        Guid existingSessionId,
        Guid existingDeviceId,
        string? existingDeviceCode) =>
        new(AcquireSessionStatus.ActiveSessionExists,
            ExistingSessionId: existingSessionId,
            ExistingDeviceId: existingDeviceId,
            ExistingDeviceCode: existingDeviceCode);
}

public interface IFieldSessionStore
{
    Task<AcquireSessionResult> AcquireLoginSessionAsync(
        Guid userId,
        Guid deviceId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        DateTimeOffset tokenExpiresAt,
        CancellationToken cancellationToken = default);

    Task<UserSessionRecord?> FindActiveSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<UserSessionRecord?> HeartbeatAsync(
        Guid sessionId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeSessionAsync(
        Guid sessionId,
        Guid userId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
