using NugoloMag.Analyst.Domain.Agentic;
using static NugoloMag.Analyst.Application.Agentic.Evidence;

namespace NugoloMag.Analyst.Application.Agentic;

public sealed record InvestigationResult(
    IReadOnlyList<Hypothesis> Hypotheses,
    string? ProbableRootCause,
    IReadOnlyList<string> ContributingFactors,
    IReadOnlyList<string> Timeline,
    ConfidenceLevel Confidence,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<string> InvolvedAgents,
    string? CausalChain,
    string? Counterfactual,
    IReadOnlyList<string> AffectedItems);

/// <summary>
/// Agente Indagine (blueprint §13): collega i segnali dei diversi agenti in una spiegazione coerente.
/// Per ogni tipo di problema segue un "playbook" (pattern §18): interroga gli specialisti, raccoglie prove a favore,
/// contro e mancanti per ipotesi concorrenti, ricostruisce la sequenza temporale e calibra la confidenza.
/// Deterministico: nessun numero viene da un LLM.
/// </summary>
public sealed class InvestigationAgent(IReadOnlyList<ISpecialistAgent> specialists)
{
    public string Name => AgentNames.Investigation;

    public InvestigationResult Investigate(AgentWorkspace ws, AgentFinding seed, IReadOnlyList<AgentFinding> related, List<AgentTraceStep> trace)
    {
        var start = DateTime.UtcNow;
        var result = seed.Signature switch
        {
            var s when s.StartsWith("productivity:below-expectation", StringComparison.Ordinal) => ProductivityPlaybook(ws, seed, trace),
            var s when s.StartsWith("productivity:mix-driven", StringComparison.Ordinal) => MixPlaybook(seed),
            var s when s.StartsWith("inventory:stockout", StringComparison.Ordinal) || s.StartsWith("inventory:depletion", StringComparison.Ordinal) => AvailabilityPlaybook(ws, seed, trace),
            var s when s.Contains(":adjustment", StringComparison.Ordinal) || s.StartsWith("inventory:integrity", StringComparison.Ordinal) => DiscrepancyPlaybook(ws, seed, trace),
            var s when s.EndsWith(":outbound", StringComparison.Ordinal) && (s.StartsWith("inventory:spike", StringComparison.Ordinal) || s.StartsWith("inventory:drop", StringComparison.Ordinal)) => DemandEventPlaybook(ws, seed, trace),
            _ => GenericPlaybook(seed)
        };

        var timeline = Timeline(seed, related, result.Timeline);
        trace.Add(new AgentTraceStep(Name, "conclusione",
            $"{result.ProbableRootCause ?? "nessuna causa prevalente"} (confidenza {Label(result.Confidence)})", (DateTime.UtcNow - start).TotalSeconds));
        return result with { Timeline = timeline };
    }

    // Pattern A — deterioramento operativo (produttività).
    private InvestigationResult ProductivityPlaybook(AgentWorkspace ws, AgentFinding seed, List<AgentTraceStep> trace)
    {
        var activity = seed.Signature.Split(':').Last();
        var q = (string aspect, IReadOnlyList<string> skus) => new AgentQuery(seed.Warehouse, aspect, skus, Activity: activity);

        var analysis = ws.Tasks.IsEmpty ? null : ProductivityAnalysis.Compute(ws.Tasks.For(new(seed.Warehouse), activity), ws.Window, seed.Warehouse, activity);
        var relocatedSkus = analysis?.Relocations.Select(r => r.Sku).ToList() ?? [];
        // La congestione si verifica nella zona di destinazione degli spostamenti, se ci sono; altrimenti ovunque.
        var targetZone = analysis?.Relocations.GroupBy(r => r.ToZone).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        var workload = Ask(ws, AgentNames.Productivity, q("workload", []), trace);
        var complexity = Ask(ws, AgentNames.Productivity, q("complexity", []), trace);
        var relocation = Ask(ws, AgentNames.Productivity, q("relocation", []), trace);
        var congestion = Ask(ws, AgentNames.Productivity, new AgentQuery(seed.Warehouse, "congestion", [], targetZone, activity), trace);
        var availability = Ask(ws, AgentNames.Inventory, new AgentQuery(seed.Warehouse, "availability", relocatedSkus), trace);

        var volumeChange = seed.Measures.GetValueOrDefault("lines_per_day");
        var hypotheses = new List<Hypothesis>();
        var relShare = relocation?.Measures.GetValueOrDefault("share_of_excess") ?? 0;
        var relCount = (int)(relocation?.Measures.GetValueOrDefault("relocated") ?? 0);
        var slowdown = congestion?.Measures.GetValueOrDefault("max_slowdown") ?? 0;
        var stockoutDays = availability?.Evidence.Count ?? 0;

        hypotheses.Add(Evaluate("relocation", $"Lo spostamento di {relCount} articoli ad alta rotazione ha allungato i percorsi di prelievo",
            support: relShare >= 0.4 ? [$"gli articoli spostati spiegano il {relShare:P0} dei minuti in eccesso", relocation!.Observation] : relCount > 0 ? [$"{relCount} articoli spostati ({relShare:P0} dei minuti in eccesso)"] : [],
            contra: relCount == 0 ? ["nessun articolo ha cambiato zona"] : [],
            missing: ["motivo e data dello spostamento nel WMS"], alternatives: 0,
            chain: "spostamento articoli → percorsi più lunghi → meno righe/ora → minore capacità di evasione"));
        hypotheses.Add(Evaluate("congestion", targetZone is null ? "Congestione in una zona di prelievo" : $"Congestione della zona {targetZone}, destinazione degli spostamenti",
            support: slowdown > 0.1 ? [congestion!.Observation] : [],
            contra: slowdown <= 0.05 && congestion is not null ? ["gli articoli non spostati non rallentano"] : [],
            missing: ["occupazione della zona e numero di operatori contemporanei"], alternatives: 1));
        hypotheses.Add(Evaluate("workload", "Aumento del carico di lavoro",
            support: workload?.WhyItMatters.Contains("cambiato") == true ? [workload.Observation] : [],
            contra: workload?.WhyItMatters.Contains("nella norma") == true ? [workload.Observation] : [],
            missing: [], alternatives: 1));
        // Il mix spiega il calo solo se, da solo, riduce la produttività.
        var mixEffect = analysis?.MixEffect ?? 0;
        hypotheses.Add(Evaluate("order-mix", "Ordini più complessi",
            support: mixEffect <= -0.03 && complexity is not null ? [complexity.Observation] : [],
            contra: mixEffect > -0.03 && complexity is not null ? [$"effetto del mix sulla produttività {Pct(mixEffect)}: non spiega un calo"] : [],
            missing: [], alternatives: 1));
        hypotheses.Add(Evaluate("availability", "Mancanza di stock sugli articoli prelevati (attese, prelievi a vuoto)",
            support: stockoutDays > 0 ? [availability!.Observation] : [],
            contra: stockoutDays == 0 && availability is not null ? [availability.Observation] : [],
            missing: [], alternatives: 1));
        hypotheses.Add(new Hypothesis("operators", "Minor rendimento degli operatori", HypothesisStatus.Candidate, ConfidenceLevel.Low, [],
            ["le condizioni operative (spostamenti, congestione) spiegano lo scostamento"],
            ["nessuna analisi per operatore: richiede prove forti dopo aver escluso le condizioni operative (equità)"]));

        var evidence = new[] { workload, complexity, relocation, congestion, availability }.OfType<AgentFinding>()
            .SelectMany(f => f.Evidence.Take(3).Prepend(Signal(f.Agent, f.Observation))).ToList();
        var timeline = new List<string>();
        if (relocation?.Measures.GetValueOrDefault("first_seen") is > 0 and var d)
            timeline.Add($"{DateOnly.FromDayNumber((int)d):dd/MM}: primi prelievi degli articoli spostati nella nuova zona");

        var counterfactual = relShare > 0
            ? $"Senza gli spostamenti, i minuti in eccesso sarebbero stati circa il {1 - relShare:P0} di quelli osservati: lo scostamento sarebbe in gran parte assente."
            : null;
        return Conclude(hypotheses, evidence, timeline, [AgentNames.Productivity, AgentNames.Inventory], counterfactual) with { AffectedItems = relocatedSkus };
    }

    private static InvestigationResult MixPlaybook(AgentFinding seed) =>
        Conclude(seed.Hypotheses.ToList(), seed.Evidence.ToList(), [], [AgentNames.Productivity],
            "A parità di mix degli ordini la produttività sarebbe stata in linea con la baseline.");

    // Pattern B — rischio di rottura di stock.
    private InvestigationResult AvailabilityPlaybook(AgentWorkspace ws, AgentFinding seed, List<AgentTraceStep> trace)
    {
        var sku = seed.Signature.Split('/').Last().Split(':')[0];
        var consumption = Ask(ws, AgentNames.Inventory, new AgentQuery(seed.Warehouse, "consumption", [sku]), trace);
        var inbound = Ask(ws, AgentNames.Inventory, new AgentQuery(seed.Warehouse, "inbound", [sku]), trace);
        var adjustments = Ask(ws, AgentNames.Inventory, new AgentQuery(seed.Warehouse, "adjustments", [sku]), trace);

        var demandUp = consumption?.WhyItMatters.Contains("più alta") == true;
        var noInbound = inbound?.Observation.StartsWith("Nessun ricevimento", StringComparison.Ordinal) == true;
        var hasAdjustments = adjustments?.Evidence.Count > 0;

        var hypotheses = new List<Hypothesis>
        {
            Evaluate("demand", "Domanda superiore al normale", demandUp ? [consumption!.Observation] : [],
                consumption is not null && !demandUp ? [consumption.Observation] : [], ["ordini clienti che hanno generato il picco"], 1,
                "domanda più alta → consumo più rapido → copertura esaurita"),
            Evaluate("replenishment", "Riassortimento fermo o in ritardo", noInbound ? [inbound!.Observation] : [],
                inbound is not null && !noInbound ? [inbound.Observation] : [], ["ordini fornitore e data di arrivo attesa (agente Inbound non ancora attivo)"], 1,
                "nessun ricevimento → giacenza non ricostituita → rottura"),
            Evaluate("accuracy", "Giacenza di sistema non affidabile", hasAdjustments ? [adjustments!.Observation] : [],
                adjustments is not null && !hasAdjustments ? [adjustments.Observation] : [], ["conteggio fisico dell'ubicazione"], 1)
        };
        var evidence = new[] { consumption, inbound, adjustments }.OfType<AgentFinding>().SelectMany(f => f.Evidence.Prepend(Signal(f.Agent, f.Observation))).ToList();
        return Conclude(hypotheses, evidence, [], [AgentNames.Inventory], null) with { AffectedItems = [sku] };
    }

    // Pattern D — discrepanze inventariali.
    private InvestigationResult DiscrepancyPlaybook(AgentWorkspace ws, AgentFinding seed, List<AgentTraceStep> trace)
    {
        var adjustments = Ask(ws, AgentNames.Inventory, new AgentQuery(seed.Warehouse, "adjustments", []), trace);
        var inbound = Ask(ws, AgentNames.Inventory, new AgentQuery(seed.Warehouse, "inbound", []), trace);
        var hypotheses = seed.Hypotheses.Select(h => h with
        {
            Missing = [.. h.Missing, "picking e ricevimenti degli stessi articoli nei giorni delle rettifiche"]
        }).ToList();
        var evidence = new[] { adjustments, inbound }.OfType<AgentFinding>().SelectMany(f => f.Evidence.Take(5).Prepend(Signal(f.Agent, f.Observation))).ToList();
        return Conclude(hypotheses, [.. seed.Evidence, .. evidence], [], [AgentNames.Inventory], null);
    }

    // Picchi o crolli di consumo: evento concentrato (promozione, grande ordine) o errore di dati?
    private InvestigationResult DemandEventPlaybook(AgentWorkspace ws, AgentFinding seed, List<AgentTraceStep> trace)
    {
        var share = seed.Measures.GetValueOrDefault("top_driver_share");
        var driver = seed.Drivers.FirstOrDefault();
        var availability = Ask(ws, AgentNames.Inventory, new AgentQuery(seed.Warehouse, "availability", []), trace);
        var concentrated = share >= 0.6 && driver is not null;

        var hypotheses = new List<Hypothesis>
        {
            Evaluate("demand-event", concentrated ? $"Evento di domanda concentrato sulla categoria {driver} (promozione o ordine eccezionale)" : "Evento di domanda diffuso",
                concentrated ? [$"la categoria {driver} spiega il {share:P0} della variazione"] : [],
                [], ["calendario promozioni e ordini clienti del giorno"], 1,
                concentrated ? $"domanda eccezionale su {driver} → uscite oltre il normale → copertura degli articoli ridotta" : null),
            Evaluate("data-duplicate", "Movimenti duplicati o caricati due volte", [],
                concentrated ? ["la variazione è concentrata in una categoria, non distribuita come un doppio caricamento"] : [],
                ["numeri documento duplicati"], 1),
            Evaluate("availability", "Mancanza di stock che ha spostato la domanda", availability?.Evidence.Count > 0 ? [availability.Observation] : [],
                availability is not null && availability.Evidence.Count == 0 ? [availability.Observation] : [], [], 1)
        };
        return Conclude(hypotheses, [.. seed.Evidence], [], [AgentNames.Inventory], null) with { AffectedItems = seed.Drivers };
    }

    private static InvestigationResult GenericPlaybook(AgentFinding seed) =>
        Conclude(seed.Hypotheses.ToList(), seed.Evidence.ToList(), [], [seed.Agent], null);

    private AgentFinding? Ask(AgentWorkspace ws, string agent, AgentQuery query, List<AgentTraceStep> trace)
    {
        var specialist = specialists.FirstOrDefault(s => s.Name == agent);
        if (specialist is null) return null;
        var start = DateTime.UtcNow;
        var answer = specialist.Examine(ws, query);
        trace.Add(new AgentTraceStep(agent, $"esame '{query.Aspect}' su {query.Warehouse}{(query.Skus.Count > 0 ? $" ({query.Skus.Count} articoli)" : "")}",
            answer?.Observation ?? "nessun dato", (DateTime.UtcNow - start).TotalSeconds));
        return answer;
    }

    private static Hypothesis Evaluate(string code, string statement, IReadOnlyList<string> support, IReadOnlyList<string> contra,
        IReadOnlyList<string> missing, int alternatives, string? chain = null)
    {
        var confidence = ConfidenceModel.Assess(support.Count, contra.Count, alternatives);
        var status = support.Count == 0 && contra.Count > 0 ? HypothesisStatus.Rejected
            : support.Count == 0 ? HypothesisStatus.Candidate
            : confidence == ConfidenceLevel.High ? HypothesisStatus.Confirmed
            : HypothesisStatus.Probable;
        return new Hypothesis(code, statement, status, confidence, support, contra, missing, chain);
    }

    /// <summary>Sceglie la spiegazione più sostenuta; le altre con prove a favore diventano fattori concomitanti.</summary>
    private static InvestigationResult Conclude(List<Hypothesis> hypotheses, List<EvidenceItem> evidence, List<string> timeline,
        IReadOnlyList<string> agents, string? counterfactual)
    {
        var ranked = hypotheses.Where(h => h.Status is HypothesisStatus.Confirmed or HypothesisStatus.Probable)
            .OrderByDescending(h => h.Status == HypothesisStatus.Confirmed)
            .ThenByDescending(h => h.Supporting.Count - h.Contradicting.Count)
            .ToList();
        var primary = ranked.FirstOrDefault();

        // Due spiegazioni ugualmente sostenute: la confidenza complessiva non può essere alta.
        var confidence = primary is null ? ConfidenceLevel.Low
            : ranked.Count > 1 && ranked[1].Supporting.Count >= primary.Supporting.Count && primary.Status != HypothesisStatus.Confirmed
                ? ConfidenceModel.Min(primary.Confidence, ConfidenceLevel.Medium)
                : primary.Confidence;

        var open = hypotheses.Where(h => h.Status != HypothesisStatus.Rejected).SelectMany(h => h.Missing).Distinct().ToList();
        if (primary is null) open.Insert(0, "Nessuna ipotesi è sufficientemente supportata: servono altri dati prima di raccomandare azioni.");

        return new InvestigationResult(
            hypotheses,
            primary?.Statement,
            ranked.Skip(1).Select(h => h.Statement).ToList(),
            timeline,
            confidence,
            open,
            evidence,
            agents,
            primary?.CausalChain,
            counterfactual,
            []);
    }

    private static IReadOnlyList<string> Timeline(AgentFinding seed, IReadOnlyList<AgentFinding> related, IReadOnlyList<string> extra)
    {
        var events = new List<(DateOnly Date, string Text)>();
        if (seed.FirstObserved is { } first) events.Add((first, $"prima deviazione: {seed.Title}"));
        foreach (var r in related.Where(r => r.Signature != seed.Signature && r.Warehouse == seed.Warehouse && r.FirstObserved is not null))
            events.Add((r.FirstObserved!.Value, $"nello stesso magazzino: {r.Title} ({AgentNames.Label(r.Agent)}) — coincidenza temporale, non causalità dimostrata"));
        return events.OrderBy(e => e.Date).Select(e => $"{e.Date:dd/MM}: {e.Text}").Concat(extra).ToList();
    }

    private static string Label(ConfidenceLevel c) => c switch { ConfidenceLevel.High => "alta", ConfidenceLevel.Medium => "media", _ => "bassa" };
}
