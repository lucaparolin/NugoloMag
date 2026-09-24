using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Application.Detection;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Application.RootCause;

/// <summary>
/// Scompone la variazione di una metrica di magazzino nei contributi di categorie e articoli
/// (analisi "drill-down" automatica): chi ha causato il delta, e per che quota.
/// </summary>
public sealed class ContributionAnalyzer : IRootCauseAnalyzer
{
    private const int TopCategories = 3;
    private const int TopSkus = 5;

    public IReadOnlyList<Contribution> Explain(Finding finding, InventoryDataset data, AnalysisWindow window)
    {
        if (!finding.Subject.IsWarehouseLevel || finding.Metric is not { } metric) return finding.RootCauses;
        if (finding.Kind is not (FindingKind.Spike or FindingKind.Drop or FindingKind.TrendReversal)) return finding.RootCauses;

        var warehouse = finding.Subject.Warehouse;
        var measure = Measure(finding.Kind, window);

        var total = measure(data.Series(finding.Subject, metric));
        var totalDelta = total.Current - total.Baseline;
        if (Math.Abs(totalDelta) < 1e-9) return [];

        var categories = data.In(warehouse).Select(r => r.Category).Distinct()
            .Select(c => (Member: c, Value: measure(data.CategorySeries(warehouse, c, metric))));
        var skus = data.SkusIn(warehouse)
            .Select(s => (Member: $"{s} ({data.CategoryOf(warehouse, s)})", Value: measure(data.Series(new Subject(warehouse, s), metric))));

        return Rank("Categoria", categories, totalDelta, TopCategories)
            .Concat(Rank("Articolo", skus, totalDelta, TopSkus))
            .ToList();
    }

    private static IEnumerable<Contribution> Rank(
        string dimension, IEnumerable<(string Member, (double Baseline, double Current) Value)> members, double totalDelta, int take) =>
        members
            .Select(m => new Contribution(dimension, m.Member, m.Value.Baseline, m.Value.Current,
                m.Value.Current - m.Value.Baseline, (m.Value.Current - m.Value.Baseline) / totalDelta))
            .Where(c => c.Share > 0.05) // solo chi spinge nella stessa direzione del totale
            .OrderByDescending(c => c.Share)
            .Take(take);

    /// <summary>Coerente con il detector: puntuale sul giorno (con attesa stagionale) oppure media finestra recente vs baseline.</summary>
    private static Func<TimeSeries, (double Baseline, double Current)> Measure(FindingKind kind, AnalysisWindow window) =>
        kind == FindingKind.TrendReversal
            ? s => (TimeSeries.Mean(s.Slice(window.BaselineDates())), TimeSeries.Mean(s.Slice(window.RecentDates())))
            : s => (new SeasonalBaseline(s, window).Expected(window.AsOf), s[window.AsOf]);
}
