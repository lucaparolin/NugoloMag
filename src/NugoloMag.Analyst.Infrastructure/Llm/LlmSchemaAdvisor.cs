using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Domain.Discovery;
using NugoloMag.Analyst.Infrastructure.Serialization;

namespace NugoloMag.Analyst.Infrastructure.Llm;

/// <summary>
/// Secondo parere di un LLM sulla proposta dell'agente: legge candidati, mapping, causali e prova sui dati
/// e segnala scelte dubbie. Non cambia nulla da solo: l'utente decide. Istruzioni in agents/schema-advisor/SKILL.md.
/// </summary>
public sealed class LlmSchemaAdvisor(AgentLlm llm) : ISchemaAdvisor
{
    public async Task<string?> ReviewAsync(DiscoveryReport report, CancellationToken ct = default)
    {
        if (llm.Resolve(SkillIds.SchemaAdvisor) is not { } agent) return null;
        // Versione compatta: candidati e mapping sì, schema completo delle tabelle e query no (contesti piccoli dei modelli locali).
        var json = NugoloJson.Serialize(report with
        {
            CandidateTables = [],
            SourceQuery = null,
            TaskQuery = null,
            Steps = report.Steps.Select(s => s with { Details = s.Details.Take(6).ToList() }).ToList(),
            Validation = report.Validation is { } v ? v with { Preview = v.Preview.Take(3).ToList() } : null
        });

        var response = await agent.CompleteAsync(agent.System(), $"<analisi>\n{json}\n</analisi>", ct);
        if (response.Stop == ChatStop.Refusal) return null;
        var text = response.Text.Trim();
        return text.Length == 0 ? null : text;
    }
}
