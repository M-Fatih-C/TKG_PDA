using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using TkgPda.Application;

namespace TkgPda.Api;

/// <summary>
/// Production FCM sender. Credentials come from Application Default Credentials
/// (GOOGLE_APPLICATION_CREDENTIALS or the host environment). No service-account
/// file is read from the repository, and the device token is not logged.
/// </summary>
public sealed class FirebasePushNotificationSender : IPushNotificationSender
{
    public FirebasePushNotificationSender(PushNotificationOptions options)
    {
        options.Validate();
        if (!options.Enabled)
        {
            throw new InvalidOperationException(
                "Firebase push sender requires PushNotifications:Enabled=true.");
        }

        GoogleCredential credential;
        try
        {
            credential = GoogleCredential.GetApplicationDefault();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Push notifications are enabled but Application Default Credentials are unavailable. Set GOOGLE_APPLICATION_CREDENTIALS without committing that file.",
                exception);
        }

        if (FirebaseApp.DefaultInstance is null)
        {
            FirebaseApp.Create(new AppOptions
            {
                Credential = credential,
                ProjectId = options.FirebaseProjectId.Trim()
            });
        }
    }

    public async Task<PushSendOutcome> SendAsync(
        string token,
        PushNotificationMessage message,
        CancellationToken cancellationToken)
    {
        var firebaseMessage = new Message
        {
            Token = token,
            Notification = new Notification
            {
                Title = message.Title,
                Body = message.Body
            },
            Data = message.Data.ToDictionary(pair => pair.Key, pair => pair.Value),
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    ChannelId = "fcm_fallback_notification_channel",
                    Sound = "default"
                }
            }
        };

        try
        {
            await FirebaseMessaging.DefaultInstance.SendAsync(firebaseMessage, cancellationToken);
            return PushSendOutcome.Sent;
        }
        catch (FirebaseMessagingException exception)
        {
            return Map(exception.MessagingErrorCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PushSendOutcome.TransientFailure;
        }
    }

    internal static PushSendOutcome Map(MessagingErrorCode? code) => code switch
    {
        MessagingErrorCode.Unregistered
            or MessagingErrorCode.SenderIdMismatch
            or MessagingErrorCode.InvalidArgument => PushSendOutcome.InvalidToken,
        _ => PushSendOutcome.TransientFailure
    };
}
