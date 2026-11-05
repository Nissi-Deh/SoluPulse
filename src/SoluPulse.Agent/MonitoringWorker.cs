namespace SoluPulse.Agent;

public sealed class MonitoringWorker(ILogger<MonitoringWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SoluPulse monitoring service started.");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            logger.LogDebug("Monitoring cycle is ready; collectors are not configured yet.");
        }
    }
}
