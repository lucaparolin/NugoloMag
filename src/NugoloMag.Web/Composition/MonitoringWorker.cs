using NugoloMag.Analyst.Application.Monitoring;

namespace NugoloMag.Web.Composition;

/// <summary>Il "battito" del monitoraggio: a intervalli regolari esegue i monitor scaduti.</summary>
public sealed class MonitoringWorker(MonitoringService monitoring, ILogger logger, TimeSpan poll) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(poll);
        do
        {
            try
            {
                var executed = await monitoring.RunDueAsync(stoppingToken);
                if (executed > 0) logger.LogInformation("Eseguiti {Count} monitoraggi", executed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Errore nel ciclo di monitoraggio");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
