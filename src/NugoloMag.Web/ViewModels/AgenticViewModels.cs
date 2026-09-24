using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Infrastructure.Llm;
using NugoloMag.Analyst.Domain.Agentic;
using NugoloMag.Analyst.Domain.Monitoring;

namespace NugoloMag.Web.ViewModels;

public sealed record OperationsViewModel(IReadOnlyList<Incident> Open, IReadOnlyList<Incident> Resolved);

public sealed record IncidentViewModel(Incident Incident, IReadOnlyList<IncidentFeedback> Feedback, string? Message = null);

public sealed record BriefingViewModel(
    ExecutiveBriefing? Briefing,
    IReadOnlyList<(long Id, string SourceName, DateOnly AsOf, DateTimeOffset CreatedAt)> History,
    IReadOnlyList<MonitorDefinition> Monitors,
    string? Error = null);

public sealed record InvestigationsViewModel(
    IReadOnlyList<InvestigationCase> Items,
    IReadOnlyList<string> Sources,
    ChatModelInfo? Model,
    string? Error = null,
    string? Draft = null);

/// <summary>Etichette italiane del vocabolario agentico (mappatura esplicita).</summary>
public static class AgenticLabels
{
    public static (string Css, string Text) Severity(SeverityLevel s) => s switch
    {
        SeverityLevel.Critical => ("crit", "Critico"),
        SeverityLevel.High => ("fail", "Alto"),
        SeverityLevel.Medium => ("warn", "Medio"),
        SeverityLevel.Low => ("muted", "Basso"),
        _ => ("muted", "Informativo")
    };

    public static string Confidence(ConfidenceLevel c) => c switch { ConfidenceLevel.High => "alta", ConfidenceLevel.Medium => "media", _ => "bassa" };

    public static (string Css, string Text) Status(IncidentStatus s) => s switch
    {
        IncidentStatus.Worsening => ("fail", "in peggioramento"),
        IncidentStatus.Improving => ("ok", "in miglioramento"),
        IncidentStatus.Resolved => ("ok", "risolto"),
        _ => ("muted", "aperto")
    };

    public static string Transition(IncidentTransition t) => t switch
    {
        IncidentTransition.NewIssue => "Nuovo problema rilevato",
        IncidentTransition.Worsening => "Problema in peggioramento",
        IncidentTransition.ConfidenceIncreased => "Confidenza sulla causa aumentata",
        IncidentTransition.Improving => "Situazione in miglioramento",
        IncidentTransition.Resolved => "Problema risolto",
        IncidentTransition.Reopened => "Problema riaperto",
        _ => "Nota"
    };

    public static (string Css, string Text) Claim(ClaimKind k) => k switch
    {
        ClaimKind.Fact => ("fact", "fatto"),
        ClaimKind.Signal => ("signal", "segnale"),
        ClaimKind.Hypothesis => ("hyp", "ipotesi"),
        _ => ("ok", "causa confermata")
    };

    public static (string Css, string Text) Hypothesis(HypothesisStatus s) => s switch
    {
        HypothesisStatus.Confirmed => ("ok", "confermata"),
        HypothesisStatus.Probable => ("warn", "probabile"),
        HypothesisStatus.Rejected => ("muted", "scartata"),
        _ => ("muted", "da verificare")
    };

    public static string Approval(ApprovalLevel a) => a switch
    {
        ApprovalLevel.Informational => "Informativa",
        ApprovalLevel.LowRiskAction => "Azione a basso rischio",
        ApprovalLevel.SupervisorApproval => "Richiede il responsabile di magazzino",
        _ => "Decisione della direzione"
    };

    public static string Domain(OperationalDomain d) => d switch
    {
        OperationalDomain.Inventory => "Inventario",
        OperationalDomain.Productivity => "Produttività",
        OperationalDomain.Quality => "Qualità e accuratezza",
        OperationalDomain.Inbound => "Ricevimento",
        OperationalDomain.Outbound => "Spedizione",
        OperationalDomain.Capacity => "Spazi e capacità",
        OperationalDomain.Flow => "Flussi",
        OperationalDomain.Forecast => "Previsione",
        _ => "Trasversale"
    };

    public static string Verification(VerificationStatus v) => v switch
    {
        VerificationStatus.Pending => "azione eseguita, verifica in corso",
        VerificationStatus.Effective => "azione efficace",
        VerificationStatus.Ineffective => "azione non efficace",
        _ => ""
    };

    public static string Escalation(int level) => level switch { 2 => "Direzione", 1 => "Responsabile di magazzino", _ => "" };

    public static string Trend(TrendKind t) => t switch
    {
        TrendKind.New => "nuovo",
        TrendKind.Worsening => "in peggioramento",
        TrendKind.Improving => "in miglioramento",
        TrendKind.Resolved => "risolto",
        _ => "persistente"
    };

    public static readonly (FeedbackVerdict Verdict, string Label)[] Verdicts =
    [
        (FeedbackVerdict.Useful, "Utile"),
        (FeedbackVerdict.ActionTaken, "Ho eseguito l'azione"),
        (FeedbackVerdict.CauseConfirmed, "Causa confermata"),
        (FeedbackVerdict.CauseRejected, "Causa sbagliata"),
        (FeedbackVerdict.Irrelevant, "Irrilevante"),
        (FeedbackVerdict.Redundant, "Ridondante"),
        (FeedbackVerdict.TooEarly, "Troppo presto"),
        (FeedbackVerdict.TooLate, "Troppo tardi")
    ];

    public static string Code(FeedbackVerdict v) => v switch
    {
        FeedbackVerdict.Useful => "useful", FeedbackVerdict.Irrelevant => "irrelevant", FeedbackVerdict.Redundant => "redundant",
        FeedbackVerdict.TooEarly => "too-early", FeedbackVerdict.TooLate => "too-late", FeedbackVerdict.ActionTaken => "action-taken",
        FeedbackVerdict.CauseConfirmed => "cause-confirmed", _ => "cause-rejected"
    };

    public static FeedbackVerdict? Parse(string? code) => Verdicts.Select(v => (FeedbackVerdict?)v.Verdict).FirstOrDefault(v => Code(v!.Value) == code);

    public static string Range(ImpactEstimate i)
    {
        var it = System.Globalization.CultureInfo.GetCultureInfo("it-IT");
        string F(double v) => i.Unit.StartsWith('%') ? v.ToString("P0", it) : v.ToString(Math.Abs(v) < 10 ? "#,0.#" : "#,0", it);
        var unit = i.Unit.StartsWith('%') ? "" : " " + i.Unit;
        return Math.Abs(i.High - i.Low) < 1e-9 ? $"{F(i.Low)}{unit}" : $"{F(i.Low)} – {F(i.High)}{unit}";
    }
}

public sealed record BriefingSection(IReadOnlyList<BriefingItem> Items, string Empty);

public sealed record ConnectorRow(LlmOptions Options, bool IsDefault, IReadOnlyList<string> Agents, string KeyStatus);

public sealed record AgentRouteRow(string AgentId, string SkillName, string Description, string Connector, string Origin, string? Error);

public sealed record ConnectorsViewModel(
    IReadOnlyList<ConnectorRow> Connectors,
    IReadOnlyList<AgentRouteRow> Routes,
    IReadOnlyList<Skill> Skills,
    string? SkillsRoot,
    ConnectorReport? Report,
    string? Error);
