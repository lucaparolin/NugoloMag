using Anthropic;
using Anthropic.Models.Beta.Messages;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Domain.Discovery;
using NugoloMag.Analyst.Infrastructure.Serialization;

namespace NugoloMag.Analyst.Infrastructure.Claude;

/// <summary>
/// Secondo parere di Claude sulla proposta dell'agente: legge candidati, mapping, causali e prova sui dati
/// e segnala scelte dubbie. Non cambia nulla da solo: l'utente decide.
/// </summary>
public sealed class ClaudeSchemaAdvisor(AnthropicClient client, string model = ClaudeInsightNarrator.DefaultModel) : ISchemaAdvisor
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
        var json = NugoloJson.Serialize(report with { Validation = report.Validation is { } v ? v with { Preview = v.Preview.Take(5).ToList() } : null });

        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = SystemPrompt,
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            Messages = [new() { Role = Role.User, Content = $"<analisi>\n{json}\n</analisi>" }]
        }, ct);

        if (response.StopReason == "refusal") return null;
        var text = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text)).Trim();
        return text.Length == 0 ? null : text;
    }
}
