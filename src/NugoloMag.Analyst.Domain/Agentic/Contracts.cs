namespace NugoloMag.Analyst.Domain.Agentic;

/// <summary>Una prova: cosa si afferma, di che natura è, chi l'ha prodotta e su quali dati si basa.</summary>
public sealed record EvidenceItem(
    ClaimKind Kind,
    string Statement,
    string Agent,
    IReadOnlyDictionary<string, string> Data);

/// <summary>Ipotesi con prove a favore, contro e mancanti (blueprint §13, verifica delle ipotesi).</summary>
public sealed record Hypothesis(
    string Code,
    string Statement,
    HypothesisStatus Status,
    ConfidenceLevel Confidence,
    IReadOnlyList<string> Supporting,
    IReadOnlyList<string> Contradicting,
    IReadOnlyList<string> Missing,
    string? CausalChain = null);

/// <summary>Stima d'impatto come intervallo, non falsa precisione (blueprint §14).</summary>
public sealed record ImpactEstimate(string Measure, double Low, double High, string Unit, string Basis);

/// <summary>Raccomandazione azionabile (blueprint §2.5 e §15).</summary>
public sealed record Recommendation(
    string Action,
    string Scope,
    string Reason,
    string ExpectedResult,
    SeverityLevel Urgency,
    ConfidenceLevel Confidence,
    string Downside,
    ApprovalLevel Approval,
    bool Reversible,
    string Agent);

/// <summary>Un'attivazione suggerita di un altro agente, con il motivo.</summary>
public sealed record NextStep(string Agent, string Reason);

/// <summary>
/// Contratto di risposta standard di ogni agente specialista (blueprint §22).
/// I fattori numerici alimentano il modello di severità; <see cref="Intensity"/> confronta la stessa condizione tra un ciclo e l'altro.
/// </summary>
public sealed record AgentFinding
{
    public required string Agent { get; init; }
    public required OperationalDomain Domain { get; init; }
    /// <summary>Chiave stabile della condizione (es. "inventory:stockout-risk:RM02:RM-012"): serve a non ripetere lo stesso avviso.</summary>
    public required string Signature { get; init; }
    public required string Warehouse { get; init; }
    public required string Title { get; init; }
    public required string Observation { get; init; }
    public required string WhyItMatters { get; init; }
    public required IReadOnlyList<EvidenceItem> Evidence { get; init; }
    public required string Baseline { get; init; }
    public required string Deviation { get; init; }
    public required string Scope { get; init; }
    public IReadOnlyList<Hypothesis> Hypotheses { get; init; } = [];
    public required ConfidenceLevel Confidence { get; init; }
    public IReadOnlyList<NextStep> SuggestedNext { get; init; } = [];
    public required SeverityInputs Severity { get; init; }
    public required double Intensity { get; init; }
    public DateOnly? FirstObserved { get; init; }
    /// <summary>Valori utili agli altri agenti (es. "units_at_risk", "daily_demand", "unit_cost").</summary>
    public IReadOnlyDictionary<string, double> Measures { get; init; } = new Dictionary<string, double>();
    /// <summary>Segmenti che spiegano la variazione (es. categorie), se l'agente li ha individuati.</summary>
    public IReadOnlyList<string> Drivers { get; init; } = [];

    public SeverityLevel Urgency => SeverityModel.Level(Severity);
}
