using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Infrastructure.Reports;
using NugoloMag.Analyst.Infrastructure.Serialization;

namespace NugoloMag.Analyst.Infrastructure.Llm;

/// <summary>
/// Narratore basato su un LLM qualsiasi (Claude, OpenAI-compatibile, Ollama). I numeri li producono i detector statistici; l'LLM li interpreta,
/// li collega tra loro e li scrive come farebbe un analista. Se la chiamata fallisce si usa il fallback.
/// Istruzioni e prompt in agents/report-narrator/.
/// </summary>
public sealed class LlmInsightNarrator(AgentLlm llm, IInsightNarrator fallback) : IInsightNarrator
{
    public async Task<string> NarrateAsync(AnalysisReport report, CancellationToken ct = default)
    {
        try
        {
            return await AskAsync(report, "narrative", null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var text = await fallback.NarrateAsync(report, ct);
            return $"{text}\n(Sintesi automatica: modello non disponibile — {ex.Message})";
        }
    }

    public Task<string> AnswerAsync(AnalysisReport report, string question, CancellationToken ct = default) =>
        AskAsync(report, "question", question, ct);

    private async Task<string> AskAsync(AnalysisReport report, string prompt, string? question, CancellationToken ct)
    {
        var agent = llm.Resolve(SkillIds.ReportNarrator) ?? throw new LlmException("Al narratore non è assegnato un modello linguistico.");
        var json = NugoloJson.Serialize(ReportDocument.From(report, includeNarrative: false, compact: true));
        var instruction = agent.Prompt(prompt, new Dictionary<string, string?> { ["question"] = question });

        var response = await agent.CompleteAsync(agent.System(), $"<report>\n{json}\n</report>\n\n{instruction}", ct);

        if (response.Stop == ChatStop.Refusal) throw new InvalidOperationException("Il modello ha rifiutato la richiesta.");
        var text = response.Text.Trim();
        return text.Length == 0 ? throw new InvalidOperationException("Risposta vuota.") : text;
    }
}
