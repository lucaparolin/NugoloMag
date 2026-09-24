using NugoloMag.Analyst.Application;
using NugoloMag.Analyst.Application.Agentic;
using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Domain.Agentic;
using NugoloMag.Analyst.Domain.Discovery;
using NugoloMag.Analyst.Domain.Monitoring;
using NugoloMag.Analyst.Infrastructure.Demo;
using Xunit.Abstractions;
using static NugoloMag.Analyst.Tests.Builders;

namespace NugoloMag.Analyst.Tests;

public class OrchestratorTests(ITestOutputHelper output)
{
    private static readonly MonitorDefinition Monitor = new()
    {
        Id = 1, Name = "demo", SourceName = "demo", DiscoveryId = 1, SourceQuery = "q", TaskQuery = "t",
        RunAt = new TimeOnly(6, 0), BaselineDays = 28, RecentDays = 7, IsActive = true, CreatedAt = DateTimeOffset.UtcNow,
        Mapping = new SourceMapping(new MovementSource(new TableName("dbo", "M"), "D", null, "S", MovementDirection.SignedQuantity, "Q", null, null, null, [], [], []), null, null)
    };

    private sealed record Harness(WarehouseOrchestrator Orchestrator, InMemoryIncidentStore Incidents, InMemoryBriefingStore Briefings,
        InMemoryInvestigationStore Investigations, FakeSourceDatabase Source, LearningAgent Learning);

    private static Harness Build(IChatModel? model = null)
    {
        var rows = new SyntheticInventoryGenerator().Generate(AsOf, days: 60);
        var tasks = new SyntheticTaskGenerator().Generate(rows, AsOf).Select(t => t.Task).ToList();
        var source = new FakeSourceDatabase(rows, tasks);
        var settings = new AgenticSettings();
        var incidents = new InMemoryIncidentStore();
        var specialists = new ISpecialistAgent[] { new InventoryAgent(new DetectionSettings(), settings), new ProductivityAgent(settings) };
        var learning = new LearningAgent(incidents, settings, TimeProvider.System);
        var team = new AgentTeam(specialists, new InvestigationAgent(specialists), new ImpactAgent(settings), new RecommendationAgent(), new BriefingAgent(model), learning);
        var briefings = new InMemoryBriefingStore();
        var investigations = new InMemoryInvestigationStore();
        var orchestrator = new WarehouseOrchestrator(team, incidents, briefings, investigations, source, new SingleMonitorStore(Monitor),
            new NoSavedQueries(), settings, model, TimeProvider.System, TimeZoneInfo.Utc);
        return new Harness(orchestrator, incidents, briefings, investigations, source, learning);
    }

    [Fact]
    public async Task Proactive_cycle_builds_evidence_based_incidents_and_a_briefing()
    {
        var h = Build();
        var cycle = await h.Orchestrator.RunCycleAsync(Monitor, AsOf);

        foreach (var i in h.Incidents.All.OrderByDescending(i => i.SeverityScore))
            output.WriteLine($"[{i.Severity} {i.SeverityScore}] {i.Signature} {i.Signals[0].Severity} impact={string.Join(",", i.Impact.Select(x => $"{x.Measure}:{x.High:0}"))}");

        // Blueprint §30: la produttività di MI01 cala per lo spostamento degli articoli in zona C.
        var mi = h.Incidents.All.Single(i => i.Signature == "productivity:below-expectation:MI01:PICK");
        Assert.Contains("spostamento", mi.ProbableRootCause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mi.Hypotheses, h1 => h1.Code == "workload" && h1.Status == HypothesisStatus.Rejected);
        Assert.Contains(mi.Hypotheses, h1 => h1.Code == "operators" && h1.Status != HypothesisStatus.Confirmed);
        Assert.Contains(mi.Hypotheses, h1 => h1.Code == "order-mix" && h1.Status == HypothesisStatus.Rejected);
        Assert.Contains(mi.Hypotheses, h1 => h1.Code == "congestion" && h1.Statement.Contains("zona C"));
        Assert.Contains(mi.Impact, x => x.Unit == "€" && x.High > x.Low);
        Assert.Contains(mi.Recommendations, r => r.Approval == ApprovalLevel.SupervisorApproval && r.Scope.Contains("MI-"));
        Assert.True(mi.Severity >= SeverityLevel.High);
        Assert.True(mi.EscalationLevel >= 1);
        Assert.Contains(mi.Timeline, t => t.Contains("catena causale"));
        Assert.Contains(AgentNames.Investigation, mi.InvolvedAgents);

        // Equità: RM02 cala per la complessità, non viene attribuito agli operatori.
        var rm = h.Incidents.All.Single(i => i.Signature == "productivity:mix-driven:RM02:PICK");
        Assert.True(rm.Severity <= SeverityLevel.Low);
        Assert.Contains(rm.Hypotheses, h1 => h1.Code == "operators" && h1.Status == HypothesisStatus.Rejected);

        Assert.Contains(h.Incidents.All, i => i.Signature.StartsWith("inventory:stockout:RM02/", StringComparison.Ordinal));

        var briefing = cycle.Briefing;
        Assert.NotEmpty(briefing.ExecutiveSummary);
        Assert.Contains(briefing.CriticalRisks, r => r.Title.Contains("MI01"));
        Assert.Contains(briefing.RecommendedActions, r => r.Warehouse == "MI01");
        Assert.Contains(briefing.PositiveSignals, p => p.Warehouse == "RM02");
        Assert.Contains(briefing.CycleTrace, t => t.Agent == AgentNames.Productivity);
    }

    [Fact]
    public async Task Unchanged_conditions_are_not_reported_twice_and_resolved_ones_are_verified()
    {
        var h = Build();
        await h.Orchestrator.RunCycleAsync(Monitor, AsOf);
        var count = h.Incidents.All.Count;

        var second = await h.Orchestrator.RunCycleAsync(Monitor, AsOf);
        Assert.Equal(count, h.Incidents.All.Count);
        Assert.All(second.Changes, c => Assert.Equal(IncidentTransition.Unchanged, c.Transition));

        // L'operatore segnala di aver riportato gli articoli in zona A; al ciclo dopo la condizione è rientrata.
        var mi = h.Incidents.All.Single(i => i.Signature == "productivity:below-expectation:MI01:PICK");
        await h.Learning.RecordAsync(new IncidentFeedback(mi.Id, DateTimeOffset.UtcNow, FeedbackVerdict.ActionTaken, "articoli ricollocati", "test"));
        h.Source.TaskRows = new SyntheticTaskGenerator(injectScenarios: false).Generate(h.Source.Rows, AsOf).Select(t => t.Task).ToList();

        var third = await h.Orchestrator.RunCycleAsync(Monitor, AsOf);
        var resolved = h.Incidents.All.Single(i => i.Id == mi.Id);
        Assert.Equal(IncidentStatus.Resolved, resolved.Status);
        Assert.Equal(VerificationStatus.Effective, resolved.Verification);
        Assert.Contains(third.Briefing.PositiveSignals, p => p.IncidentId == mi.Id);
    }

    [Fact]
    public async Task Repeatedly_dismissed_alerts_are_demoted()
    {
        var h = Build();
        await h.Orchestrator.RunCycleAsync(Monitor, AsOf);
        var target = h.Incidents.All.Where(i => i.Signature.StartsWith("inventory:", StringComparison.Ordinal) && i.Severity > SeverityLevel.Informational).First();
        foreach (var _ in Enumerable.Range(0, 2))
            await h.Learning.RecordAsync(new IncidentFeedback(target.Id, DateTimeOffset.UtcNow, FeedbackVerdict.Irrelevant, "ciclo promozionale noto", "test"));

        await h.Orchestrator.RunCycleAsync(Monitor, AsOf);
        var after = h.Incidents.All.Single(i => i.Id == target.Id);
        Assert.True(after.Severity < target.Severity);
        Assert.Contains(after.History, e => e.Note.Contains("Severità ridotta"));
    }

    [Fact]
    public async Task Questions_become_multi_agent_investigations_without_an_llm()
    {
        var h = Build();
        var id = await h.Orchestrator.AskAsync("demo", "Perché la produttività di MI01 è calata?");
        var result = h.Investigations.Items.Single(i => i.Id == id);
        output.WriteLine(result.Answer);

        Assert.Equal(OperationalDomain.Productivity, result.Domain);
        Assert.Contains(result.Plan, p => p.Contains("spostati"));
        Assert.Contains("spostamento", result.ProbableRootCause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Trace, t => t.Agent == AgentNames.Inventory && t.Action.Contains("availability"));
        Assert.StartsWith("Risposta:", result.Answer);
    }

    [Fact]
    public async Task With_an_llm_the_orchestrator_lets_it_query_agents_as_tools()
    {
        var model = new ScriptedChatModel(true,
            new ChatResponse("", [new ToolCall("c1", "ask_agent", """{"agent":"productivity","aspect":"congestion","warehouse":"MI01"}""")], ChatStop.ToolUse),
            new ChatResponse("Risposta: la zona C è congestionata e gli articoli spostati allungano i percorsi.", [], ChatStop.EndTurn));
        var h = Build(model);

        var id = await h.Orchestrator.AskAsync("demo", "Perché il picking di MI01 è più lento?");
        var result = h.Investigations.Items.Single(i => i.Id == id);

        Assert.Contains("congestionata", result.Answer);
        Assert.Contains(result.Trace, t => t.Action.Contains("richiesto dall'LLM"));
        Assert.Contains(model.Requests[0].Tools, t => t.Name == "run_query");         // SQL in sola lettura disponibile
        Assert.Contains("Indagine preliminare", model.Requests[0].Messages[0].Text);  // l'LLM parte dalle prove deterministiche
        Assert.Contains("tool", model.Requests[1].Messages.Last().Role.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
