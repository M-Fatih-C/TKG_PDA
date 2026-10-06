using TkgPda.Infrastructure.Postgres;

namespace TkgPda.Api;

/// <summary>
/// Delivers outbox rows after the database commit. Started only when push is
/// enabled, so a local run with the default configuration never contacts FCM.
/// </summary>
public sealed class PushOutboxWorker(PushOutboxProcessor processor, ILogger<PushOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processor.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Push outbox pass failed ({ErrorType}).",
                    exception.GetType().Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
