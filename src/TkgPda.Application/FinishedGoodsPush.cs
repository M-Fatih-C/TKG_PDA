namespace TkgPda.Application;

/// <summary>
/// Finished-goods delivery push. The client never decides that a container
/// arrived; the completion transaction writes one outbox row per eligible
/// registration, and a worker delivers it later.
/// </summary>
public static class FinishedGoodsPushCopy
{
    public const string NotificationType = "FINISHED_GOODS_DELIVERY";

    public static string EventKey(Guid storeUnitId) =>
        $"finished-goods-delivery:{storeUnitId:D}";

    public static string Title(string locationDisplayName) =>
        $"Kasa Koyuldu — {locationDisplayName}";

    /// <summary>
    /// Human-readable body from the values the server actually has. Missing
    /// pieces are omitted. Raw identifiers are never included.
    /// </summary>
    public static string Body(string? partCode, string? machineCode, long? requestNo)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(partCode)) parts.Add(partCode.Trim());
        if (!string.IsNullOrWhiteSpace(machineCode)) parts.Add(machineCode.Trim());
        if (requestNo is { } number) parts.Add($"Talep #{number}");
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Roles that may receive a finished-goods delivery push. Evaluated on the
/// server when the outbox row is written and again when it is sent.
/// </summary>
public static class FinishedGoodsPushAuthorization
{
    public static readonly string[] RecipientRoles =
    [
        KnownRoleCodes.Admin,
        KnownRoleCodes.Supervisor,
        KnownRoleCodes.WarehouseOperator,
        KnownRoleCodes.FinishedGoodsWarehouseOperator
    ];
}

public enum PushSendOutcome
{
    Sent,
    TransientFailure,
    InvalidToken
}

public sealed record PushNotificationMessage(
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Data);

/// <summary>
/// The only Firebase-facing boundary. Transport completion never calls it.
/// </summary>
public interface IPushNotificationSender
{
    Task<PushSendOutcome> SendAsync(
        string token,
        PushNotificationMessage message,
        CancellationToken cancellationToken);
}

public interface IPushRegistrationStore
{
    Task RegisterAsync(
        Guid userId,
        Guid deviceId,
        string provider,
        string platform,
        string token,
        CancellationToken cancellationToken);

    Task DeactivateAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);
}

public sealed class PushNotificationOptions
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "Firebase";
    public string FirebaseProjectId { get; set; } = "";

    public void Validate()
    {
        if (!Enabled) return;
        if (!string.Equals(Provider, "Firebase", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "PushNotifications:Provider must be Firebase when push notifications are enabled.");
        }

        if (string.IsNullOrWhiteSpace(FirebaseProjectId))
        {
            throw new InvalidOperationException(
                "PushNotifications:FirebaseProjectId is required when push notifications are enabled.");
        }
    }
}
