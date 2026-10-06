using TkgPda.Application;

namespace TkgPda.Api.Tests;

/// <summary>
/// Stand-in for FCM. Tests decide the outcome; nothing here contacts Google.
/// </summary>
public sealed class CapturingPushSender : IPushNotificationSender
{
    private readonly Lock _gate = new();
    public int Calls { get; private set; }
    public PushSendOutcome Next { get; set; } = PushSendOutcome.Sent;
    public Exception? Throw { get; set; }
    public TaskCompletionSource<PushSendOutcome>? Hold { get; set; }
    public Action? OnSend { get; set; }

    public async Task<PushSendOutcome> SendAsync(
        string token,
        PushNotificationMessage message,
        CancellationToken cancellationToken)
    {
        OnSend?.Invoke();
        if (Hold is { } hold)
        {
            lock (_gate) Calls++;
            return await hold.Task.WaitAsync(cancellationToken);
        }

        if (Throw is { } exception)
        {
            lock (_gate) Calls++;
            throw exception;
        }

        lock (_gate) Calls++;
        return Next;
    }

    public void Reset()
    {
        Calls = 0;
        Next = PushSendOutcome.Sent;
        Throw = null;
        Hold = null;
        OnSend = null;
    }
}
