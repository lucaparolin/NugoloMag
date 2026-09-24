using NugoloMag.Analyst.Domain.Agentic;

namespace NugoloMag.Analyst.Application.Agentic;

/// <summary>
/// Agente Raccomandazioni (blueprint §15, §26): azioni concrete (cosa, dove, su quale ambito, perché, effetto atteso,
/// urgenza, confidenza, controindicazioni, livello di approvazione). Con confidenza bassa propone la prossima indagine utile,
/// non un'azione.
/// </summary>
public sealed class RecommendationAgent
{
    public string Name => AgentNames.Recommendation;

    public IReadOnlyList<Recommendation> Recommend(AgentFinding seed, InvestigationResult investigation, IReadOnlyList<ImpactEstimate> impact, AgentWorkspace ws)
    {
        var urgency = seed.Urgency;

        // Azioni a basso rischio e reversibili che sono esse stesse verifiche: si propongono anche con confidenza bassa (§20).
        if (seed.Signature.Contains(":adjustment", StringComparison.Ordinal) || seed.Signature.StartsWith("inventory:integrity", StringComparison.Ordinal))
            return [new Recommendation("Inventario a rotazione (cycle count) sugli articoli coinvolti e verifica dei movimenti dei giorni interessati",
                seed.Scope, seed.Observation, "Giacenze di sistema riallineate e causa della differenza individuata.", urgency, ConfidenceLevel.Medium,
                "Tempo operatore per i conteggi.", ApprovalLevel.LowRiskAction, Reversible: true, Name)];
        if (seed.Signature.StartsWith("inventory:data-freshness", StringComparison.Ordinal))
            return [new Recommendation("Verificare il caricamento dei dati del magazzino prima di prendere decisioni", seed.Scope, seed.Observation,
                "Analisi di nuovo affidabili.", SeverityLevel.High, ConfidenceLevel.High, "Nessuna.", ApprovalLevel.LowRiskAction, Reversible: true, Name)];

        if (investigation.Confidence == ConfidenceLevel.Low && !seed.Signature.StartsWith("inventory:stockout", StringComparison.Ordinal))
            return [NextInvestigation(seed, investigation, urgency)];

        var list = new List<Recommendation>();
        var confirmed = investigation.Hypotheses.Where(h => h.Status is HypothesisStatus.Confirmed or HypothesisStatus.Probable).Select(h => h.Code).ToHashSet();
        var money = impact.FirstOrDefault(i => i.Unit == "€");

        switch (seed.Signature.Split(':')[0] + ":" + seed.Signature.Split(':')[1])
        {
            case "productivity:below-expectation":
                if (confirmed.Contains("relocation"))
                {
                    var relocated = investigation.AffectedItems.Take(12).ToList();
                    list.Add(new Recommendation(
                        $"Riportare nelle ubicazioni di prelievo vicine i {relocated.Count} articoli spostati con più righe",
                        $"{seed.Warehouse}: {string.Join(", ", relocated)}",
                        "Gli articoli spostati spiegano la maggior parte dei minuti in eccesso.",
                        money is null ? "Recupero di buona parte della produttività persa." : $"Recupero stimato {money.Low:#,0}–{money.High:#,0} € nei prossimi giorni.",
                        urgency, investigation.Confidence, "Il lavoro di ricollocazione riduce temporaneamente la capacità: pianificarlo fuori dai picchi.",
                        ApprovalLevel.SupervisorApproval, Reversible: true, Name));
                }
                if (confirmed.Contains("congestion"))
                    list.Add(new Recommendation(
                        "Ribilanciare temporaneamente il prelievo spostando parte del lavoro fuori dalla zona congestionata (ondate sfalsate, meno operatori contemporanei)",
                        $"{seed.Warehouse}, zona indicata dall'indagine", "Anche gli articoli non spostati rallentano: la zona è congestionata.",
                        "Tempi per riga della zona di nuovo vicini alla baseline finché la congestione rientra.", urgency, ConfidenceLevel.Medium,
                        "Può aumentare la latenza di alcuni ordini.", ApprovalLevel.LowRiskAction, Reversible: true, Name));
                if (list.Count == 0) list.Add(NextInvestigation(seed, investigation, urgency));
                break;

            case "productivity:mix-driven":
                list.Add(new Recommendation(
                    "Nessuna azione sulle persone: valutare il raggruppamento (batching) delle righe pesanti o degli ordini multi-zona",
                    $"{seed.Warehouse}, prelievo", "Il calo è spiegato dalla maggiore complessità del lavoro, non da inefficienza.",
                    "Parte della capacità recuperata riducendo gli spostamenti.", SeverityLevel.Low, ConfidenceLevel.Medium,
                    "Il batching migliora la produttività ma può allungare la latenza degli ordini.", ApprovalLevel.Informational, Reversible: true, Name));
                break;

            case "inventory:stockout":
            case "inventory:depletion":
                var sku = seed.Signature.Split('/').Last().Split(':')[0];
                list.Add(new Recommendation(
                    confirmed.Contains("replenishment") ? $"Sollecitare il fornitore e verificare gli arrivi attesi di {sku}" : $"Anticipare il riordino di {sku} e verificare gli arrivi in corso",
                    $"{sku} in {seed.Warehouse}", seed.Observation,
                    impact.FirstOrDefault(i => i.Unit == "unità") is { } u ? $"Evitare la mancata evasione di {u.Low:#,0}–{u.High:#,0} unità." : "Evitare ordini incompleti.",
                    urgency, investigation.Confidence == ConfidenceLevel.Low ? ConfidenceLevel.Medium : investigation.Confidence,
                    "Un riordino affrettato può creare eccesso se il picco di domanda è temporaneo.", ApprovalLevel.SupervisorApproval, Reversible: false, Name));
                if (confirmed.Contains("accuracy"))
                    list.Add(new Recommendation($"Conteggio fisico immediato dell'ubicazione di {sku}", $"{sku} in {seed.Warehouse}",
                        "Le rettifiche recenti rendono la giacenza di sistema poco affidabile.", "Giacenza certa prima di decidere il riordino.",
                        urgency, ConfidenceLevel.Medium, "Tempo operatore per il conteggio.", ApprovalLevel.LowRiskAction, Reversible: true, Name));
                break;

            case "inventory:overstock":
            case "inventory:dead-stock":
                list.Add(new Recommendation("Sospendere i riordini degli articoli elencati e valutare promozioni, resi a fornitore o trasferimenti",
                    seed.Scope, seed.Observation, money is null ? "Riduzione del capitale immobilizzato." : $"Fino a {money.High:#,0} € liberabili.",
                    SeverityLevel.Low, ConfidenceLevel.Medium, "Rischio di carenza se la domanda riparte.", ApprovalLevel.ManagementDecision, Reversible: true, Name));
                break;

            case "inventory:imbalance":
                list.Add(new Recommendation("Trasferire parte della giacenza dal magazzino in eccesso a quello in carenza", seed.Scope, seed.Observation,
                    "Rottura evitata senza nuovi acquisti.", urgency, ConfidenceLevel.High, "Costo e tempo di trasporto interno.", ApprovalLevel.SupervisorApproval, Reversible: true, Name));
                break;

            case "inventory:spike":
            case "inventory:drop":
                if (confirmed.Contains("demand-event") && investigation.AffectedItems.Count > 0)
                {
                    var category = investigation.AffectedItems[0];
                    list.Add(new Recommendation(
                        $"Verificare con l'ufficio commerciale la causa del picco sulla categoria {category} (promozione, ordine eccezionale) e controllare la copertura dei suoi articoli",
                        $"{seed.Warehouse}, categoria {category}", seed.Observation,
                        "Riordini adeguati se il picco continua; nessun riordino inutile se era un evento isolato.", urgency, investigation.Confidence,
                        "Nessuna: è una verifica.", ApprovalLevel.LowRiskAction, Reversible: true, Name));
                }
                else list.Add(NextInvestigation(seed, investigation, urgency));
                break;

            default:
                list.Add(NextInvestigation(seed, investigation, urgency));
                break;
        }
        return list;
    }

    private Recommendation NextInvestigation(AgentFinding seed, InvestigationResult investigation, SeverityLevel urgency) => new(
        "Approfondire prima di agire: " + (investigation.OpenQuestions.FirstOrDefault() ?? "raccogliere dati sulle ipotesi aperte"),
        seed.Scope,
        "Le prove non bastano a distinguere tra le spiegazioni possibili: agire ora rischierebbe di correggere la cosa sbagliata.",
        "Una causa identificata con confidenza sufficiente per decidere.",
        urgency, ConfidenceLevel.Low, "Ritardo nell'intervento se il problema peggiora.", ApprovalLevel.Informational, Reversible: true, Name);
}
