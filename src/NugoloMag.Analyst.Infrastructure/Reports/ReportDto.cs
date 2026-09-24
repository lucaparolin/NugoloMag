using NugoloMag.Analyst.Application;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Infrastructure.Reports;

/// <summary>Proiezione serializzabile del report (JSON per integrazioni e per il contesto dell'LLM).</summary>
public static class ReportDto
{
    public static object From(AnalysisReport report, bool includeNarrative = true) => new
    {
        asOf = report.Window.AsOf.ToString("yyyy-MM-dd"),
        baseline = new { from = report.Window.BaselineStart.ToString("yyyy-MM-dd"), to = report.Window.BaselineEnd.ToString("yyyy-MM-dd") },
        recent = new { from = report.Window.RecentStart.ToString("yyyy-MM-dd"), to = report.Window.AsOf.ToString("yyyy-MM-dd") },
        warehouses = report.Warehouses.Select(w => w.Value),
        rowsAnalyzed = report.RowsAnalyzed,
        narrative = includeNarrative ? report.Narrative : null,
        findings = report.Findings.Select((f, i) => new
        {
            rank = i + 1,
            kind = f.Kind.ToString(),
            kindLabel = MetricLabels.Italian(f.Kind),
            severity = f.Severity.ToString(),
            magnitude = f.Magnitude,
            subject = f.Subject.ToString(),
            metric = f.Metric?.ToString(),
            headline = f.Headline,
            baseline = f.Baseline,
            observed = f.Observed,
            relativeChange = f.RelativeChange is { } rc && double.IsFinite(rc) ? Math.Round(rc, 4) : (double?)null,
            evidence = f.Evidence,
            rootCauses = f.RootCauses.Select(c => new
            {
                dimension = c.Dimension, member = c.Member,
                baseline = Math.Round(c.Baseline, 3), current = Math.Round(c.Current, 3),
                delta = Math.Round(c.Delta, 3), share = Math.Round(c.Share, 3)
            })
        })
    };
}
