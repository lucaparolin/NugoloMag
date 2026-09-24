using NugoloMag.Analyst.Domain.Agentic;

namespace NugoloMag.Analyst.Application.Agentic;

public sealed record IncidentChange(Incident Incident, IncidentTransition Transition, AgentFinding? Finding);

/// <summary>
/// Ciclo di vita degli incidenti (blueprint §23): non ripete la stessa condizione invariata, comunica le transizioni
/// (nuovo, in peggioramento, in miglioramento, confidenza aumentata, risolto).
/// </summary>
public static class IncidentManager
{
    private const double WorseningFactor = 1.15;
    private const double ImprovingFactor = 0.85;

    public static IReadOnlyList<IncidentChange> Reconcile(
        string sourceName, IReadOnlyList<Incident> open, IReadOnlyList<AgentFinding> findings, IReadOnlyCollection<string> observedWarehouses, DateTimeOffset now)
    {
        var changes = new List<IncidentChange>();
        var bySignature = open.ToDictionary(i => i.Signature);

        foreach (var f in findings.GroupBy(f => f.Signature).Select(g => g.OrderByDescending(x => x.Intensity).First()))
        {
            if (!bySignature.TryGetValue(f.Signature, out var existing))
            {
                var score = SeverityModel.Score(f.Severity);
                changes.Add(new IncidentChange(new Incident
                {
                    SourceName = sourceName,
                    Warehouse = f.Warehouse,
                    Domain = f.Domain,
                    Signature = f.Signature,
                    Title = f.Title,
                    DetectedAt = now,
                    FirstObserved = f.FirstObserved ?? DateOnly.FromDateTime(now.Date),
                    UpdatedAt = now,
                    Status = IncidentStatus.Open,
                    Severity = SeverityModel.Level(score),
                    SeverityScore = score,
                    Confidence = f.Confidence,
                    Scope = f.Scope,
                    Signals = [f],
                    Evidence = f.Evidence,
                    Hypotheses = f.Hypotheses,
                    InvolvedAgents = [f.Agent],
                    LastIntensity = f.Intensity,
                    History = [new IncidentEvent(now, IncidentTransition.NewIssue, f.Observation)]
                }, IncidentTransition.NewIssue, f));
                continue;
            }

            var transition =
                f.Intensity > existing.LastIntensity * WorseningFactor ? IncidentTransition.Worsening :
                f.Intensity < existing.LastIntensity * ImprovingFactor ? IncidentTransition.Improving :
                f.Confidence > existing.Confidence ? IncidentTransition.ConfidenceIncreased :
                IncidentTransition.Unchanged;

            var updated = existing with
            {
                Title = f.Title,
                UpdatedAt = now,
                Signals = [f],
                Scope = f.Scope,
                LastIntensity = f.Intensity,
                Confidence = transition == IncidentTransition.ConfidenceIncreased ? f.Confidence : existing.Confidence,
                Status = transition switch
                {
                    IncidentTransition.Worsening => IncidentStatus.Worsening,
                    IncidentTransition.Improving => IncidentStatus.Improving,
                    _ => existing.Status == IncidentStatus.Resolved ? IncidentStatus.Open : existing.Status
                },
                History = transition == IncidentTransition.Unchanged
                    ? existing.History
                    : [.. existing.History, new IncidentEvent(now, transition, f.Observation)]
            };
            changes.Add(new IncidentChange(updated, transition, f));
        }

        // Condizioni non più osservate: risolte (solo se il magazzino è stato effettivamente analizzato in questo ciclo).
        var seen = findings.Select(f => f.Signature).ToHashSet();
        foreach (var i in open.Where(i => !seen.Contains(i.Signature) && observedWarehouses.Contains(i.Warehouse)))
        {
            changes.Add(new IncidentChange(i with
            {
                Status = IncidentStatus.Resolved,
                UpdatedAt = now,
                History = [.. i.History, new IncidentEvent(now, IncidentTransition.Resolved, "La condizione non è più osservata.")]
            }, IncidentTransition.Resolved, null));
        }
        return changes;
    }

    /// <summary>Regole di escalation (blueprint §4).</summary>
    public static (int Level, string? Reason) Escalation(Incident incident, IReadOnlyList<Incident> allOpen, AgenticSettings settings)
    {
        // Sistemico = stesso tipo di problema in più magazzini senza cause locali diverse
        // (due picchi spiegati da categorie diverse sono due eventi locali, non un problema comune).
        var pattern = string.Join(':', incident.Signature.Split(':').Take(2));
        var peers = allOpen.Where(i => string.Join(':', i.Signature.Split(':').Take(2)) == pattern && i.Warehouse != incident.Warehouse).ToList();
        var systemic = peers.Any(p => p.ProbableRootCause is null || incident.ProbableRootCause is null || p.ProbableRootCause == incident.ProbableRootCause);

        if (incident.Severity >= settings.EscalateToManagementFrom) return (2, "Severità critica: esposizione operativa o economica rilevante.");
        if (systemic && incident.Severity >= SeverityLevel.Medium) return (2, "Lo stesso problema è presente in più magazzini: possibile causa sistemica.");
        if (incident.Severity >= settings.EscalateToSupervisorFrom) return (1, "Severità alta: serve un intervento rapido.");
        if (incident.Confidence == ConfidenceLevel.Low && incident.Severity >= SeverityLevel.Medium) return (1, "Rischio rilevante con confidenza bassa: serve una verifica umana.");
        return (0, null);
    }
}
