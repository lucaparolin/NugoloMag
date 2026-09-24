using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Application.Detection;

/// <summary>
/// Confronta la pendenza della media mobile a 7 giorni nella baseline con quella del periodo recente:
/// segnala quando la direzione si inverte (es. giacenza che cresceva e ora cala velocemente).
/// </summary>
public sealed class TrendReversalDetector(DetectionSettings settings) : IChangeDetector
{
    private static readonly Metric[] Metrics = [Metric.Outbound, Metric.OnHand];

    public string Name => "Inversioni di trend";

    public IEnumerable<Finding> Detect(InventoryDataset data, AnalysisWindow window)
    {
        if (window.RecentDays < 4) yield break;

        foreach (var warehouse in data.Warehouses.Where(w => data.IsLoadedThrough(w, window.AsOf)))
        foreach (var metric in Metrics)
        {
            var subject = new Subject(warehouse);
            var series = data.Series(subject, metric);
            var baselineRoll = SeasonalBaseline.Rolling7(series, window.BaselineDates());
            var recentRoll = SeasonalBaseline.Rolling7(series, window.RecentDates());
            var level = TimeSeries.Mean(baselineRoll);
            if (level <= 0) continue;

            var before = TimeSeries.Slope(baselineRoll) / level;
            var after = TimeSeries.Slope(recentRoll) / level;
            var change = after - before;

            var reversed = Math.Sign(before) != Math.Sign(after) && Math.Abs(after) >= settings.MinTrendChangePerDay;
            if (!reversed || Math.Abs(change) < settings.MinTrendChangePerDay) continue;

            yield return new Finding
            {
                Kind = FindingKind.TrendReversal,
                Subject = subject,
                Metric = metric,
                Date = window.AsOf,
                Magnitude = MagnitudeScore.From(change * 200, impact: 1.0),
                Headline = $"{MetricLabels.Italian(metric)} {warehouse}: trend invertito, da {Rate(before)} a {Rate(after)} al giorno",
                Baseline = TimeSeries.Mean(baselineRoll),
                Observed = TimeSeries.Mean(recentRoll),
                Evidence = new Dictionary<string, string>
                {
                    ["pendenza baseline"] = Rate(before) + "/giorno",
                    ["pendenza recente"] = Rate(after) + "/giorno",
                    ["media mobile 7gg attuale"] = MetricLabels.Number(recentRoll[^1])
                }
            };
        }
    }

    private static string Rate(double r) => (r >= 0 ? "+" : "") + r.ToString("P1", System.Globalization.CultureInfo.GetCultureInfo("it-IT"));
}
