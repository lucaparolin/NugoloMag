using NugoloMag.Analyst.Domain.Agentic;

namespace NugoloMag.Analyst.Application.Agentic;

/// <summary>
/// Agente Apprendimento (blueprint §17), versione iniziale: usa il feedback degli utenti per declassare avvisi giudicati
/// ripetutamente irrilevanti e verifica se le azioni eseguite hanno funzionato quando l'incidente evolve.
/// </summary>
public sealed class LearningAgent(IIncidentStore incidents, AgenticSettings settings, TimeProvider clock)
{
    public string Name => AgentNames.Learning;

    /// <summary>Registra il feedback e aggiorna subito l'incidente (verifica, conferma o rifiuto della causa).</summary>
    public async Task RecordAsync(IncidentFeedback feedback, CancellationToken ct = default)
    {
        await incidents.AddFeedbackAsync(feedback, ct);
        if (await incidents.GetAsync(feedback.IncidentId, ct) is not { } incident) return;

        var note = feedback.Note is { Length: > 0 } n ? $": {n}" : "";
        var updated = feedback.Verdict switch
        {
            FeedbackVerdict.ActionTaken => incident with { Verification = VerificationStatus.Pending, Outcome = "Azione eseguita, in attesa di verifica" + note },
            FeedbackVerdict.CauseConfirmed => incident with
            {
                Hypotheses = incident.Hypotheses.Select(h => h.Statement == incident.ProbableRootCause ? h with { Status = HypothesisStatus.Confirmed } : h).ToList(),
                Confidence = ConfidenceLevel.High
            },
            FeedbackVerdict.CauseRejected => incident with
            {
                Hypotheses = incident.Hypotheses.Select(h => h.Statement == incident.ProbableRootCause ? h with { Status = HypothesisStatus.Rejected } : h).ToList(),
                ProbableRootCause = null,
                Confidence = ConfidenceLevel.Low,
                OpenQuestions = [.. incident.OpenQuestions, "La causa proposta è stata respinta dall'utente" + note]
            },
            _ => incident
        };
        updated = updated with
        {
            History = [.. updated.History, new IncidentEvent(clock.GetUtcNow(), IncidentTransition.Unchanged, $"Feedback: {Label(feedback.Verdict)}{note}")]
        };
        await incidents.SaveAsync(updated, ct);
    }

    /// <summary>Applica ciò che si è imparato a un incidente appena aggiornato dal ciclo.</summary>
    public async Task<Incident> ApplyAsync(Incident incident, IncidentTransition transition, CancellationToken ct = default)
    {
        var result = incident;

        // Verifica dell'esito delle azioni (chiusura del ciclo: "l'azione ha funzionato?").
        if (incident.Verification == VerificationStatus.Pending)
        {
            if (transition == IncidentTransition.Resolved)
                result = result with { Verification = VerificationStatus.Effective, Outcome = "Azione eseguita e condizione rientrata." };
            else if (transition == IncidentTransition.Worsening)
                result = result with { Verification = VerificationStatus.Ineffective, Outcome = "Azione eseguita ma la condizione è peggiorata: rivedere la causa." };
        }
        else if (transition == IncidentTransition.Resolved && result.Outcome is null)
        {
            result = result with { Outcome = "Rientrato senza interventi registrati." };
        }

        // Avvisi ripetutamente giudicati irrilevanti: si abbassa la severità (salvo nuovi segnali di rischio).
        if (transition is IncidentTransition.NewIssue or IncidentTransition.Unchanged &&
            await incidents.DismissalsAsync(incident.SourceName, incident.Signature, ct) >= settings.DismissalsBeforeDemotion &&
            result.Severity > SeverityLevel.Informational)
        {
            result = result with
            {
                Severity = result.Severity - 1,
                History = [.. result.History, new IncidentEvent(clock.GetUtcNow(), IncidentTransition.Unchanged,
                    $"Severità ridotta: avvisi simili sono stati giudicati irrilevanti {settings.DismissalsBeforeDemotion} o più volte.")]
            };
        }
        return result;
    }

    public static string Label(FeedbackVerdict v) => v switch
    {
        FeedbackVerdict.Useful => "utile",
        FeedbackVerdict.Irrelevant => "irrilevante",
        FeedbackVerdict.Redundant => "ridondante",
        FeedbackVerdict.TooEarly => "troppo presto",
        FeedbackVerdict.TooLate => "troppo tardi",
        FeedbackVerdict.ActionTaken => "azione eseguita",
        FeedbackVerdict.CauseConfirmed => "causa confermata",
        _ => "causa respinta"
    };
}
