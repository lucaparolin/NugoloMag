using Anthropic;
using Anthropic.Models.Beta.Messages;
using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Infrastructure.Reports;
using NugoloMag.Analyst.Infrastructure.Serialization;

namespace NugoloMag.Analyst.Infrastructure.Claude;

/// <summary>
/// Narratore basato su Claude. I numeri li producono i detector statistici; l'LLM li interpreta,
/// li collega tra loro e li scrive come farebbe un analista. Se la chiamata fallisce si usa il fallback.
/// </summary>
public sealed class ClaudeInsightNarrator(AnthropicClient client, IInsightNarrator fallback, string model = ClaudeInsightNarrator.DefaultModel) : IInsightNarrator
{
    public const string DefaultModel = "claude-opus-5";

    private const string SystemPrompt = """
        Sei un analista senior di logistica e supply chain. Ricevi in JSON i risultati di un motore statistico
        che confronta lo stato dei magazzini con le settimane precedenti (baseline) e ordina i cambiamenti per magnitudo.

        Regole:
        - Scrivi in italiano, tono professionale e diretto, per un responsabile operations.
        - Usa solo i numeri presenti nel JSON; non inventare cause: quando ipotizzi, dillo esplicitamente ("possibile causa").
        - Collega i finding correlati (es. un picco di uscite e una rottura di stock nella stessa categoria).
        - Metti prima i problemi di qualità del dato (DataFreshness, Integrity): se i dati non sono affidabili, le altre conclusioni vanno lette con cautela.
        - Testo semplice, senza markdown pesante: al massimo elenchi puntati con "-".
        """;

    private const string NarrativeInstruction = """
        Scrivi la sintesi del report in tre parti:
        1) Situazione in 2-3 frasi.
        2) Le 3-5 cose da guardare oggi, ciascuna con evidenza numerica e root cause se presente.
        3) Azioni consigliate, concrete e assegnabili (es. verifica inventario, contatto commerciale, riordino).
        """;

    public async Task<string> NarrateAsync(AnalysisReport report, CancellationToken ct = default)
    {
        try
        {
            return await AskAsync(report, NarrativeInstruction, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var text = await fallback.NarrateAsync(report, ct);
            return $"{text}\n(Sintesi automatica: Claude non disponibile — {ex.Message})";
        }
    }

    public Task<string> AnswerAsync(AnalysisReport report, string question, CancellationToken ct = default) =>
        AskAsync(report, $"Rispondi a questa domanda basandoti solo sui dati del report. Se i dati non bastano, dillo e suggerisci quale analisi servirebbe.\n\nDomanda: {question}", ct);

    private async Task<string> AskAsync(AnalysisReport report, string instruction, CancellationToken ct)
    {
        var json = NugoloJson.Serialize(ReportDocument.From(report, includeNarrative: false));

        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = SystemPrompt,
            // In caso di rifiuto dei classificatori di sicurezza il server ripiega su un altro modello.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            Messages =
            [
                new() { Role = Role.User, Content = $"<report>\n{json}\n</report>\n\n{instruction}" }
            ]
        }, ct);

        if (response.StopReason == "refusal")
            throw new InvalidOperationException("Claude ha rifiutato la richiesta.");

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        return string.IsNullOrWhiteSpace(text) ? throw new InvalidOperationException("Risposta vuota.") : text.Trim();
    }
}
