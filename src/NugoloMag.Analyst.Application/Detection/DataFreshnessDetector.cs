using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Application.Detection;

/// <summary>Prima di fidarsi dei numeri: il magazzino ha caricato i dati di oggi, e tutti?</summary>
public sealed class DataFreshnessDetector(DetectionSettings settings) : IChangeDetector
{
    public string Name => "Freschezza dati";

    public IEnumerable<Finding> Detect(InventoryDataset data, AnalysisWindow window)
    {
        foreach (var warehouse in data.Warehouses)
        {
            var last = data.LastDate(warehouse);
            if (last is { } l && l < window.AsOf)
            {
                var missingDays = window.AsOf.DayNumber - l.DayNumber;
                yield return new Finding
                {
                    Kind = FindingKind.DataFreshness,
                    Subject = new Subject(warehouse),
                    Date = window.AsOf,
                    Magnitude = MagnitudeScore.From(8 + 2 * missingDays, impact: 1.0),
                    Headline = $"Dati di {warehouse} fermi al {l:dd/MM/yyyy}: {missingDays} giorni mancanti",
                    Evidence = new Dictionary<string, string> { ["ultimo giorno caricato"] = l.ToString("yyyy-MM-dd") }
                };
                continue;
            }

            var rowsPerDay = data.In(warehouse).GroupBy(r => r.Date).ToDictionary(g => g.Key, g => g.Count());
            var typical = TimeSeries.Median(window.BaselineDates().Select(d => (double)rowsPerDay.GetValueOrDefault(d)).ToArray());
            var today = rowsPerDay.GetValueOrDefault(window.AsOf);
            if (typical <= 0) continue;

            var completeness = today / typical;
            if (completeness >= settings.MinLoadCompleteness) continue;

            yield return new Finding
            {
                Kind = FindingKind.DataFreshness,
                Subject = new Subject(warehouse),
                Date = window.AsOf,
                Magnitude = MagnitudeScore.From(15 * (1 - completeness), impact: 1.0),
                Headline = $"Caricamento parziale per {warehouse}: {today} righe contro {typical:0} tipiche ({completeness:P0})",
                Baseline = typical,
                Observed = today
            };
        }
    }
}
