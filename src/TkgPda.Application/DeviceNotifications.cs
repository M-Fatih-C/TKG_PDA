namespace TkgPda.Application;

/// <summary>
/// Finished-goods delivery copy. The tablet displays these strings; it does
/// not decide that a container arrived.
/// </summary>
public static class FinishedGoodsNotificationCopy
{
    public const string NotificationType = "FINISHED_GOODS_DELIVERY";

    public static string EventKey(Guid storeUnitId) => $"finished-goods-delivery:{storeUnitId:D}";

    public static string Title(string locationName) => $"Kasa Koyuldu — {locationName}";

    public static string Body(string? partCode, string? machineCode, long requestNo)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(partCode)) parts.Add(partCode.Trim());
        if (!string.IsNullOrWhiteSpace(machineCode)) parts.Add(machineCode.Trim());
        parts.Add($"Talep #{requestNo}");
        return string.Join(" · ", parts);
    }
}

public static class FinishedGoodsNotificationAuthorization
{
    public static readonly string[] RecipientRoles =
    [
        KnownRoleCodes.Admin,
        KnownRoleCodes.Supervisor,
        KnownRoleCodes.WarehouseOperator,
        KnownRoleCodes.FinishedGoodsWarehouseOperator
    ];
}

public sealed class DeviceNotificationOptions
{
    public int LongPollSeconds { get; set; } = 25;

    public int LeaseSeconds { get; set; } = 60;
}

public sealed record DeviceNotificationRegistration(Guid RegistrationId, string Credential);

public sealed record DeviceNotificationDelivery(
    Guid Id,
    string EventKey,
    string NotificationType,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Data);

public interface IDeviceNotificationStore
{
    Task<DeviceNotificationRegistration> RegisterAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);

    Task DeactivateAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);

    Task<DeviceNotificationDelivery?> WaitNextAsync(string rawCredential, TimeSpan hold, TimeSpan lease, CancellationToken cancellationToken);

    Task<bool> AcknowledgeAsync(string rawCredential, Guid notificationId, CancellationToken cancellationToken);
}

/// <summary>The credential is missing, revoked, or not a known hash.</summary>
public sealed class DeviceNotificationUnauthorizedException : Exception
{
    public DeviceNotificationUnauthorizedException()
        : base("The notification credential is not valid.")
    {
    }
}

/// <summary>The credential matches, but the user, device, or role may not receive.</summary>
public sealed class DeviceNotificationForbiddenException : Exception
{
    public DeviceNotificationForbiddenException()
        : base("The notification registration is not allowed to receive this event.")
    {
    }
}
