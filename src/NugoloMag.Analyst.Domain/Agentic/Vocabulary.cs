namespace NugoloMag.Analyst.Domain.Agentic;

/// <summary>Dominio operativo di un problema (blueprint §4, classificazione dell'intento).</summary>
public enum OperationalDomain { Inventory, Inbound, Outbound, Productivity, Capacity, Quality, Flow, Forecast, CrossFunctional }

/// <summary>Modello di severità (blueprint §19).</summary>
public enum SeverityLevel { Informational, Low, Medium, High, Critical }

/// <summary>Modello di confidenza (blueprint §20).</summary>
public enum ConfidenceLevel { Low, Medium, High }

/// <summary>
/// "Prove prima delle conclusioni" (blueprint §2.2): ogni affermazione dichiara di che natura è.
/// Un'ipotesi non viene mai presentata come causa confermata.
/// </summary>
public enum ClaimKind
{
    /// <summary>Osservato direttamente nei dati.</summary>
    Fact,
    /// <summary>Andamento statisticamente o operativamente rilevante.</summary>
    Signal,
    /// <summary>Spiegazione plausibile, non ancora provata.</summary>
    Hypothesis,
    /// <summary>Ipotesi sostenuta da prove sufficienti.</summary>
    ConfirmedCause
}

public enum HypothesisStatus { Candidate, Probable, Confirmed, Rejected }

/// <summary>Livello di approvazione richiesto da una raccomandazione (blueprint §15).</summary>
public enum ApprovalLevel { Informational, LowRiskAction, SupervisorApproval, ManagementDecision }

public enum IncidentStatus { Open, Worsening, Improving, Resolved }

/// <summary>Transizioni di stato comunicate al posto di avvisi ripetuti (blueprint §23).</summary>
public enum IncidentTransition { NewIssue, Worsening, ConfidenceIncreased, Improving, Resolved, Reopened, Unchanged }

public enum VerificationStatus { NotApplicable, Pending, Effective, Ineffective }

/// <summary>Feedback dell'utente su un incidente (blueprint §17).</summary>
public enum FeedbackVerdict { Useful, Irrelevant, Redundant, TooEarly, TooLate, ActionTaken, CauseConfirmed, CauseRejected }

/// <summary>Identificativi stabili degli agenti.</summary>
public static class AgentNames
{
    public const string Orchestrator = "orchestrator";
    public const string Inventory = "inventory";
    public const string Productivity = "productivity";
    public const string Investigation = "investigation";
    public const string Impact = "impact";
    public const string Recommendation = "recommendation";
    public const string Briefing = "briefing";
    public const string Learning = "learning";

    public static string Label(string agent) => agent switch
    {
        Orchestrator => "Orchestratore",
        Inventory => "Agente Inventario",
        Productivity => "Agente Produttività",
        Investigation => "Agente Indagine",
        Impact => "Agente Impatto",
        Recommendation => "Agente Raccomandazioni",
        Briefing => "Agente Briefing",
        Learning => "Agente Apprendimento",
        _ => agent
    };
}
