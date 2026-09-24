namespace NugoloMag.Analyst.Domain.Agentic;

public sealed record IncidentEvent(DateTimeOffset At, IncidentTransition Transition, string Note);

/// <summary>
/// Oggetto incidente condiviso da tutti gli agenti (blueprint §21). È anche il "caso" costruito dall'orchestratore (§4):
/// problema, severità, ambito, agenti coinvolti, prove, cause, confidenza, impatto, azioni, domande aperte.
/// </summary>
public sealed record Incident
{
    public long Id { get; init; }
    public required string SourceName { get; init; }
    public required string Warehouse { get; init; }
    public required OperationalDomain Domain { get; init; }
    public required string Signature { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
    public required DateOnly FirstObserved { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required IncidentStatus Status { get; init; }
    public required SeverityLevel Severity { get; init; }
    public required double SeverityScore { get; init; }
    public required ConfidenceLevel Confidence { get; init; }
    public required string Scope { get; init; }
    public required IReadOnlyList<AgentFinding> Signals { get; init; }
    public IReadOnlyList<EvidenceItem> Evidence { get; init; } = [];
    public IReadOnlyList<Hypothesis> Hypotheses { get; init; } = [];
    public string? ProbableRootCause { get; init; }
    public IReadOnlyList<string> ContributingFactors { get; init; } = [];
    public IReadOnlyList<string> Timeline { get; init; } = [];
    public IReadOnlyList<ImpactEstimate> Impact { get; init; } = [];
    public IReadOnlyList<Recommendation> Recommendations { get; init; } = [];
    public IReadOnlyList<string> InvolvedAgents { get; init; } = [];
    public IReadOnlyList<string> OpenQuestions { get; init; } = [];
    public string? Narrative { get; init; }
    public string? Owner { get; init; }
    /// <summary>0 = nessuna, 1 = responsabile di magazzino, 2 = direzione.</summary>
    public int EscalationLevel { get; init; }
    public string? EscalationReason { get; init; }
    public VerificationStatus Verification { get; init; } = VerificationStatus.NotApplicable;
    public string? Outcome { get; init; }
    public IReadOnlyList<IncidentEvent> History { get; init; } = [];
    /// <summary>Intensità dell'ultima osservazione: confrontata con la successiva per capire se peggiora o migliora.</summary>
    public double LastIntensity { get; init; }

    public bool IsOpen => Status != IncidentStatus.Resolved;
}

public sealed record IncidentFeedback(long IncidentId, DateTimeOffset At, FeedbackVerdict Verdict, string? Note, string? User);
