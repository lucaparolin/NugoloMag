using System.Diagnostics;
using System.Text;
using NugoloMag.Analyst.Application.Assistant;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Application.Monitoring;
using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Domain.Agentic;
using NugoloMag.Analyst.Domain.Monitoring;

namespace NugoloMag.Analyst.Application.Agentic;

/// <summary>La squadra di agenti che l'orchestratore coordina.</summary>
public sealed record AgentTeam(
    IReadOnlyList<ISpecialistAgent> Specialists,
    InvestigationAgent Investigation,
    ImpactAgent Impact,
    RecommendationAgent Recommendation,
    BriefingAgent Briefing,
    LearningAgent Learning);

public sealed record CycleResult(IReadOnlyList<IncidentChange> Changes, ExecutiveBriefing Briefing);

/// <summary>
/// Orchestratore di magazzino (blueprint §4): non fa analisi di dominio, coordina.
/// Ciclo proattivo: osserva → rileva → indaga → spiega → quantifica → raccomanda → verifica (§1),
/// e trasforma le domande degli utenti in indagini multi-agente (§24).
/// </summary>
public sealed class WarehouseOrchestrator(
    AgentTeam team,
    IIncidentStore incidents,
    IBriefingStore briefings,
    IInvestigationStore investigations,
    ISourceRegistry sources,
    IMonitorStore monitors,
    ISavedQueryStore savedQueries,
    AgenticSettings settings,
    IChatModel? model,
    TimeProvider clock,
    TimeZoneInfo zone)
{
    private const string Principles = """
        Principi del sistema (non negoziabili):
        - Prove prima delle conclusioni: distingui fatto (osservato), segnale (andamento rilevante), ipotesi (plausibile) e causa confermata. Mai presentare un'ipotesi come causa confermata.
        - Il contesto conta più delle soglie fisse; l'impatto conta più del rumore.
        - Raccomandazioni concrete: cosa, dove, su quale ambito, perché, effetto atteso, urgenza, confidenza, controindicazioni.
        - Non attribuire cali di prestazione agli operatori senza prove forti, dopo aver escluso le condizioni operative.
        - Se l'incertezza è alta, proponi la prossima indagine utile invece di fingere di sapere.
        - I numeri vengono SOLO dai risultati degli strumenti e dall'indagine fornita. Non inventarli.
        """;

    // ------------------------------------------------------------------ ciclo proattivo

    public async Task<CycleResult> RunCycleAsync(MonitorDefinition monitor, DateOnly asOf, CancellationToken ct = default)
    {
        var trace = new List<AgentTraceStep>();
        var open = await incidents.ListOpenAsync(monitor.SourceName, ct);
        var ws = await LoadWorkspaceAsync(monitor, asOf, open, ct);
        var now = clock.GetUtcNow();

        var findings = Observe(ws, team.Specialists, trace);
        var changes = IncidentManager.Reconcile(monitor.SourceName, open, findings,
            ws.Inventory.Warehouses.Select(w => w.Value).ToHashSet(), now);
        trace.Add(new AgentTraceStep(AgentNames.Orchestrator, "riconciliazione",
            string.Join(", ", changes.GroupBy(c => c.Transition).Select(g => $"{Transition(g.Key)}: {g.Count()}")), 0));

        // 1) indagine, impatto e raccomandazioni per ciò che è nuovo o cambiato
        var enriched = changes.Select(change =>
            change.Finding is { } finding && change.Transition is IncidentTransition.NewIssue or IncidentTransition.Worsening or IncidentTransition.ConfidenceIncreased
                ? change with { Incident = Enrich(change.Incident, finding, findings, ws, trace) }
                : change).ToList();

        // 2) escalation con il quadro completo (cause comprese), 3) apprendimento, 4) salvataggio
        var allOpenAfter = enriched.Where(c => c.Incident.IsOpen).Select(c => c.Incident).ToList();
        var saved = new List<IncidentChange>();
        foreach (var change in enriched)
        {
            var incident = change.Incident;
            var (level, reason) = IncidentManager.Escalation(incident, allOpenAfter, settings);
            incident = incident with { EscalationLevel = level, EscalationReason = reason };
            incident = await team.Learning.ApplyAsync(incident, change.Transition, ct);

            var id = await incidents.SaveAsync(incident, ct);
            saved.Add(change with { Incident = incident with { Id = id } });
        }

        var recent = await incidents.ListAsync(monitor.SourceName, includeResolved: true, take: 300, ct);
        var briefing = await team.Briefing.ComposeAsync(monitor.SourceName, asOf, now, recent, ct);
        briefing = briefing with { CycleTrace = trace };
        var briefingId = await briefings.SaveAsync(briefing, ct);
        return new CycleResult(saved, briefing with { Id = briefingId });
    }

    /// <summary>Indagine → impatto → raccomandazioni → severità ricalcolata con l'impatto e la confidenza dell'indagine.</summary>
    private Incident Enrich(Incident incident, AgentFinding finding, IReadOnlyList<AgentFinding> all, AgentWorkspace ws, List<AgentTraceStep> trace)
    {
        if (finding.Urgency < SeverityLevel.Low) return incident;

        var investigation = finding.Urgency >= SeverityLevel.Medium
            ? team.Investigation.Investigate(ws, finding, all, trace)
            : new InvestigationResult(finding.Hypotheses, null, [], [], finding.Confidence, [], finding.Evidence, [finding.Agent], null, null, []);
        var impact = team.Impact.Estimate(finding, investigation);
        var recommendations = team.Recommendation.Recommend(finding, investigation, impact, ws);
        trace.Add(new AgentTraceStep(AgentNames.Impact, "stima", impact.Count == 0 ? "impatto non quantificabile" : string.Join("; ", impact.Select(i => $"{i.Measure}: {i.Low:#,0}–{i.High:#,0} {i.Unit}")), 0));
        trace.Add(new AgentTraceStep(AgentNames.Recommendation, "azioni", string.Join(" | ", recommendations.Select(r => r.Action)), 0));

        var severityInputs = finding.Severity with
        {
            BusinessImpact = team.Impact.BusinessImpactFactor(impact, finding.Severity.BusinessImpact),
            Confidence = investigation.Confidence > finding.Confidence ? investigation.Confidence : finding.Confidence
        };
        var score = SeverityModel.Score(severityInputs);

        return incident with
        {
            Severity = SeverityModel.Level(score),
            SeverityScore = score,
            Confidence = investigation.Confidence > finding.Confidence ? investigation.Confidence : finding.Confidence,
            Evidence = [.. finding.Evidence, .. investigation.Evidence.Where(e => finding.Evidence.All(f => f.Statement != e.Statement))],
            Hypotheses = investigation.Hypotheses,
            ProbableRootCause = investigation.ProbableRootCause,
            ContributingFactors = investigation.ContributingFactors,
            Timeline = [.. investigation.Timeline, .. investigation.CausalChain is { } chain ? new[] { $"catena causale: {chain}" } : [],
                        .. investigation.Counterfactual is { } cf ? new[] { $"controfattuale: {cf}" } : []],
            Impact = impact,
            Recommendations = recommendations,
            InvolvedAgents = incident.InvolvedAgents.Union(investigation.InvolvedAgents).Union([AgentNames.Investigation, AgentNames.Impact, AgentNames.Recommendation]).ToList(),
            OpenQuestions = investigation.OpenQuestions,
            Narrative = Narrative(finding, investigation, impact, recommendations)
        };
    }

    // ------------------------------------------------------------------ domande degli utenti

    public async Task<long> AskAsync(string sourceName, string question, CancellationToken ct = default)
    {
        var trace = new List<AgentTraceStep>();
        var monitor = (await monitors.ListAsync(ct)).Where(m => m.IsActive && string.Equals(m.SourceName, sourceName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.CreatedAt).FirstOrDefault()
            ?? throw new InvalidOperationException("Per questa sorgente non c'è un monitoraggio attivo: prima analizza il database e attiva il monitoraggio.");

        var open = await incidents.ListOpenAsync(sourceName, ct);
        var asOf = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime).AddDays(-1);
        var ws = await LoadWorkspaceAsync(monitor, asOf, open, ct);

        var intent = IntentClassifier.Classify(question, ws);
        var plan = IntentClassifier.Plan(intent);
        trace.Add(new AgentTraceStep(AgentNames.Orchestrator, "classificazione",
            $"dominio {Domain(intent.Domain)}, domanda di tipo {intent.Kind}, magazzini: {(intent.Warehouses.Count == 0 ? "tutti" : string.Join(", ", intent.Warehouses))}", 0));
        trace.Add(new AgentTraceStep(AgentNames.Orchestrator, "piano", string.Join(" / ", plan), 0));

        var selected = team.Specialists.Where(s => intent.Domain switch
        {
            OperationalDomain.Productivity => s.Domain == OperationalDomain.Productivity,
            OperationalDomain.Inventory or OperationalDomain.Quality => s.Domain == OperationalDomain.Inventory,
            _ => true
        }).ToList();
        trace.Add(new AgentTraceStep(AgentNames.Orchestrator, "selezione agenti", string.Join(", ", selected.Select(s => AgentNames.Label(s.Name))), 0));

        var findings = Observe(ws, selected, trace)
            .Where(f => intent.Warehouses.Count == 0 || intent.Warehouses.Contains(f.Warehouse, StringComparer.OrdinalIgnoreCase))
            .Where(f => intent.Skus.Count == 0 || intent.Skus.Any(s => f.Signature.Contains(s, StringComparison.OrdinalIgnoreCase)) || f.Signature.StartsWith("productivity", StringComparison.Ordinal))
            .Where(f => intent.Domain is OperationalDomain.CrossFunctional or OperationalDomain.Productivity || f.Domain != OperationalDomain.Productivity)
            .OrderByDescending(f => SeverityModel.Score(f.Severity))
            .ToList();

        var seed = findings.FirstOrDefault(f => f.Urgency >= SeverityLevel.Low);
        InvestigationResult? investigation = null;
        IReadOnlyList<ImpactEstimate> impact = [];
        IReadOnlyList<Recommendation> recommendations = [];
        if (seed is not null)
        {
            investigation = team.Investigation.Investigate(ws, seed, findings, trace);
            impact = team.Impact.Estimate(seed, investigation);
            recommendations = team.Recommendation.Recommend(seed, investigation, impact, ws);
        }

        var answer = model is null
            ? TemplateAnswer(question, intent, findings, seed, investigation, impact, recommendations)
            : await LlmAnswerAsync(question, intent, plan, ws, selected, findings, open, seed, investigation, impact, recommendations, trace, ct);

        var result = new InvestigationCase
        {
            SourceName = sourceName,
            Question = question,
            CreatedAt = clock.GetUtcNow(),
            Domain = intent.Domain,
            Plan = plan,
            InvolvedAgents = selected.Select(s => s.Name).Concat(seed is null ? [] : [AgentNames.Investigation, AgentNames.Impact, AgentNames.Recommendation]).Distinct().ToList(),
            Findings = findings.Take(10).ToList(),
            Hypotheses = investigation?.Hypotheses ?? [],
            ProbableRootCause = investigation?.ProbableRootCause,
            Confidence = investigation?.Confidence ?? ConfidenceLevel.Medium,
            Impact = impact,
            Recommendations = recommendations,
            OpenQuestions = investigation?.OpenQuestions ?? [],
            Answer = answer,
            Trace = trace
        };
        return await investigations.SaveAsync(result, ct);
    }

    private async Task<string> LlmAnswerAsync(
        string question, QuestionIntent intent, IReadOnlyList<string> plan, AgentWorkspace ws, IReadOnlyList<ISpecialistAgent> selected,
        IReadOnlyList<AgentFinding> findings, IReadOnlyList<Incident> open, AgentFinding? seed, InvestigationResult? investigation,
        IReadOnlyList<ImpactEstimate> impact, IReadOnlyList<Recommendation> recommendations, List<AgentTraceStep> trace, CancellationToken ct)
    {
        var context = new StringBuilder();
        context.AppendLine($"Domanda dell'utente: {question}");
        context.AppendLine($"Periodo analizzato: baseline {ws.Window.BaselineStart:dd/MM}–{ws.Window.BaselineEnd:dd/MM}, recente {ws.Window.RecentStart:dd/MM}–{ws.Window.AsOf:dd/MM/yyyy}. Magazzini: {string.Join(", ", ws.Warehouses)}.");
        context.AppendLine("Piano: " + string.Join(" / ", plan));
        context.AppendLine("Segnali degli agenti:");
        foreach (var f in findings.Take(8)) context.AppendLine($"- [{f.Urgency}] {f.Observation}");
        if (seed is not null && investigation is not null)
        {
            context.AppendLine($"Indagine preliminare sul segnale principale ({seed.Title}):");
            foreach (var h in investigation.Hypotheses)
                context.AppendLine($"- ipotesi '{h.Statement}': {Status(h.Status)}, confidenza {Confidence(h.Confidence)}; a favore: {string.Join("; ", h.Supporting)}; contro: {string.Join("; ", h.Contradicting)}; mancano: {string.Join("; ", h.Missing)}");
            context.AppendLine("Le ipotesi SCARTATE non sono cause: non presentarle come tali. Le ipotesi DA VERIFICARE non sono dimostrate.");
            if (investigation.Counterfactual is { } cf) context.AppendLine($"- controfattuale: {cf}");
            foreach (var i in impact) context.AppendLine($"- impatto: {i.Measure} {i.Low:#,0}–{i.High:#,0} {i.Unit} ({i.Basis})");
            foreach (var r in recommendations) context.AppendLine($"- raccomandazione: {r.Action} — {r.ExpectedResult} (approvazione {r.Approval}, rischio: {r.Downside})");
        }

        IToolbox tools = new AgentToolbox(ws, selected, findings, open, trace);
        if (ws.Source is { } source)
            tools = new CompositeToolbox(tools, new DataAgentToolbox(source, await source.ReadCatalogAsync(ct), savedQueries, 0, clock));

        var system = $"""
            Sei l'Orchestratore di un sistema agentico di analisi dei magazzini. Coordini agenti specialisti deterministici
            (inventario, produttività, indagine, impatto, raccomandazioni) e puoi interrogarli con gli strumenti. Puoi anche
            eseguire query SQL in sola lettura sul gestionale se servono dati che gli agenti non coprono.
            {Principles}
            Rispondi in italiano, in testo semplice, con questa struttura:
            Risposta: (2-3 frasi dirette)
            Prove: (elenco con numeri presi dagli strumenti o dall'indagine, indicando se sono fatti o segnali)
            Causa più probabile: (con confidenza alta/media/bassa, oppure "non determinata")
            Impatto: (intervalli)
            Cosa fare: (azioni concrete o prossima indagine)
            Cosa resta aperto:
            """;

        var result = await ToolLoop.RunAsync(model!, system, [ChatMessage.User(context.ToString())], tools, maxRounds: 8, ct: ct);
        foreach (var step in result.Steps.Where(s => s.Call is not null && s.Call.Name is not "ask_agent"))
            trace.Add(new AgentTraceStep(AgentNames.Orchestrator, $"strumento {step.Call!.Name}", step.Outcome!.Content.Split('\n').LastOrDefault() ?? "", 0));
        trace.Add(new AgentTraceStep(AgentNames.Orchestrator, "risposta", $"{model!.Info.Provider}/{model.Info.Model}, {result.Steps.Count(s => s.Call is not null)} strumenti usati", 0));

        if (!result.Completed)
            return TemplateAnswer(question, intent, findings, seed, investigation, impact, recommendations) + $"\n\n(Modello linguistico non conclusivo: {result.FinalText})";

        // Il testo dell'LLM è accompagnato dal verdetto deterministico degli agenti: ciò che è stabilito resta verificabile.
        return investigation is null ? result.FinalText : result.FinalText.Trim() + "\n\n" + Verdict(investigation);
    }

    private static string Verdict(InvestigationResult inv)
    {
        var sb = new StringBuilder("Verifica degli agenti (deterministica):");
        foreach (var group in inv.Hypotheses.GroupBy(h => h.Status).OrderBy(g => g.Key))
            sb.Append($"\n- {Status(group.Key)}: {string.Join("; ", group.Select(h => h.Statement))}");
        return sb.ToString();
    }

    private static string Status(HypothesisStatus s) => s switch
    {
        HypothesisStatus.Confirmed => "CONFERMATA",
        HypothesisStatus.Probable => "PROBABILE",
        HypothesisStatus.Rejected => "SCARTATA",
        _ => "DA VERIFICARE"
    };

    private static string Confidence(ConfidenceLevel c) => c switch { ConfidenceLevel.High => "alta", ConfidenceLevel.Medium => "media", _ => "bassa" };

    private static string TemplateAnswer(string question, QuestionIntent intent, IReadOnlyList<AgentFinding> findings, AgentFinding? seed,
        InvestigationResult? inv, IReadOnlyList<ImpactEstimate> impact, IReadOnlyList<Recommendation> recs)
    {
        var sb = new StringBuilder();
        if (seed is null || inv is null)
        {
            sb.AppendLine(findings.Count == 0
                ? "Risposta: nel periodo analizzato gli agenti non rilevano deviazioni significative per questo ambito: i dati sono in linea con il normale per contesto."
                : "Risposta: ci sono solo segnali minori, nessuno rilevante: " + string.Join("; ", findings.Take(3).Select(f => f.Title)) + ".");
            return sb.ToString().Trim();
        }

        sb.AppendLine($"Risposta: {seed.Observation}");
        sb.AppendLine("Prove:");
        foreach (var e in inv.Evidence.Take(8)) sb.AppendLine($"- [{AgentToolbox.Kind(e.Kind)}] {e.Statement}");
        sb.AppendLine($"Causa più probabile: {inv.ProbableRootCause ?? "non determinata"} (confidenza {inv.Confidence switch { ConfidenceLevel.High => "alta", ConfidenceLevel.Medium => "media", _ => "bassa" }})");
        foreach (var c in inv.ContributingFactors) sb.AppendLine($"- fattore concomitante: {c}");
        if (inv.Counterfactual is { } cf) sb.AppendLine($"Controfattuale: {cf}");
        if (impact.Count > 0) sb.AppendLine("Impatto: " + string.Join("; ", impact.Select(i => $"{i.Measure} {i.Low:#,0}–{i.High:#,0} {i.Unit}")));
        if (recs.Count > 0) sb.AppendLine("Cosa fare: " + string.Join(" | ", recs.Select(r => r.Action)));
        if (inv.OpenQuestions.Count > 0) sb.AppendLine("Cosa resta aperto: " + string.Join("; ", inv.OpenQuestions.Take(4)));
        return sb.ToString().Trim();
    }

    // ------------------------------------------------------------------ supporto

    private static List<AgentFinding> Observe(AgentWorkspace ws, IEnumerable<ISpecialistAgent> agents, List<AgentTraceStep> trace)
    {
        var all = new List<AgentFinding>();
        foreach (var agent in agents)
        {
            var sw = Stopwatch.StartNew();
            var found = agent.Observe(ws);
            all.AddRange(found);
            trace.Add(new AgentTraceStep(agent.Name, "osservazione",
                found.Count == 0 ? "nessuna deviazione rilevante" : $"{found.Count} segnali: " + string.Join("; ", found.OrderByDescending(f => SeverityModel.Score(f.Severity)).Take(3).Select(f => f.Title)),
                sw.Elapsed.TotalSeconds));
        }
        return all;
    }

    private async Task<AgentWorkspace> LoadWorkspaceAsync(MonitorDefinition monitor, DateOnly asOf, IReadOnlyList<Incident> open, CancellationToken ct)
    {
        var source = sources.Get(monitor.SourceName);
        var window = new AnalysisWindow(asOf, monitor.BaselineDays, monitor.RecentDays);
        var rows = await source.Inventory(monitor.SourceQuery).LoadAsync(window.LoadFrom, asOf, ct);
        IReadOnlyList<WarehouseTask> tasks = monitor.TaskQuery is { } tq ? await source.Tasks(tq).LoadAsync(window.LoadFrom, asOf, ct) : [];
        return new AgentWorkspace(monitor.SourceName, window, new InventoryDataset(rows), new TaskDataset(tasks), clock.GetUtcNow(), open, source);
    }

    private static string Narrative(AgentFinding f, InvestigationResult inv, IReadOnlyList<ImpactEstimate> impact, IReadOnlyList<Recommendation> recs)
    {
        var parts = new List<string> { "Qualcosa è cambiato: " + f.Observation };
        if (f.Scope.Length > 0) parts.Add("Dove: " + f.Scope + ".");
        parts.Add(inv.ProbableRootCause is { } c ? $"Causa più probabile: {c}." : "La causa non è ancora determinata con prove sufficienti.");
        if (impact.FirstOrDefault() is { } i) parts.Add($"Impatto atteso: {i.Measure.ToLowerInvariant()} {i.Low:#,0}–{i.High:#,0} {i.Unit}.");
        if (recs.FirstOrDefault() is { } r) parts.Add($"Raccomandazione: {r.Action}.");
        return string.Join(" ", parts);
    }

    public static string Transition(IncidentTransition t) => t switch
    {
        IncidentTransition.NewIssue => "nuovi",
        IncidentTransition.Worsening => "in peggioramento",
        IncidentTransition.Improving => "in miglioramento",
        IncidentTransition.ConfidenceIncreased => "confidenza aumentata",
        IncidentTransition.Resolved => "risolti",
        IncidentTransition.Reopened => "riaperti",
        _ => "invariati"
    };

    public static string Domain(OperationalDomain d) => d switch
    {
        OperationalDomain.Inventory => "inventario",
        OperationalDomain.Productivity => "produttività",
        OperationalDomain.Quality => "qualità e accuratezza",
        OperationalDomain.CrossFunctional => "trasversale",
        _ => d.ToString().ToLowerInvariant()
    };
}
