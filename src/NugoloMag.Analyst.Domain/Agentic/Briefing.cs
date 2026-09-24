namespace NugoloMag.Analyst.Domain.Agentic;

public enum TrendKind { New, Worsening, Stable, Improving, Resolved }

public sealed record BriefingItem(string Title, string Detail, SeverityLevel Severity, TrendKind Trend, string Warehouse, long? IncidentId);

/// <summary>Struttura standard del briefing direzionale (blueprint §16).</summary>
public sealed record ExecutiveBriefing
{
    public long Id { get; init; }
    public required string SourceName { get; init; }
    public required DateOnly AsOf { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required IReadOnlyList<string> ExecutiveSummary { get; init; }
    public required IReadOnlyList<BriefingItem> CriticalRisks { get; init; }
    public required IReadOnlyList<BriefingItem> PositiveSignals { get; init; }
    public required IReadOnlyList<BriefingItem> RootCauses { get; init; }
    public required IReadOnlyList<BriefingItem> RecommendedActions { get; init; }
    public required IReadOnlyList<BriefingItem> WatchList { get; init; }
    public IReadOnlyList<string> CrossWarehouse { get; init; } = [];
    public string? Narrative { get; init; }
    /// <summary>Traccia del ciclo di osservazione che ha prodotto il briefing (quali agenti, cosa hanno trovato).</summary>
    public IReadOnlyList<AgentTraceStep> CycleTrace { get; init; } = [];
}

/// <summary>Un passo registrato dell'orchestrazione: quale agente, perché, con quale esito (tracciabilità, §29 "Trust").</summary>
public sealed record AgentTraceStep(string Agent, string Action, string Detail, double Seconds);

/// <summary>Esito di una domanda dell'utente trasformata in indagine multi-agente (blueprint §24).</summary>
public sealed record InvestigationCase
{
    public long Id { get; init; }
    public required string SourceName { get; init; }
    public required string Question { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required OperationalDomain Domain { get; init; }
    public required IReadOnlyList<string> Plan { get; init; }
    public required IReadOnlyList<string> InvolvedAgents { get; init; }
    public required IReadOnlyList<AgentFinding> Findings { get; init; }
    public IReadOnlyList<Hypothesis> Hypotheses { get; init; } = [];
    public string? ProbableRootCause { get; init; }
    public required ConfidenceLevel Confidence { get; init; }
    public IReadOnlyList<ImpactEstimate> Impact { get; init; } = [];
    public IReadOnlyList<Recommendation> Recommendations { get; init; } = [];
    public IReadOnlyList<string> OpenQuestions { get; init; } = [];
    public required string Answer { get; init; }
    public required IReadOnlyList<AgentTraceStep> Trace { get; init; }
}
