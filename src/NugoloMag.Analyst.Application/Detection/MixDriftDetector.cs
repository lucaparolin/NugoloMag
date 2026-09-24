using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Application.Detection;

/// <summary>
/// Drift di distribuzione: confronta il mix delle uscite per categoria tra baseline e periodo recente
/// con il Population Stability Index. La scomposizione per categoria è già la root cause.
/// </summary>
public sealed class MixDriftDetector(DetectionSettings settings) : IChangeDetector
{
    private const double Epsilon = 1e-4;

    public string Name => "Cambio di mix";

    public IEnumerable<Finding> Detect(InventoryDataset data, AnalysisWindow window)
    {
        foreach (var warehouse in data.Warehouses.Where(w => data.IsLoadedThrough(w, window.AsOf)))
        {
            var byCategory = data.In(warehouse)
                .GroupBy(r => r.Category)
                .Select(g => (
                    Category: g.Key,
                    Baseline: (double)g.Where(r => window.IsBaseline(r.Date)).Sum(r => r.Outbound),
                    Recent: (double)g.Where(r => window.IsRecent(r.Date)).Sum(r => r.Outbound)))
                .ToList();

            var totalBaseline = byCategory.Sum(c => c.Baseline);
            var totalRecent = byCategory.Sum(c => c.Recent);
            if (byCategory.Count < 2 || totalBaseline <= 0 || totalRecent <= 0) continue;

            var terms = byCategory.Select(c =>
            {
                var b = Math.Max(c.Baseline / totalBaseline, Epsilon);
                var r = Math.Max(c.Recent / totalRecent, Epsilon);
                return (c.Category, b, r, Psi: (r - b) * Math.Log(r / b));
            }).ToList();

            var psi = terms.Sum(t => t.Psi);
            if (psi < settings.MinPsi) continue;

            var contributions = terms
                .OrderByDescending(t => t.Psi)
                .Take(5)
                .Select(t => new Contribution("Categoria", t.Category, t.b, t.r, t.r - t.b, t.Psi / psi))
                .ToList();
            var lead = contributions[0];

            yield return new Finding
            {
                Kind = FindingKind.MixDrift,
                Subject = new Subject(warehouse),
                Metric = Metric.Outbound,
                Date = window.AsOf,
                Magnitude = MagnitudeScore.From(psi * 30, impact: 1.0),
                Headline = $"Mix delle uscite di {warehouse} cambiato (PSI {psi:0.00}): {lead.Member} passa dal {lead.Baseline:P0} al {lead.Current:P0}",
                Evidence = new Dictionary<string, string> { ["PSI"] = psi.ToString("0.000") },
                RootCauses = contributions
            };
        }
    }
}
