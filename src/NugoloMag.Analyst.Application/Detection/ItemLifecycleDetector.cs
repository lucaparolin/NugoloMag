using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Application.Detection;

/// <summary>
/// Nuovi articoli, articoli abitualmente movimentati che si sono fermati, rotture di stock.
/// È l'equivalente di magazzino dei "nuovi valori" / "valori scomparsi" di una colonna.
/// </summary>
public sealed class ItemLifecycleDetector(DetectionSettings settings) : IChangeDetector
{
    public string Name => "Ciclo di vita articoli";

    public IEnumerable<Finding> Detect(InventoryDataset data, AnalysisWindow window)
    {
        var baselineDates = window.BaselineDates().ToArray();
        var stallDates = Enumerable.Range(0, settings.StallDays).Select(k => window.AsOf.AddDays(-k)).ToArray();

        foreach (var warehouse in data.Warehouses)
        {
            if (!data.IsLoadedThrough(warehouse, window.AsOf)) continue; // lo segnala DataFreshnessDetector

            var whOutbound = new SeasonalBaseline(data.Series(new Subject(warehouse), Metric.Outbound), window).Mean;
            var newItems = new List<(Sku Sku, double Outbound)>();

            foreach (var sku in data.SkusIn(warehouse))
            {
                var subject = new Subject(warehouse, sku);
                var outbound = data.Series(subject, Metric.Outbound);
                var onHand = data.Series(subject, Metric.OnHand);
                var baselineOut = outbound.Slice(baselineDates);

                var activeInBaseline = data.In(subject).Any(r => window.IsBaseline(r.Date) && r.IsActive);
                if (!activeInBaseline)
                {
                    var recentOut = outbound.Slice(window.RecentDates()).Sum();
                    if (data.In(subject).Any(r => window.IsRecent(r.Date) && r.IsActive)) newItems.Add((sku, recentOut));
                    continue;
                }

                var moverRatio = baselineOut.Count(v => v > 0) / (double)baselineOut.Length;
                if (moverRatio < settings.RegularMoverRatio) continue;

                var seasonal = new SeasonalBaseline(outbound, window);
                var expected = stallDates.Sum(seasonal.Expected);
                var actual = stallDates.Sum(d => outbound[d]);
                if (actual > 0 || expected < settings.MinAbsoluteDelta) continue;

                var stock = onHand[window.AsOf];
                var isStockout = stock <= 0;
                var impact = whOutbound <= 0 ? 0.5 : Math.Min(1, 10 * seasonal.Mean / whOutbound);

                yield return new Finding
                {
                    Kind = isStockout ? FindingKind.Stockout : FindingKind.StalledItem,
                    Subject = subject,
                    Metric = Metric.Outbound,
                    Date = window.AsOf,
                    Magnitude = MagnitudeScore.From(isStockout ? 9 : 6, impact),
                    Headline = isStockout
                        ? $"Rottura di stock {subject}: giacenza {MetricLabels.Number(stock)}, zero uscite negli ultimi {settings.StallDays} giorni (attese {MetricLabels.Number(expected)})"
                        : $"Articolo fermo {subject}: nessuna uscita da {settings.StallDays} giorni pur con {MetricLabels.Number(stock)} a stock (attese {MetricLabels.Number(expected)})",
                    Baseline = expected,
                    Observed = 0,
                    Evidence = new Dictionary<string, string>
                    {
                        ["categoria"] = data.CategoryOf(warehouse, sku),
                        ["giorni con uscite in baseline"] = moverRatio.ToString("P0"),
                        ["uscita media giornaliera"] = MetricLabels.Number(seasonal.Mean),
                        ["giacenza attuale"] = MetricLabels.Number(stock)
                    }
                };
            }

            if (newItems.Count > 0)
            {
                var top = newItems.OrderByDescending(n => n.Outbound).Take(5).ToList();
                var recentWhOut = data.Series(new Subject(warehouse), Metric.Outbound).Slice(window.RecentDates()).Sum();
                var share = recentWhOut <= 0 ? 0 : newItems.Sum(n => n.Outbound) / recentWhOut;

                yield return new Finding
                {
                    Kind = FindingKind.NewItem,
                    Subject = new Subject(warehouse),
                    Date = window.AsOf,
                    Magnitude = MagnitudeScore.From(3 + Math.Log(1 + newItems.Count), Math.Min(1, share * 5)),
                    Headline = $"{newItems.Count} nuovi articoli attivi in {warehouse} negli ultimi {window.RecentDays} giorni ({share:P1} delle uscite)",
                    Evidence = top.ToDictionary(
                        n => $"{n.Sku} ({data.CategoryOf(warehouse, n.Sku)})",
                        n => $"{MetricLabels.Number(n.Outbound)} unità uscite")
                };
            }
        }
    }
}
