using System.Text;
using NugoloMag.Analyst.Application;
using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Infrastructure.Reports;

public sealed class MarkdownReportWriter : IReportWriter
{
    public string FileExtension => ".md";

    public string Render(AnalysisReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Analisi magazzini al {report.Window.AsOf:dd/MM/yyyy}");
        sb.AppendLine();
        sb.AppendLine($"Magazzini: {string.Join(", ", report.Warehouses)} · righe analizzate: {report.RowsAnalyzed:N0} · " +
                      $"baseline {report.Window.BaselineStart:dd/MM}–{report.Window.BaselineEnd:dd/MM}, recente {report.Window.RecentStart:dd/MM}–{report.Window.AsOf:dd/MM}");
        sb.AppendLine();
        sb.AppendLine("## Sintesi");
        sb.AppendLine();
        sb.AppendLine(report.Narrative.Trim());
        sb.AppendLine();
        sb.AppendLine("## Cambiamenti rilevati");

        foreach (var (f, i) in report.Findings.Select((f, i) => (f, i + 1)))
        {
            sb.AppendLine();
            sb.AppendLine($"### {i}. {f.Headline}");
            sb.AppendLine();
            sb.AppendLine($"**{MetricLabels.Italian(f.Kind)}** · priorità {f.Severity} · magnitudo {f.Magnitude:0}/100 · `{f.Subject}`");
            if (f.Evidence.Count > 0)
            {
                sb.AppendLine();
                foreach (var (k, v) in f.Evidence) sb.AppendLine($"- {k}: {v}");
            }
            if (f.RootCauses.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("| Driver | Segmento | Prima | Ora | Delta | Quota |");
                sb.AppendLine("|---|---|---:|---:|---:|---:|");
                foreach (var c in f.RootCauses)
                    sb.AppendLine($"| {c.Dimension} | {c.Member} | {MetricLabels.Number(c.Baseline)} | {MetricLabels.Number(c.Current)} | {MetricLabels.Number(c.Delta)} | {c.Share:P0} |");
            }
        }
        return sb.ToString();
    }
}
