using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Domain.Discovery;
using NugoloMag.Analyst.Domain.Monitoring;

namespace NugoloMag.Analyst.Application.Monitoring;

public sealed record ActivationRequest(long DiscoveryId, string Name, TimeOnly RunAt, int BaselineDays, int RecentDays);

/// <summary>
/// Ciclo di vita del monitoraggio: attivazione da un'analisi del DB approvata, esecuzioni pianificate, pausa.
/// Il monitoraggio si attiva solo se l'agente ha una proposta non bloccata: prima si capisce il DB, poi si osserva.
/// </summary>
public sealed class MonitoringService(
    IDiscoveryStore discoveries,
    IMonitorStore monitors,
    ISourceRegistry sources,
    AnalystService analyst,
    TimeProvider clock,
    TimeZoneInfo zone,
    Agentic.WarehouseOrchestrator? orchestrator = null)
{
    public async Task<long> ActivateAsync(ActivationRequest request, CancellationToken ct = default)
    {
        var discovery = await discoveries.GetAsync(request.DiscoveryId, ct)
                        ?? throw new InvalidOperationException($"Analisi {request.DiscoveryId} non trovata.");

        if (discovery.Readiness == Readiness.Blocked || discovery.Mapping is null || discovery.SourceQuery is null)
            throw new InvalidOperationException("L'analisi del database non è conclusa: correggere il mapping prima di attivare il monitoraggio.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100)
            throw new ArgumentException("Nome del monitoraggio obbligatorio (max 100 caratteri).");
        if (request.BaselineDays is < 7 or > 365 || request.RecentDays < 1 || request.RecentDays >= request.BaselineDays)
            throw new ArgumentException("Finestre non valide: baseline 7-365 giorni, periodo recente più corto della baseline.");

        var now = clock.GetUtcNow();
        return await monitors.CreateAsync(new MonitorDefinition
        {
            Name = request.Name.Trim(),
            SourceName = discovery.SourceName,
            DiscoveryId = discovery.Id,
            Mapping = discovery.Mapping,
            SourceQuery = discovery.SourceQuery,
            TaskQuery = discovery.TaskQuery,
            RunAt = request.RunAt,
            BaselineDays = request.BaselineDays,
            RecentDays = request.RecentDays,
            IsActive = true,
            CreatedAt = now,
            NextRunAt = now // prima esecuzione subito: l'utente vede il primo report senza aspettare la notte
        }, ct);
    }

    public Task RunNowAsync(long monitorId, CancellationToken ct = default) =>
        monitors.SetScheduleAsync(monitorId, isActive: true, clock.GetUtcNow(), ct);

    public async Task PauseAsync(long monitorId, CancellationToken ct = default) =>
        await monitors.SetScheduleAsync(monitorId, isActive: false, nextRunAt: null, ct);

    public async Task ResumeAsync(long monitorId, CancellationToken ct = default)
    {
        var monitor = await monitors.GetAsync(monitorId, ct) ?? throw new InvalidOperationException("Monitoraggio non trovato.");
        await monitors.SetScheduleAsync(monitorId, isActive: true, monitor.NextOccurrenceAfter(clock.GetUtcNow(), zone), ct);
    }

    /// <summary>Chiamato periodicamente dal worker: esegue i monitoraggi scaduti. Ritorna quanti ne ha eseguiti.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var due = await monitors.ListDueAsync(clock.GetUtcNow(), ct);
        foreach (var monitor in due)
            await RunAsync(monitor, ct);
        return due.Count;
    }

    public async Task<long> RunAsync(MonitorDefinition monitor, CancellationToken ct = default)
    {
        var started = clock.GetUtcNow();
        // Si analizza la giornata di ieri: quella di oggi è ancora in corso.
        var asOf = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(started, zone).DateTime).AddDays(-1);

        // Si ripianifica prima di eseguire: un errore non deve far ripartire il monitor in loop ogni minuto.
        await monitors.SetScheduleAsync(monitor.Id, monitor.IsActive, monitor.NextOccurrenceAfter(started, zone), ct);
        var runId = await monitors.StartRunAsync(new MonitorRun
        {
            MonitorId = monitor.Id,
            AsOf = asOf,
            StartedAt = started,
            Status = RunStatus.Running
        }, ct);

        try
        {
            var source = sources.Get(monitor.SourceName).Inventory(monitor.SourceQuery);
            var report = await analyst.AnalyzeAsync(source, new AnalysisWindow(asOf, monitor.BaselineDays, monitor.RecentDays), ct);
            await monitors.CompleteRunAsync(runId, report, clock.GetUtcNow(), ct);

            // Dopo il report statistico, il ciclo agentico: incidenti, indagini, raccomandazioni, briefing.
            if (orchestrator is not null)
            {
                try
                {
                    var cycle = await orchestrator.RunCycleAsync(monitor, asOf, ct);
                    await monitors.AnnotateRunAsync(runId, $"Ciclo agentico: {cycle.Changes.Count(c => c.Transition != Domain.Agentic.IncidentTransition.Unchanged)} transizioni, briefing #{cycle.Briefing.Id}.", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await monitors.AnnotateRunAsync(runId, $"Ciclo agentico non completato: {ex.Message}", ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await monitors.FailRunAsync(runId, ex.Message, clock.GetUtcNow(), ct);
        }
        return runId;
    }
}
