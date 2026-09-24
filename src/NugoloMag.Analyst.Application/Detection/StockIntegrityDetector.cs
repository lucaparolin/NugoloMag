using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Application.Detection;

/// <summary>
/// Regole di integrità specifiche del magazzino: giacenze negative e giacenze che non quadrano con
/// giacenza di ieri + entrate - uscite + rettifiche. Sono i "test di validazione" del dominio.
/// </summary>
public sealed class StockIntegrityDetector(DetectionSettings settings) : IChangeDetector
{
    public string Name => "Integrità giacenze";

    public IEnumerable<Finding> Detect(InventoryDataset data, AnalysisWindow window)
    {
        foreach (var warehouse in data.Warehouses)
        {
            var stockValue = Math.Max(1, (double)data.In(warehouse).Where(r => r.Date == window.AsOf).Sum(r => Math.Abs(r.StockValue)));
            var rowsBySku = data.In(warehouse).GroupBy(r => r.Sku).ToDictionary(g => g.Key, g => g.ToDictionary(r => r.Date));

            var negatives = new List<StockDay>();
            var breaks = new List<(StockDay Row, double Gap)>();

            foreach (var (_, days) in rowsBySku)
            foreach (var date in window.RecentDates())
            {
                if (!days.TryGetValue(date, out var today)) continue;
                if (today.OnHand < 0) negatives.Add(today);

                if (!days.TryGetValue(date.AddDays(-1), out var yesterday)) continue;
                var expected = yesterday.OnHand + today.Inbound - today.Outbound + today.Adjustment;
                var gap = (double)(today.OnHand - expected);
                if (Math.Abs(gap) > settings.ReconciliationTolerance) breaks.Add((today, gap));
            }

            if (negatives.Count > 0)
            {
                var value = negatives.Sum(n => Math.Abs((double)n.StockValue));
                yield return new Finding
                {
                    Kind = FindingKind.Integrity,
                    Subject = new Subject(warehouse),
                    Metric = Metric.OnHand,
                    Date = window.AsOf,
                    Magnitude = MagnitudeScore.From(6 + Math.Log(negatives.Count), Math.Min(1, value / (stockValue * 0.01))),
                    Headline = $"{Articles(negatives.Select(n => n.Sku).Distinct().Count())} con giacenza negativa in {warehouse} negli ultimi {window.RecentDays} giorni",
                    Evidence = negatives.OrderBy(n => n.OnHand).DistinctBy(n => n.Sku).Take(5)
                        .ToDictionary(n => n.Sku.Value, n => $"{MetricLabels.Number((double)n.OnHand)} il {n.Date:dd/MM}")
                };
            }

            if (breaks.Count > 0)
            {
                var value = breaks.Sum(b => Math.Abs(b.Gap) * (double)b.Row.UnitCost);
                yield return new Finding
                {
                    Kind = FindingKind.Integrity,
                    Subject = new Subject(warehouse),
                    Metric = Metric.OnHand,
                    Date = window.AsOf,
                    Magnitude = MagnitudeScore.From(6 + Math.Log(breaks.Count), Math.Min(1, value / (stockValue * 0.01))),
                    Headline = $"{breaks.Count} squadrature di giacenza in {warehouse} (valore {MetricLabels.Number(value)} €): la giacenza non torna con i movimenti",
                    Evidence = breaks.OrderByDescending(b => Math.Abs(b.Gap) * (double)b.Row.UnitCost).Take(5)
                        .ToDictionary(b => $"{b.Row.Sku} {b.Row.Date:dd/MM}", b => $"scarto {MetricLabels.Number(b.Gap)} unità")
                };
            }
        }
    }

    private static string Articles(int n) => n == 1 ? "1 articolo" : $"{n} articoli";
}
