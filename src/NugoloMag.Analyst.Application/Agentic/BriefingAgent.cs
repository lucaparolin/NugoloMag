using System.Text;
using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Domain.Agentic;

namespace NugoloMag.Analyst.Application.Agentic;

/// <summary>
/// Agente Briefing direzionale (blueprint §16): non riassume cruscotti, dice cosa merita attenzione.
/// La struttura (sintesi, rischi critici, segnali positivi, cause, azioni, watch list) è deterministica;
/// un LLM, se configurato, la comprime in poche frasi di linguaggio manageriale senza aggiungere numeri.
/// </summary>
public sealed class BriefingAgent(IChatModel? model)
{
    public string Name => AgentNames.Briefing;

    public async Task<ExecutiveBriefing> ComposeAsync(
        string sourceName, DateOnly asOf, DateTimeOffset now, IReadOnlyList<Incident> incidents, CancellationToken ct = default)
    {
        var open = incidents.Where(i => i.IsOpen).OrderByDescending(i => i.SeverityScore).ToList();
        var resolvedRecently = incidents.Where(i => !i.IsOpen && i.UpdatedAt >= now.AddDays(-7)).ToList();

        BriefingItem Item(Incident i, string? detail = null) =>
            new(i.Title, detail ?? i.ProbableRootCause ?? i.Signals.FirstOrDefault()?.WhyItMatters ?? "", i.Severity, Trend(i), i.Warehouse, i.Id);

        var critical = open.Where(i => i.Severity >= SeverityLevel.High).Select(i => Item(i)).ToList();
        var watch = open.Where(i => i.Severity is SeverityLevel.Low or SeverityLevel.Medium && i.Severity < SeverityLevel.High)
            .Take(8).Select(i => Item(i, i.Signals.FirstOrDefault()?.Observation)).ToList();
        var positive = resolvedRecently.Select(i => Item(i, i.Outcome ?? "Situazione rientrata."))
            .Concat(open.Where(i => i.Status == IncidentStatus.Improving).Select(i => Item(i, "In miglioramento.")))
            .Concat(open.Where(i => i.Signature.StartsWith("productivity:mix-driven", StringComparison.Ordinal))
                .Select(i => Item(i, "Nessuna inefficienza: il calo è spiegato dalla complessità del lavoro.")))
            .ToList();
        var causes = open.Where(i => i.ProbableRootCause is not null && i.Confidence >= ConfidenceLevel.Medium)
            .Select(i => Item(i, $"{i.ProbableRootCause} (confidenza {Label(i.Confidence)})")).Take(6).ToList();
        var actions = open.SelectMany(i => i.Recommendations.Select(r => (Incident: i, Rec: r)))
            .OrderByDescending(x => x.Rec.Urgency).ThenByDescending(x => x.Incident.SeverityScore)
            .Take(6)
            .Select(x => new BriefingItem(x.Rec.Action, $"{x.Rec.ExpectedResult} Approvazione: {Approval(x.Rec.Approval)}.", x.Rec.Urgency, Trend(x.Incident), x.Incident.Warehouse, x.Incident.Id))
            .ToList();

        // Confronto tra magazzini: lo stesso tipo di problema in più magazzini è un segnale sistemico.
        var cross = open.GroupBy(i => string.Join(':', i.Signature.Split(':').Take(2)))
            .Where(g => g.Select(i => i.Warehouse).Distinct().Count() >= 2)
            .Select(g => $"{g.First().Title.Split(' ')[0]}… presente in {string.Join(", ", g.Select(i => i.Warehouse).Distinct())}: possibile causa comune")
            .ToList();

        var summary = new List<string>();
        foreach (var i in open.Take(5))
            summary.Add($"{TrendLabel(Trend(i))} · {i.Warehouse}: {i.Title}" + (i.ProbableRootCause is { } c ? $" — {c}" : ""));
        if (summary.Count == 0) summary.Add("Nessun problema rilevante aperto: le operazioni sono in linea con il normale.");

        var briefing = new ExecutiveBriefing
        {
            SourceName = sourceName,
            AsOf = asOf,
            CreatedAt = now,
            ExecutiveSummary = summary,
            CriticalRisks = critical,
            PositiveSignals = positive,
            RootCauses = causes,
            RecommendedActions = actions,
            WatchList = watch,
            CrossWarehouse = cross
        };
        return briefing with { Narrative = await NarrateAsync(briefing, ct) };
    }

    private async Task<string?> NarrateAsync(ExecutiveBriefing b, CancellationToken ct)
    {
        if (model is null) return null;
        var sb = new StringBuilder();
        sb.AppendLine($"Briefing al {b.AsOf:dd/MM/yyyy} per la sorgente {b.SourceName}.");
        void Section(string title, IEnumerable<string> lines)
        {
            sb.AppendLine(title + ":");
            foreach (var l in lines) sb.AppendLine("- " + l);
        }
        Section("Sintesi", b.ExecutiveSummary);
        Section("Rischi critici", b.CriticalRisks.Select(i => $"{i.Title}: {i.Detail}"));
        Section("Cause", b.RootCauses.Select(i => $"{i.Title}: {i.Detail}"));
        Section("Azioni", b.RecommendedActions.Select(i => $"{i.Title} ({i.Detail})"));
        Section("Segnali positivi", b.PositiveSignals.Select(i => $"{i.Title}: {i.Detail}"));

        try
        {
            var response = await model.CompleteAsync(new ChatRequest(
                """
                Sei il responsabile operations che scrive alla direzione. Riscrivi il briefing in 4-6 frasi in italiano:
                cosa è successo, perché, cosa rischiamo, cosa proponi. Usa SOLO fatti e numeri presenti nel testo, non aggiungerne.
                Distingui cause confermate da ipotesi. Niente elenchi, niente titoli.
                """,
                [ChatMessage.User(sb.ToString())], [], MaxTokens: 800), ct);
            return response.Stop == ChatStop.Refusal || string.IsNullOrWhiteSpace(response.Text) ? null : response.Text.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // il briefing strutturato basta: la sintesi LLM è un di più
        }
    }

    public static TrendKind Trend(Incident i) => i.Status switch
    {
        IncidentStatus.Resolved => TrendKind.Resolved,
        IncidentStatus.Worsening => TrendKind.Worsening,
        IncidentStatus.Improving => TrendKind.Improving,
        _ => i.History.Count <= 1 ? TrendKind.New : TrendKind.Stable
    };

    public static string TrendLabel(TrendKind t) => t switch
    {
        TrendKind.New => "Nuovo",
        TrendKind.Worsening => "In peggioramento",
        TrendKind.Improving => "In miglioramento",
        TrendKind.Resolved => "Risolto",
        _ => "Persistente"
    };

    private static string Label(ConfidenceLevel c) => c switch { ConfidenceLevel.High => "alta", ConfidenceLevel.Medium => "media", _ => "bassa" };

    private static string Approval(ApprovalLevel a) => a switch
    {
        ApprovalLevel.Informational => "informativa",
        ApprovalLevel.LowRiskAction => "azione a basso rischio",
        ApprovalLevel.SupervisorApproval => "responsabile di magazzino",
        _ => "direzione"
    };
}
