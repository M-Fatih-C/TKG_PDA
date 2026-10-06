using Microsoft.Extensions.Options;
using TkgPda.Application;

namespace TkgPda.Api;

public static class DeviceNotificationEndpoints
{
    public static IEndpointRouteBuilder MapDeviceNotifications(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/device-notifications/next", NextAsync).AllowAnonymous();
        endpoints.MapPost("/api/v1/device-notifications/{notificationId:guid}/ack", AckAsync).AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> NextAsync(
        HttpContext context,
        IDeviceNotificationStore notifications,
        IOptions<DeviceNotificationOptions> options,
        CancellationToken cancellationToken)
    {
        var credential = ReadCredential(context);
        var settings = options.Value;
        var hold = TimeSpan.FromSeconds(Math.Clamp(settings.LongPollSeconds, 0, 30));
        var lease = TimeSpan.FromSeconds(Math.Max(1, settings.LeaseSeconds));
        try
        {
            var delivery = await notifications.WaitNextAsync(credential, hold, lease, cancellationToken);
            return delivery is null
                ? Results.NoContent()
                : Results.Ok(new DeviceNotificationResponse(
                    delivery.Id,
                    delivery.EventKey,
                    delivery.NotificationType,
                    delivery.Title,
                    delivery.Body,
                    delivery.Data));
        }
        catch (DeviceNotificationUnauthorizedException)
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "NOTIFICATION_CREDENTIAL_INVALID", "The notification credential is not valid.");
        }
        catch (DeviceNotificationForbiddenException)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "NOTIFICATION_RECIPIENT_NOT_AUTHORIZED", "The notification registration cannot receive finished-goods deliveries.");
        }
    }

    private static async Task<IResult> AckAsync(
        Guid notificationId,
        HttpContext context,
        IDeviceNotificationStore notifications,
        CancellationToken cancellationToken)
    {
        var credential = ReadCredential(context);
        try
        {
            var acknowledged = await notifications.AcknowledgeAsync(credential, notificationId, cancellationToken);
            if (!acknowledged)
            {
                throw new ApiException(StatusCodes.Status404NotFound, "NOTIFICATION_NOT_FOUND", "The notification is not waiting for this registration.");
            }

            return Results.NoContent();
        }
        catch (DeviceNotificationUnauthorizedException)
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "NOTIFICATION_CREDENTIAL_INVALID", "The notification credential is not valid.");
        }
        catch (DeviceNotificationForbiddenException)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "NOTIFICATION_RECIPIENT_NOT_AUTHORIZED", "The notification registration cannot receive finished-goods deliveries.");
        }
    }

    private static string ReadCredential(HttpContext context)
    {
        var value = context.Request.Headers[ApiHeaders.NotificationCredential].ToString();
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
    }
}
