using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Application;

/// <summary>
/// Orchestratore: carica → rileva (N detector indipendenti) → ordina → spiega (root cause) → consolida → racconta.
/// Ogni passo dipende solo da astrazioni, così sorgenti dati e narratori sono intercambiabili.
/// </summary>
public sealed class AnalystService(
    IInventoryRepository repository,
    IEnumerable<IChangeDetector> detectors,
    IRootCauseAnalyzer rootCause,
    FindingRanker ranker,
    FindingConsolidator consolidator,
    IInsightNarrator narrator,
    TimeProvider clock)
{
    public async Task<AnalysisReport> AnalyzeAsync(AnalysisWindow window, CancellationToken ct = default)
    {
        var rows = await repository.LoadAsync(window.LoadFrom, window.AsOf, ct);
        var data = new InventoryDataset(rows);

        var explained = ranker.Rank(detectors.SelectMany(d => d.Detect(data, window)))
            .Select(f => f with
            {
                RootCauses = rootCause.Explain(f, data, window),
                History = HistoryOf(f, data, window)
            })
            .ToList();
        var findings = consolidator.Consolidate(explained, data);

        var draft = new AnalysisReport(window, data.Warehouses, rows.Count, findings, Narrative: "", clock.GetUtcNow());
        return draft with { Narrative = await narrator.NarrateAsync(draft, ct) };
    }

    private static IReadOnlyList<SeriesPoint> HistoryOf(Finding finding, InventoryDataset data, AnalysisWindow window)
    {
        if (finding.Metric is not { } metric) return [];
        var series = data.Series(finding.Subject, metric);
        return window.BaselineDates().Concat(window.RecentDates()).Select(d => new SeriesPoint(d, series[d])).ToList();
    }
}
