using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Application.Detection;

/// <summary>Picchi e crolli del giorno analizzato, per magazzino e per articolo.</summary>
public sealed class PointAnomalyDetector(DetectionSettings settings) : IChangeDetector
{
    private static readonly Metric[] WarehouseMetrics = [Metric.Outbound, Metric.Inbound, Metric.Adjustment, Metric.StockValue];
    private static readonly Metric[] SkuMetrics = [Metric.Outbound, Metric.Adjustment];

    public string Name => "Picchi e crolli";

    public IEnumerable<Finding> Detect(InventoryDataset data, AnalysisWindow window)
    {
        foreach (var warehouse in data.Warehouses)
        {
            if (!data.IsLoadedThrough(warehouse, window.AsOf)) continue; // lo segnala DataFreshnessDetector

            var whSubject = new Subject(warehouse);
            var whOutboundMean = new SeasonalBaseline(data.Series(whSubject, Metric.Outbound), window).Mean;

            foreach (var metric in WarehouseMetrics)
                if (Evaluate(data, window, whSubject, metric, impact: 1.0) is { } f) yield return f;

            foreach (var sku in data.SkusIn(warehouse))
            {
                var subject = new Subject(warehouse, sku);
                var skuBaseline = new SeasonalBaseline(data.Series(subject, Metric.Outbound), window);
                if (skuBaseline.Mean < settings.MinSkuDailyVolume) continue;

                // Un articolo che pesa il 10% delle uscite del magazzino ha impatto pieno.
                var impact = whOutboundMean <= 0 ? 0.5 : Math.Min(1, 10 * skuBaseline.Mean / whOutboundMean);
                foreach (var metric in SkuMetrics)
                    if (Evaluate(data, window, subject, metric, impact) is { } f) yield return f;
            }
        }
    }

    private Finding? Evaluate(InventoryDataset data, AnalysisWindow window, Subject subject, Metric metric, double impact)
    {
        var series = data.Series(subject, metric);
        var baseline = new SeasonalBaseline(series, window);
        var observed = series[window.AsOf];
        var expected = baseline.Expected(window.AsOf);
        var delta = observed - expected;
        var z = baseline.ZScore(observed, window.AsOf);
        var relative = expected == 0 ? double.PositiveInfinity : Math.Abs(delta / expected);

        if (Math.Abs(z) < settings.ZThreshold || relative < settings.MinRelativeChange || Math.Abs(delta) < settings.MinAbsoluteDelta)
            return null;

        var kind = delta > 0 ? FindingKind.Spike : FindingKind.Drop;
        // Su attese piccole la percentuale è fuorviante (-3.950%): meglio la differenza assoluta.
        var change = Math.Abs(expected) < 1 || relative > 5
            ? $"{(delta >= 0 ? "+" : "")}{MetricLabels.Number(delta)} unità"
            : MetricLabels.Percent(delta / Math.Abs(expected));
        return new Finding
        {
            Kind = kind,
            Subject = subject,
            Metric = metric,
            Date = window.AsOf,
            Magnitude = MagnitudeScore.From(z, impact),
            Headline = $"{MetricLabels.Italian(metric)} {subject}: {change} rispetto al normale del {window.AsOf.DayOfWeek.ToItalian()} " +
                       $"({MetricLabels.Number(observed)} contro {MetricLabels.Number(expected)} attesi)",
            Baseline = expected,
            Observed = observed,
            Evidence = new Dictionary<string, string>
            {
                ["z-score robusto"] = z.ToString("0.0"),
                ["rumore tipico (±)"] = MetricLabels.Number(baseline.Noise),
                ["media baseline"] = MetricLabels.Number(baseline.Mean)
            }
        };
    }
}

internal static class DayOfWeekExtensions
{
    public static string ToItalian(this DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "lunedì",
        DayOfWeek.Tuesday => "martedì",
        DayOfWeek.Wednesday => "mercoledì",
        DayOfWeek.Thursday => "giovedì",
        DayOfWeek.Friday => "venerdì",
        DayOfWeek.Saturday => "sabato",
        _ => "domenica"
    };
}
