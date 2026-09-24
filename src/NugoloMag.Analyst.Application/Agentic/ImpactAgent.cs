using NugoloMag.Analyst.Domain.Agentic;

namespace NugoloMag.Analyst.Application.Agentic;

/// <summary>
/// Agente Impatto (blueprint §14): traduce gli eventi operativi in conseguenze economiche, sempre come intervallo
/// e confrontando "nessun intervento" con "intervento raccomandato" quando la causa è nota.
/// </summary>
public sealed class ImpactAgent(AgenticSettings settings)
{
    public string Name => AgentNames.Impact;

    public IReadOnlyList<ImpactEstimate> Estimate(AgentFinding seed, InvestigationResult investigation)
    {
        var m = seed.Measures;
        var horizon = settings.ImpactHorizonDays;
        var list = new List<ImpactEstimate>();

        if (seed.Signature.StartsWith("productivity:below-expectation", StringComparison.Ordinal))
        {
            var hours = Math.Max(0, m.GetValueOrDefault("excess_minutes_per_day")) / 60;
            list.Add(new("Ore di lavoro in più al giorno", hours * 0.8, hours * 1.2, "ore", "minuti oltre l'atteso a parità di mix, media del periodo recente"));
            list.Add(new($"Costo in {horizon} giorni senza intervento", hours * 0.8 * settings.LaborCostPerHour * horizon, hours * 1.2 * settings.LaborCostPerHour * horizon, "€",
                $"{settings.LaborCostPerHour} €/ora di manodopera"));
            var linesPerDay = m.GetValueOrDefault("lines_per_day");
            var gap = -m.GetValueOrDefault("efficiency_gap");
            list.Add(new("Capacità di evasione persa", linesPerDay * gap * 0.8, linesPerDay * gap * 1.2, "righe/giorno", "righe non evase a pari ore lavorate"));
            if (investigation.Hypotheses.FirstOrDefault(h => h.Code == "relocation" && h.Status is HypothesisStatus.Confirmed or HypothesisStatus.Probable) is not null)
                list.Add(new("Recuperabile correggendo gli spostamenti", hours * 0.5 * settings.LaborCostPerHour * horizon, hours * 0.8 * settings.LaborCostPerHour * horizon, "€",
                    "quota dei minuti in eccesso attribuita agli articoli spostati"));
        }
        else if (seed.Signature.StartsWith("productivity:mix-driven", StringComparison.Ordinal))
        {
            list.Add(new("Capacità ridotta per maggiore complessità", -m.GetValueOrDefault("raw_change") * 0.8, -m.GetValueOrDefault("raw_change") * 1.2, "% righe/ora",
                "nessuna inefficienza: il lavoro richiesto è aumentato"));
        }
        else if (seed.Signature.StartsWith("inventory:stockout", StringComparison.Ordinal) || seed.Signature.StartsWith("inventory:depletion", StringComparison.Ordinal))
        {
            var demand = m.GetValueOrDefault("daily_demand");
            var cost = m.GetValueOrDefault("unit_cost");
            var onHand = m.GetValueOrDefault("on_hand");
            var shortLow = Math.Max(0, demand * 3 - onHand);
            var shortHigh = Math.Max(0, demand * 7 - onHand);
            list.Add(new("Unità a rischio (3–7 giorni di riassortimento)", shortLow, shortHigh, "unità", $"domanda {demand:0.#}/giorno, giacenza {onHand:0.#}"));
            list.Add(new("Valore delle vendite a rischio (a costo)", shortLow * cost, shortHigh * cost, "€", $"costo unitario {cost:0.##} €"));
        }
        else if (seed.Signature.StartsWith("inventory:overstock", StringComparison.Ordinal) || seed.Signature.StartsWith("inventory:dead-stock", StringComparison.Ordinal))
        {
            var value = m.GetValueOrDefault("value");
            list.Add(new("Capitale immobilizzato", value, value, "€", "giacenza × costo unitario"));
            list.Add(new("Costo di mantenimento annuo", value * 0.15, value * 0.25, "€", "15–25% annuo del valore a stock"));
        }
        else if (seed.Signature.EndsWith(":outbound", StringComparison.Ordinal) && m.GetValueOrDefault("value_exposed") is > 0 and var moved)
        {
            // Un picco di uscite non è una perdita: è valore movimentato, che conta per la copertura, non come esposizione.
            list.Add(new("Valore movimentato fuori norma (a costo)", moved * 0.8, moved * 1.2, "€ movimentati", "unità oltre l'atteso × costo unitario"));
        }
        else if (seed.Signature.Contains(":adjustment", StringComparison.Ordinal) && m.GetValueOrDefault("observed") >= m.GetValueOrDefault("baseline")
                 && m.GetValueOrDefault("value_exposed") is > 0 and var corrected)
        {
            // Rettifica positiva (merce "ritrovata"): problema di accuratezza, non una perdita economica.
            list.Add(new("Valore rettificato in aumento", corrected * 0.8, corrected * 1.2, "€ rettificati", "giacenza di sistema corretta verso l'alto: da verificare"));
        }
        else if (m.GetValueOrDefault("value_exposed") is > 0 and var exposed)
        {
            list.Add(new("Valore coinvolto", exposed * 0.8, exposed * 1.2, "€", "unità fuori norma × costo unitario"));
        }
        return list;
    }

    /// <summary>Fattore 0-1 per il modello di severità, dal valore economico relativo.</summary>
    public double BusinessImpactFactor(IReadOnlyList<ImpactEstimate> impacts, double fallback) =>
        impacts.FirstOrDefault(i => i.Unit == "€") is { } money ? Math.Min(1, Math.Max(fallback, money.High / settings.MaterialAmount)) : fallback;
}
