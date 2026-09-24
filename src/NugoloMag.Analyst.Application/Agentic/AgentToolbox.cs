using System.Text;
using System.Text.Json;
using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Domain.Agentic;

namespace NugoloMag.Analyst.Application.Agentic;

/// <summary>
/// Espone gli agenti specialisti come strumenti per l'LLM dell'orchestratore: il modello non calcola nulla da sé,
/// chiede agli agenti (deterministici) e ragiona sulle loro risposte.
/// </summary>
public sealed class AgentToolbox(
    AgentWorkspace ws,
    IReadOnlyList<ISpecialistAgent> specialists,
    IReadOnlyList<AgentFinding> currentFindings,
    IReadOnlyList<Incident> openIncidents,
    List<AgentTraceStep> trace) : IToolbox
{
    public IReadOnlyList<ToolSpec> Specs { get; } =
    [
        new("ask_agent",
            "Chiede a un agente specialista di esaminare un aspetto. Agenti e aspetti: " +
            string.Join("; ", specialists.Select(s => $"{s.Name} → {string.Join(", ", s.Aspects.Keys)}")) +
            ". Restituisce osservazione, perché conta e prove.",
            "{\"type\":\"object\",\"properties\":{\"agent\":{\"type\":\"string\",\"enum\":[" + string.Join(",", specialists.Select(s => $"\"{s.Name}\"")) + "]}," +
            "\"aspect\":{\"type\":\"string\"},\"warehouse\":{\"type\":\"string\"},\"skus\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"zone\":{\"type\":\"string\"}}," +
            "\"required\":[\"agent\",\"aspect\",\"warehouse\"],\"additionalProperties\":false}"),
        new("list_findings",
            "Elenca i segnali rilevati dagli agenti in questo ciclo (facoltativo filtro per magazzino).",
            """{"type":"object","properties":{"warehouse":{"type":"string"}},"additionalProperties":false}"""),
        new("list_open_incidents",
            "Elenca gli incidenti aperti con severità, stato, causa probabile e raccomandazioni.",
            """{"type":"object","properties":{},"additionalProperties":false}""")
    ];

    public Task<ToolOutcome> ExecuteAsync(string name, JsonElement input, CancellationToken ct = default)
    {
        string? Str(string p) => input.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        switch (name)
        {
            case "ask_agent":
            {
                var agent = specialists.FirstOrDefault(s => s.Name == Str("agent"));
                var warehouse = Str("warehouse");
                if (agent is null || warehouse is null || Str("aspect") is not { } aspect)
                    return Task.FromResult(new ToolOutcome("Parametri mancanti: agent, aspect, warehouse.", true));
                if (!ws.Warehouses.Contains(warehouse, StringComparer.OrdinalIgnoreCase))
                    return Task.FromResult(new ToolOutcome($"Magazzino sconosciuto. Disponibili: {string.Join(", ", ws.Warehouses)}", true));
                var skus = input.TryGetProperty("skus", out var s) && s.ValueKind == JsonValueKind.Array
                    ? s.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];

                var answer = agent.Examine(ws, new AgentQuery(warehouse.ToUpperInvariant(), aspect, skus, Str("zone")));
                trace.Add(new AgentTraceStep(agent.Name, $"esame '{aspect}' su {warehouse} (richiesto dall'LLM)", answer?.Observation ?? "nessun dato", 0));
                return Task.FromResult(answer is null
                    ? new ToolOutcome("Nessun dato sufficiente per questo esame.", false)
                    : new ToolOutcome(Format(answer), false));
            }
            case "list_findings":
            {
                var w = Str("warehouse");
                var list = currentFindings.Where(f => w is null || string.Equals(f.Warehouse, w, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => SeverityModel.Score(f.Severity)).Take(15).ToList();
                return Task.FromResult(new ToolOutcome(list.Count == 0 ? "Nessun segnale rilevato."
                    : string.Join('\n', list.Select(f => $"- [{f.Urgency}] {f.Warehouse} · {f.Title} (agente {f.Agent}, firma {f.Signature})")), false));
            }
            case "list_open_incidents":
                return Task.FromResult(new ToolOutcome(openIncidents.Count == 0 ? "Nessun incidente aperto."
                    : string.Join('\n', openIncidents.OrderByDescending(i => i.SeverityScore).Take(15).Select(i =>
                        $"- #{i.Id} [{i.Severity}, {i.Status}] {i.Warehouse} · {i.Title}; causa: {i.ProbableRootCause ?? "non determinata"}; azioni: {string.Join(" | ", i.Recommendations.Select(r => r.Action))}")), false));
            default:
                return Task.FromResult(new ToolOutcome($"Strumento sconosciuto: {name}", true));
        }
    }

    public static string Format(AgentFinding f)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Osservazione: {f.Observation}");
        sb.AppendLine($"Perché conta: {f.WhyItMatters}");
        foreach (var e in f.Evidence.Take(12)) sb.AppendLine($"- [{Kind(e.Kind)}] {e.Statement}");
        return sb.ToString();
    }

    public static string Kind(ClaimKind k) => k switch
    {
        ClaimKind.Fact => "fatto",
        ClaimKind.Signal => "segnale",
        ClaimKind.Hypothesis => "ipotesi",
        _ => "causa confermata"
    };
}
