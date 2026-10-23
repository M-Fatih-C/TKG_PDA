using TkgPda.Application;

namespace TkgPda.Api;

/// <summary>
/// Polls the current RDS alarm snapshot on the server. It does not dispatch,
/// change slots, or depend on an open Admin or PDA screen.
/// </summary>
public sealed class RdsAlarmObservationWorker(
    RdsIntegrationService observations,
    RdsOptions options,
    ILogger<RdsAlarmObservationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(options.AlarmObservationIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await observations.ObserveCurrentAlarmsAsync(Guid.NewGuid(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                var code = error is RdsReadException read ? read.Code : error.GetType().Name;
                logger.LogWarning("RDS alarm observation poll failed with {Code}.", code);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
