using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Domain.Discovery;
using NugoloMag.Analyst.Infrastructure.Serialization;

namespace NugoloMag.Analyst.Infrastructure.Llm;

/// <summary>
/// Secondo parere di un LLM sulla proposta dell'agente: legge candidati, mapping, causali e prova sui dati
/// e segnala scelte dubbie. Non cambia nulla da solo: l'utente decide.
/// </summary>
public sealed class LlmSchemaAdvisor(IChatModel model) : ISchemaAdvisor
{
    private const string SystemPrompt = """
        Sei un consulente esperto di gestionali italiani (ERP) e di database SQL Server.
        Ricevi in JSON l'analisi automatica di un database: tabelle candidate con punteggi e motivazioni,
        il mapping proposto verso il modello "posizione giornaliera di magazzino", le causali di movimento trovate
        e l'esito della query sui dati reali.

        Rispondi in italiano, massimo 12 righe, in testo semplice con elenchi "-":
        - le scelte che ti sembrano corrette (una riga);
        - i rischi concreti (causali assegnate male, tabella sbagliata, giacenze ricostruite inaffidabili, colonne mancanti);
        - cosa verificare con chi conosce il gestionale.
        Non inventare tabelle o colonne che non compaiono nel JSON.
        """;

    public async Task<string?> ReviewAsync(DiscoveryReport report, CancellationToken ct = default)
    {
        // Versione compatta: candidati e mapping sì, schema completo delle tabelle e query no (contesti piccoli dei modelli locali).
        var json = NugoloJson.Serialize(report with
        {
            CandidateTables = [],
            SourceQuery = null,
            TaskQuery = null,
            Steps = report.Steps.Select(s => s with { Details = s.Details.Take(6).ToList() }).ToList(),
            Validation = report.Validation is { } v ? v with { Preview = v.Preview.Take(3).ToList() } : null
        });

        var response = await model.CompleteAsync(new ChatRequest(SystemPrompt, [ChatMessage.User($"<analisi>\n{json}\n</analisi>")], [], MaxTokens: 2000), ct);
        if (response.Stop == ChatStop.Refusal) return null;
        var text = response.Text.Trim();
        return text.Length == 0 ? null : text;
    }
}
