using NugoloMag.Analyst.Application;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Infrastructure.Reports;

/// <summary>
/// Forma serializzabile e stabile del report: la salva il monitoraggio, la leggono le viste MVC,
/// la riceve Claude come contesto. Mappatura esplicita dal dominio, serializzazione con source generator.
/// </summary>
public sealed record ReportDocument(
    string AsOf,
    string BaselineFrom,
    string BaselineTo,
    string RecentFrom,
    string RecentTo,
    IReadOnlyList<string> Warehouses,
    int RowsAnalyzed,
    string? Narrative,
    IReadOnlyList<FindingDocument> Findings)
{
    public static ReportDocument From(AnalysisReport report, bool includeNarrative = true) => new(
        AsOf: Iso(report.Window.AsOf),
        BaselineFrom: Iso(report.Window.BaselineStart),
        BaselineTo: Iso(report.Window.BaselineEnd),
        RecentFrom: Iso(report.Window.RecentStart),
        RecentTo: Iso(report.Window.AsOf),
        Warehouses: report.Warehouses.Select(w => w.Value).ToList(),
        RowsAnalyzed: report.RowsAnalyzed,
        Narrative: includeNarrative ? report.Narrative : null,
        Findings: report.Findings.Select((f, i) => FindingDocument.From(f, i + 1)).ToList());

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record FindingDocument(
    int Rank,
    string Kind,
    string KindLabel,
    string Severity,
    string SeverityLabel,
    double Magnitude,
    string Subject,
    string? Metric,
    string? MetricLabel,
    string Headline,
    double? Baseline,
    double? Observed,
    double? RelativeChange,
    IReadOnlyDictionary<string, string> Evidence,
    IReadOnlyList<ContributionDocument> RootCauses,
    IReadOnlyList<PointDocument> History)
{
    public static FindingDocument From(Finding f, int rank) => new(
        Rank: rank,
        Kind: f.Kind.Code(),
        KindLabel: MetricLabels.Italian(f.Kind),
        Severity: f.Severity.Code(),
        SeverityLabel: MetricLabels.Italian(f.Severity),
        Magnitude: f.Magnitude,
        Subject: f.Subject.ToString(),
        Metric: f.Metric?.Code(),
        MetricLabel: f.Metric is { } m ? MetricLabels.Italian(m) : null,
        Headline: f.Headline,
        Baseline: f.Baseline,
        Observed: f.Observed,
        RelativeChange: f.RelativeChange is { } rc && double.IsFinite(rc) ? Math.Round(rc, 4) : null,
        Evidence: f.Evidence,
        RootCauses: f.RootCauses.Select(c => new ContributionDocument(c.Dimension, c.Member,
            Math.Round(c.Baseline, 4), Math.Round(c.Current, 4), Math.Round(c.Delta, 4), Math.Round(c.Share, 4))).ToList(),
        History: f.History.Select(p => new PointDocument(p.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), p.Value)).ToList());
}

public sealed record ContributionDocument(string Dimension, string Member, double Baseline, double Current, double Delta, double Share);

public sealed record PointDocument(string Date, double Value);
