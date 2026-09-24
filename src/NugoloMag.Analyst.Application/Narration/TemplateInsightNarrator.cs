using System.Text;
using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Application.Narration;

/// <summary>Narratore deterministico, senza LLM: usato offline o come fallback.</summary>
public sealed class TemplateInsightNarrator : IInsightNarrator
{
    public Task<string> NarrateAsync(AnalysisReport report, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var high = report.Findings.Count(f => f.Severity == Severity.High);
        var medium = report.Findings.Count(f => f.Severity == Severity.Medium);

        sb.AppendLine($"Analisi al {report.Window.AsOf:dd/MM/yyyy} su {report.Warehouses.Count} magazzini e {report.RowsAnalyzed.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("it-IT"))} righe: " +
                      $"{report.Findings.Count} cambiamenti rilevanti ({high} alta priorità, {medium} media).");

        if (report.Findings.Count == 0)
        {
            sb.AppendLine("Nessun cambiamento significativo rispetto alle ultime settimane.");
            return Task.FromResult(sb.ToString());
        }

        sb.AppendLine();
        sb.AppendLine("Da guardare per primi:");
        foreach (var f in report.Findings.Take(5))
        {
            sb.Append($"- [{MetricLabels.Italian(f.Kind)}] {f.Headline}");
            var cause = f.RootCauses.FirstOrDefault();
            if (cause is not null) sb.Append($" — principale causa: {cause.Dimension.ToLowerInvariant()} {cause.Member} ({cause.Share:P0} della variazione)");
            sb.AppendLine(".");
        }

        var dataIssues = report.Findings.Where(f => f.Kind is FindingKind.DataFreshness or FindingKind.Integrity).ToList();
        if (dataIssues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Attenzione: {dataIssues.Count} problemi di qualità del dato; verificare prima di trarre conclusioni sui magazzini {string.Join(", ", dataIssues.Select(d => d.Subject.Warehouse).Distinct())}.");
        }

        return Task.FromResult(sb.ToString());
    }

    public Task<string> AnswerAsync(AnalysisReport report, string question, CancellationToken ct = default) =>
        Task.FromResult("Le domande in linguaggio naturale richiedono il narratore Claude (impostare ANTHROPIC_API_KEY).");
}
