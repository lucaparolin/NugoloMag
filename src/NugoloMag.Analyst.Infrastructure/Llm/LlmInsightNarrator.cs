using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Infrastructure.Reports;
using NugoloMag.Analyst.Infrastructure.Serialization;

namespace NugoloMag.Analyst.Infrastructure.Llm;

/// <summary>
/// Narratore basato su un LLM qualsiasi (Claude, OpenAI-compatibile, Ollama). I numeri li producono i detector statistici; l'LLM li interpreta,
/// li collega tra loro e li scrive come farebbe un analista. Se la chiamata fallisce si usa il fallback.
/// </summary>
public sealed class LlmInsightNarrator(IChatModel model, IInsightNarrator fallback) : IInsightNarrator
{

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
            return $"{text}\n(Sintesi automatica: modello non disponibile — {ex.Message})";
        }
    }

    public Task<string> AnswerAsync(AnalysisReport report, string question, CancellationToken ct = default) =>
        AskAsync(report, $"Rispondi a questa domanda basandoti solo sui dati del report. Se i dati non bastano, dillo e suggerisci quale analisi servirebbe.\n\nDomanda: {question}", ct);

    private async Task<string> AskAsync(AnalysisReport report, string instruction, CancellationToken ct)
    {
        var json = NugoloJson.Serialize(ReportDocument.From(report, includeNarrative: false, compact: true));

        var response = await model.CompleteAsync(new ChatRequest(SystemPrompt,
            [ChatMessage.User($"<report>\n{json}\n</report>\n\n{instruction}")], [], MaxTokens: 4000), ct);

        if (response.Stop == ChatStop.Refusal) throw new InvalidOperationException("Il modello ha rifiutato la richiesta.");
        var text = response.Text.Trim();
        return text.Length == 0 ? throw new InvalidOperationException("Risposta vuota.") : text;
    }
}
