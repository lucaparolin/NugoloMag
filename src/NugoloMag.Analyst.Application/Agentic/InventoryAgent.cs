using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Application.Detection;
using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Domain.Agentic;
using static NugoloMag.Analyst.Application.Agentic.Evidence;

namespace NugoloMag.Analyst.Application.Agentic;

/// <summary>
/// Agente Inventario (blueprint §5): valuta se lo stock è sano, disponibile, ben distribuito ed economicamente efficiente.
/// Riusa i detector statistici esistenti e aggiunge esaurimento accelerato, copertura, eccessi, stock fermo e squilibri tra magazzini.
/// </summary>
public sealed class InventoryAgent(DetectionSettings detection, AgenticSettings settings) : ISpecialistAgent
{
    public string Name => AgentNames.Inventory;
    public OperationalDomain Domain => OperationalDomain.Inventory;
    public string Mission => "Valutare in continuo se l'inventario è sano, disponibile, ben posizionato ed economicamente efficiente.";

    public IReadOnlyDictionary<string, string> Aspects { get; } = new Dictionary<string, string>
    {
        ["availability"] = "Disponibilità degli articoli: giorni a giacenza zero o negativa nel periodo recente",
        ["consumption"] = "Consumo (uscite) recente rispetto alla baseline, per articoli o per magazzino",
        ["cover"] = "Giorni di copertura della giacenza al ritmo di consumo recente",
        ["adjustments"] = "Rettifiche inventariali nel periodo recente",
        ["inbound"] = "Entrate (ricevimenti) recenti rispetto alla baseline"
    };

    public IReadOnlyList<AgentFinding> Observe(AgentWorkspace ws)
    {
        var data = ws.Inventory;
        var window = ws.Window;
        var detectors = new IChangeDetector[]
        {
            new DataFreshnessDetector(detection), new StockIntegrityDetector(detection), new PointAnomalyDetector(detection),
            new TrendReversalDetector(detection), new ItemLifecycleDetector(detection), new MixDriftDetector(detection)
        };

        var ranked = new FindingRanker(detection).Rank(detectors.SelectMany(d => d.Detect(data, window)));
        var rootCause = new RootCause.ContributionAnalyzer();
        var explained = new FindingConsolidator().Consolidate(
            ranked.Select(f => f with { RootCauses = rootCause.Explain(f, data, window) }).ToList(), data);

        var findings = explained.Select(f => FromDetector(f, data, window)).ToList();
        var stockouts = explained.Where(f => f.Kind == FindingKind.Stockout).Select(f => f.Subject).ToHashSet();

        findings.AddRange(Depletion(data, window, stockouts));
        findings.AddRange(ExcessAndDeadStock(data, window));
        findings.AddRange(NetworkImbalance(data, window));
        return findings;
    }

    public AgentFinding? Examine(AgentWorkspace ws, AgentQuery query)
    {
        var data = ws.Inventory;
        var window = ws.Window;
        var warehouse = new WarehouseCode(query.Warehouse);
        var skus = query.Skus.Count > 0
            ? query.Skus.Select(s => new Sku(s)).ToList()
            : data.SkusIn(warehouse).ToList();
        if (skus.Count == 0) return null;

        var evidence = new List<EvidenceItem>();
        string observation;
        string why;
        switch (query.Aspect)
        {
            case "availability":
            {
                var short_ = skus.Select(s => (Sku: s, Days: window.RecentDates().Count(d => data.Series(new Subject(warehouse, s), Metric.OnHand)[d] <= 0)))
                    .Where(x => x.Days > 0).ToList();
                evidence.AddRange(short_.Take(10).Select(x => Fact(Name, $"{x.Sku}: {x.Days} giorni a giacenza zero nel periodo recente")));
                observation = short_.Count == 0
                    ? $"Tutti i {skus.Count} articoli esaminati in {warehouse} sono rimasti disponibili nel periodo recente"
                    : $"{short_.Count} articoli su {skus.Count} hanno avuto giorni a giacenza zero in {warehouse}";
                why = short_.Count == 0 ? "La disponibilità di stock non spiega il problema." : "La mancanza di stock può causare attese, prelievi a vuoto e ordini incompleti.";
                break;
            }
            case "consumption":
            {
                var (b, r) = Flow(data, window, warehouse, skus, Metric.Outbound);
                var ratio = b <= 0 ? 0 : r / b;
                evidence.Add(Fact(Name, $"Uscite medie giornaliere: {Num(r)} contro {Num(b)} in baseline ({Pct(ratio - 1)})"));
                observation = $"Consumo {(skus.Count == 1 ? skus[0].Value : $"di {skus.Count} articoli")} in {warehouse}: {Pct(ratio - 1)} rispetto alla baseline";
                why = Math.Abs(ratio - 1) < 0.2 ? "Il consumo è nella norma." : ratio > 1 ? "La domanda è più alta del normale." : "La domanda è più bassa del normale.";
                break;
            }
            case "inbound":
            {
                var (b, r) = Flow(data, window, warehouse, skus, Metric.Inbound);
                evidence.Add(Fact(Name, $"Entrate medie giornaliere: {Num(r)} contro {Num(b)} in baseline"));
                var noneRecently = r == 0 && b > 0;
                observation = noneRecently ? $"Nessun ricevimento recente in {warehouse} per gli articoli esaminati (in baseline {Num(b)}/giorno)"
                    : $"Ricevimenti in {warehouse}: {Num(r)}/giorno contro {Num(b)} in baseline";
                why = noneRecently ? "Il riassortimento si è fermato: possibile ritardo fornitore o di ricevimento (agente Inbound non ancora attivo)." : "I ricevimenti sono in linea.";
                break;
            }
            case "adjustments":
            {
                var adj = skus.Select(s => (Sku: s, Units: window.RecentDates().Sum(d => data.Series(new Subject(warehouse, s), Metric.Adjustment)[d])))
                    .Where(x => x.Units != 0).OrderBy(x => x.Units).ToList();
                evidence.AddRange(adj.Take(10).Select(x => Fact(Name, $"{x.Sku}: rettifiche {Num(x.Units)} unità nel periodo recente")));
                observation = adj.Count == 0 ? $"Nessuna rettifica recente in {warehouse} sugli articoli esaminati" : $"{adj.Count} articoli con rettifiche recenti in {warehouse}";
                why = adj.Count == 0 ? "Nessun segnale di inaccuratezza inventariale." : "Rettifiche ripetute indicano giacenze di sistema che divergono dalla realtà.";
                break;
            }
            default:
            {
                var cover = skus.Select(s => (Sku: s, Days: Cover(data, window, warehouse, s))).Where(x => x.Days is not null).OrderBy(x => x.Days).ToList();
                evidence.AddRange(cover.Take(10).Select(x => Fact(Name, $"{x.Sku}: copertura {Num(x.Days!.Value)} giorni")));
                observation = cover.Count == 0 ? "Copertura non calcolabile (nessun consumo)" : $"Copertura minima {Num(cover[0].Days!.Value)} giorni ({cover[0].Sku})";
                why = "La copertura indica quanto a lungo lo stock regge al ritmo attuale.";
                break;
            }
        }

        return new AgentFinding
        {
            Agent = Name, Domain = Domain, Signature = $"inventory:{query.Aspect}:{warehouse}", Warehouse = warehouse.Value,
            Title = observation, Observation = observation, WhyItMatters = why, Evidence = evidence,
            Baseline = $"{window.BaselineStart:dd/MM}–{window.BaselineEnd:dd/MM}", Deviation = "", Scope = $"{warehouse}, {skus.Count} articoli",
            Confidence = ConfidenceLevel.High, Severity = new SeverityInputs(0, 0, 0, 0, ConfidenceLevel.High), Intensity = 0
        };
    }

    // ---------- conversione dei finding statistici nel contratto standard ----------

    private AgentFinding FromDetector(Finding f, InventoryDataset data, AnalysisWindow window)
    {
        var evidence = new List<EvidenceItem>();
        if (f.Baseline is { } b && f.Observed is { } o)
            evidence.Add(Fact(Name, $"Valore osservato {Num(o)} contro {Num(b)} atteso", ("atteso", Num(b)), ("osservato", Num(o))));
        evidence.AddRange(f.Evidence.Select(e => Fact(Name, $"{e.Key}: {e.Value}")));
        evidence.AddRange(f.RootCauses.Take(4).Select(c => Signal(Name, $"{c.Dimension} {c.Member}: {c.Share:P0} della variazione")));

        var subjectValue = SubjectValue(f, data, window);
        var dailyValue = Math.Max(1, DailyOutboundValue(data, window, f.Subject.Warehouse));
        var (why, urgency, confidence, next) = f.Kind switch
        {
            FindingKind.Stockout => ("Un articolo abitualmente venduto non è disponibile: ordini persi o incompleti finché non arriva merce.", 1.0, ConfidenceLevel.High,
                new[] { new NextStep(AgentNames.Investigation, "Capire perché il riassortimento non è arrivato"), new NextStep(AgentNames.Impact, "Stimare vendite a rischio") }),
            FindingKind.StalledItem => ("Merce a stock che non esce: possibile blocco fisico o di sistema, o crollo della domanda.", 0.6, ConfidenceLevel.Medium,
                new[] { new NextStep(AgentNames.Investigation, "Distinguere blocco operativo da calo della domanda") }),
            FindingKind.Integrity => ("Le giacenze di sistema non tornano con i movimenti: decisioni di riordino su dati sbagliati.", 0.6, ConfidenceLevel.High,
                new[] { new NextStep(AgentNames.Investigation, "Individuare i movimenti non registrati") }),
            FindingKind.DataFreshness => ("Dati incompleti: tutte le altre conclusioni sul magazzino vanno sospese.", 0.9, ConfidenceLevel.High, Array.Empty<NextStep>()),
            FindingKind.MixDrift => ("Il mix della domanda è cambiato: stock e posizionamento pensati per il mix precedente.", 0.3, ConfidenceLevel.Medium,
                new[] { new NextStep(AgentNames.Investigation, "Verificare se il cambio di mix è strutturale") }),
            FindingKind.NewItem => ("Nuovi articoli attivi: vanno dimensionati scorte e ubicazioni.", 0.2, ConfidenceLevel.High, Array.Empty<NextStep>()),
            FindingKind.TrendReversal => ("Il trend si è invertito: la giacenza potrebbe andare verso un eccesso o una carenza.", 0.5, ConfidenceLevel.Medium,
                new[] { new NextStep(AgentNames.Investigation, "Capire cosa ha invertito il trend") }),
            _ when f.Metric == Metric.Adjustment => ("Rettifiche inventariali anomale: possibili ammanchi o errori di conteggio.", 0.5, ConfidenceLevel.High,
                new[] { new NextStep(AgentNames.Investigation, "Distinguere ammanco da errore di conteggio") }),
            // Evento di un solo giorno: rilevante ma già avvenuto, quindi meno urgente di una condizione che persiste.
            _ => ("Consumo anomalo rispetto al normale di questo giorno della settimana: impatto su copertura e riordini.", 0.4, ConfidenceLevel.High,
                new[] { new NextStep(AgentNames.Investigation, "Capire la causa del cambio di consumo") })
        };

        // Impatto economico relativo al valore delle uscite di una giornata normale del magazzino:
        // una perdita (rettifica negativa) pesa più di un flusso; un picco di vendite non è esposizione ma rischio di copertura;
        // una rottura è un disservizio verso i clienti, non solo un valore a costo.
        var relative = subjectValue / (f.Subject.IsWarehouseLevel ? dailyValue : dailyValue * 0.2);
        var impact = f.Kind switch
        {
            FindingKind.Stockout => Math.Max(0.5, Math.Min(1, relative)),
            FindingKind.Integrity => 0.5, // dati di giacenza inaffidabili: rischio sulle decisioni di riordino, non una perdita misurata
            FindingKind.Spike or FindingKind.Drop when f.Metric == Metric.Outbound => 0.3,
            _ when f.Metric == Metric.Adjustment => Math.Min(1, relative * 3),
            _ => Math.Min(1, relative)
        };
        // Ambito: un finding di magazzino spiegato da pochi articoli ha un ambito ristretto.
        var skuDrivers = f.RootCauses.Count(c => c.Dimension == "Articolo");
        var scope = !f.Subject.IsWarehouseLevel ? 0.4 : f.Metric == Metric.Adjustment && skuDrivers > 0 ? Math.Min(1, 0.3 + 0.1 * skuDrivers) : 1;
        return new AgentFinding
        {
            Agent = Name,
            Domain = f.Kind is FindingKind.Integrity or FindingKind.DataFreshness ? OperationalDomain.Quality : Domain,
            Signature = $"inventory:{f.Kind.Code()}:{f.Subject}{(f.Metric is { } m ? ":" + m.Code() : "")}",
            Warehouse = f.Subject.Warehouse.Value,
            Title = f.Headline,
            Observation = f.Headline,
            WhyItMatters = why,
            Evidence = evidence,
            Baseline = f.Baseline is { } bl ? Num(bl) : "—",
            Deviation = f.RelativeChange is { } rc && double.IsFinite(rc) ? Pct(rc) : f.Observed is { } ob && f.Baseline is { } bs ? $"{Num(ob - bs)}" : "—",
            Scope = f.Subject.IsWarehouseLevel ? $"magazzino {f.Subject.Warehouse}" : $"articolo {f.Subject.Sku} in {f.Subject.Warehouse}",
            Hypotheses = HypothesesFor(f),
            Confidence = confidence,
            SuggestedNext = next,
            Severity = new SeverityInputs(f.Magnitude / 100, scope, impact, urgency, confidence),
            Intensity = f.Magnitude / 100,
            FirstObserved = f.Kind is FindingKind.Stockout or FindingKind.StalledItem ? f.Date.AddDays(-(detection.StallDays - 1)) : f.Date,
            Measures = new Dictionary<string, double>
            {
                ["baseline"] = f.Baseline ?? 0,
                ["observed"] = f.Observed ?? 0,
                ["value_exposed"] = subjectValue,
                ["daily_demand"] = f.Subject.Sku is { } sku ? DailyDemand(data, window, f.Subject.Warehouse, sku) : 0,
                ["unit_cost"] = f.Subject.Sku is { } s2 ? UnitCost(data, f.Subject.Warehouse, s2) : 0,
                ["top_driver_share"] = f.RootCauses.FirstOrDefault()?.Share ?? 0
            },
            Drivers = f.RootCauses.Where(c => c.Dimension == "Categoria").Select(c => c.Member).Take(3).ToList()
        };
    }

    private static IReadOnlyList<Hypothesis> HypothesesFor(Finding f) => f.Kind switch
    {
        FindingKind.Stockout =>
        [
            H("demand", "Domanda superiore al previsto prima della rottura", ["uscite recenti rispetto alla baseline"]),
            H("replenishment", "Riassortimento in ritardo (fornitore o ricevimento)", ["ordini fornitore e arrivi attesi (agente Inbound non ancora attivo)"]),
            H("accuracy", "Giacenza di sistema non corretta (stock fisico diverso)", ["rettifiche e conteggi recenti"])
        ],
        FindingKind.StalledItem =>
        [
            H("blocked", "Merce presente ma non prelevabile (ubicazione, blocco qualità, lotto)", ["stato ubicazione e blocchi qualità"]),
            H("demand-drop", "Crollo della domanda dell'articolo", ["ordini clienti aperti sull'articolo"])
        ],
        FindingKind.Integrity =>
        [
            H("unrecorded", "Movimenti fisici non registrati a sistema", ["confronto con documenti di trasporto"]),
            H("interface", "Errore di interfaccia o di caricamento dati", ["log delle integrazioni"])
        ],
        _ when f.Metric == Metric.Adjustment =>
        [
            H("shrinkage", "Ammanco fisico (danneggiamento, furto, scarti)", ["esito del conteggio fisico"]),
            H("count-error", "Errore di conteggio o di rettifica", ["storico delle rettifiche sull'articolo"])
        ],
        _ when f.Metric == Metric.Outbound =>
        [
            H("demand-event", "Evento di domanda (promozione, ordine eccezionale di un cliente)", ["ordini clienti del giorno"]),
            H("data-duplicate", "Movimenti duplicati o caricati due volte", ["numeri documento duplicati"])
        ],
        _ => []
    };

    private static Hypothesis H(string code, string statement, IReadOnlyList<string> missing) =>
        new(code, statement, HypothesisStatus.Candidate, ConfidenceLevel.Low, [], [], missing);

    // ---------- analisi aggiuntive dell'agente ----------

    private IEnumerable<AgentFinding> Depletion(InventoryDataset data, AnalysisWindow window, HashSet<Subject> alreadyOut)
    {
        foreach (var warehouse in data.Warehouses.Where(w => data.IsLoadedThrough(w, window.AsOf)))
        foreach (var sku in data.SkusIn(warehouse))
        {
            var subject = new Subject(warehouse, sku);
            if (alreadyOut.Contains(subject)) continue;
            var outbound = data.Series(subject, Metric.Outbound);
            var baseline = TimeSeries.Mean(outbound.Slice(window.BaselineDates()));
            var recent = TimeSeries.Mean(outbound.Slice(window.RecentDates()));
            if (baseline < 2 || recent / baseline < settings.MinDepletionRatio) continue;

            var onHand = data.Series(subject, Metric.OnHand)[window.AsOf];
            var cover = recent <= 0 ? double.PositiveInfinity : onHand / recent;
            if (cover >= settings.StockoutCoverDays) continue;

            var ratio = recent / baseline;
            var unitCost = UnitCost(data, warehouse, sku);
            var inboundRecent = TimeSeries.Mean(data.Series(subject, Metric.Inbound).Slice(window.RecentDates()));
            yield return new AgentFinding
            {
                Agent = Name,
                Domain = Domain,
                Signature = $"inventory:depletion:{subject}",
                Warehouse = warehouse.Value,
                Title = $"{sku} in {warehouse} si esaurisce {ratio:0.0}× più velocemente del normale",
                Observation = $"{sku} in {warehouse} si sta esaurendo {ratio:0.0}× più velocemente della sua baseline: al ritmo attuale la giacenza ({Num(onHand)}) copre circa {Num(cover)} giorni.",
                WhyItMatters = "Senza intervento l'articolo scenderà sotto la normale copertura operativa entro pochi giorni.",
                Evidence =
                [
                    Fact(Name, $"Uscite medie: {Num(recent)}/giorno contro {Num(baseline)} in baseline"),
                    Fact(Name, $"Giacenza al {window.AsOf:dd/MM}: {Num(onHand)} unità"),
                    Fact(Name, $"Entrate medie recenti: {Num(inboundRecent)}/giorno"),
                    Signal(Name, $"Copertura {Num(cover)} giorni, sotto la soglia di {settings.StockoutCoverDays} giorni")
                ],
                Baseline = $"{Num(baseline)} unità/giorno",
                Deviation = $"{ratio:0.0}× la baseline",
                Scope = $"articolo {sku} ({data.CategoryOf(warehouse, sku)}) in {warehouse}",
                Hypotheses =
                [
                    H("demand", "Domanda in aumento (promozione, nuovo cliente, stagionalità)", ["ordini clienti recenti sull'articolo"]),
                    H("replenishment", "Riassortimento insufficiente rispetto al nuovo ritmo", ["ordini fornitore in corso"])
                ],
                Confidence = ConfidenceLevel.High,
                SuggestedNext = [new NextStep(AgentNames.Impact, "Stimare le unità a rischio"), new NextStep(AgentNames.Recommendation, "Anticipare il riassortimento")],
                Severity = new SeverityInputs(Math.Min(1, (ratio - 1) / 3), 0.4, Math.Min(1, recent * unitCost * settings.ImpactHorizonDays / Math.Max(1, DailyOutboundValue(data, window, warehouse))), Math.Clamp(1 - cover / settings.StockoutCoverDays, 0.3, 1), ConfidenceLevel.High),
                Intensity = ratio,
                FirstObserved = window.RecentStart,
                Measures = new Dictionary<string, double> { ["daily_demand"] = recent, ["cover_days"] = cover, ["on_hand"] = onHand, ["unit_cost"] = unitCost }
            };
        }
    }

    private IEnumerable<AgentFinding> ExcessAndDeadStock(InventoryDataset data, AnalysisWindow window)
    {
        foreach (var warehouse in data.Warehouses.Where(w => data.IsLoadedThrough(w, window.AsOf)))
        {
            var items = data.SkusIn(warehouse).Select(sku =>
            {
                var subject = new Subject(warehouse, sku);
                var daily = TimeSeries.Mean(data.Series(subject, Metric.Outbound).Slice(window.BaselineDates().Concat(window.RecentDates())));
                var onHand = data.Series(subject, Metric.OnHand)[window.AsOf];
                return (Sku: sku, Daily: daily, OnHand: onHand, Value: onHand * UnitCost(data, warehouse, sku), Cover: daily <= 0 ? double.PositiveInfinity : onHand / daily);
            }).Where(x => x.OnHand > 0).ToList();

            var dead = items.Where(x => x.Daily == 0 && x.Value > 200).OrderByDescending(x => x.Value).ToList();
            var excess = items.Where(x => x.Daily > 0 && x.Cover > settings.OverstockCoverDays).OrderByDescending(x => x.Value).ToList();

            foreach (var (list, code, label) in new[] { (dead, "dead-stock", "stock fermo (nessuna uscita nel periodo)"), (excess, "overstock", $"copertura oltre {settings.OverstockCoverDays} giorni") })
            {
                if (list.Count == 0) continue;
                var value = list.Sum(x => x.Value);
                var totalValue = Math.Max(1, items.Sum(x => x.Value));
                yield return new AgentFinding
                {
                    Agent = Name,
                    Domain = Domain,
                    Signature = $"inventory:{code}:{warehouse}",
                    Warehouse = warehouse.Value,
                    Title = $"{list.Count} articoli in {warehouse} con {label}: {Num(value)} € immobilizzati",
                    Observation = $"In {warehouse} {list.Count} articoli hanno {label}, per un valore di {Num(value)} € ({value / totalValue:P0} del valore a stock).",
                    WhyItMatters = "Capitale circolante immobilizzato e spazio occupato da merce che non ruota.",
                    Evidence = list.Take(8).Select(x => Fact(Name, $"{x.Sku}: {Num(x.OnHand)} unità, {Num(x.Value)} €{(double.IsFinite(x.Cover) ? $", copertura {Num(x.Cover)} giorni" : "")}")).ToList(),
                    Baseline = "rotazione normale dell'articolo",
                    Deviation = label,
                    Scope = $"{list.Count} articoli in {warehouse}",
                    Hypotheses = [H("reorder", "Riordini non adeguati alla domanda reale", ["parametri di riordino"]), H("obsolete", "Articoli a fine vita o fuori assortimento", ["stato commerciale degli articoli"])],
                    Confidence = ConfidenceLevel.High,
                    Severity = new SeverityInputs(Math.Min(1, value / totalValue * 3), Math.Min(1, list.Count / 20.0), Math.Min(1, value / totalValue * 2), 0.2, ConfidenceLevel.High),
                    Intensity = value,
                    FirstObserved = window.BaselineStart,
                    Measures = new Dictionary<string, double> { ["value"] = value, ["items"] = list.Count }
                };
            }
        }
    }

    private IEnumerable<AgentFinding> NetworkImbalance(InventoryDataset data, AnalysisWindow window)
    {
        var bySku = data.Warehouses
            .SelectMany(w => data.SkusIn(w).Select(s => (Warehouse: w, Sku: s, Cover: Cover(data, window, w, s))))
            .Where(x => x.Cover is not null)
            .GroupBy(x => x.Sku)
            .Where(g => g.Count() >= 2);

        foreach (var g in bySku)
        {
            var low = g.MinBy(x => x.Cover)!;
            var high = g.MaxBy(x => x.Cover)!;
            if (low.Cover >= settings.StockoutCoverDays || high.Cover <= 60) continue;
            yield return new AgentFinding
            {
                Agent = Name,
                Domain = Domain,
                Signature = $"inventory:imbalance:{g.Key}",
                Warehouse = low.Warehouse.Value,
                Title = $"{g.Key}: eccesso in {high.Warehouse}, carenza in {low.Warehouse}",
                Observation = $"{high.Warehouse} ha {Num(high.Cover!.Value)} giorni di copertura di {g.Key} mentre {low.Warehouse} ne ha {Num(low.Cover!.Value)}: esiste un'opportunità di ridistribuzione.",
                WhyItMatters = "Un trasferimento interno evita la rottura senza nuovi acquisti.",
                Evidence = g.Select(x => Fact(Name, $"{x.Warehouse}: copertura {Num(x.Cover!.Value)} giorni")).ToList(),
                Baseline = "copertura equilibrata tra magazzini",
                Deviation = $"{Num(high.Cover.Value)} contro {Num(low.Cover.Value)} giorni",
                Scope = $"{g.Key} in {g.Count()} magazzini",
                Confidence = ConfidenceLevel.High,
                SuggestedNext = [new NextStep(AgentNames.Recommendation, "Proporre il trasferimento")],
                Severity = new SeverityInputs(0.7, 0.3, 0.5, 0.8, ConfidenceLevel.High),
                Intensity = high.Cover.Value / Math.Max(0.1, low.Cover.Value),
                FirstObserved = window.AsOf
            };
        }
    }

    // ---------- misure ----------

    private static (double Baseline, double Recent) Flow(InventoryDataset data, AnalysisWindow window, WarehouseCode warehouse, IReadOnlyList<Sku> skus, Metric metric)
    {
        double Mean(IEnumerable<DateOnly> dates) => TimeSeries.Mean(dates.Select(d => skus.Sum(s => data.Series(new Subject(warehouse, s), metric)[d])).ToArray());
        return (Mean(window.BaselineDates()), Mean(window.RecentDates()));
    }

    private static double? Cover(InventoryDataset data, AnalysisWindow window, WarehouseCode warehouse, Sku sku)
    {
        var subject = new Subject(warehouse, sku);
        var daily = TimeSeries.Mean(data.Series(subject, Metric.Outbound).Slice(window.RecentDates()));
        return daily <= 0 ? null : data.Series(subject, Metric.OnHand)[window.AsOf] / daily;
    }

    private static double DailyDemand(InventoryDataset data, AnalysisWindow window, WarehouseCode warehouse, Sku sku) =>
        TimeSeries.Mean(data.Series(new Subject(warehouse, sku), Metric.Outbound).Slice(window.BaselineDates()));

    private static double UnitCost(InventoryDataset data, WarehouseCode warehouse, Sku sku) =>
        (double)(data.In(new Subject(warehouse, sku)).Select(r => r.UnitCost).LastOrDefault());

    private static double DailyOutboundValue(InventoryDataset data, AnalysisWindow window, WarehouseCode warehouse) =>
        data.In(warehouse).Where(r => window.IsBaseline(r.Date)).Sum(r => (double)(r.Outbound * r.UnitCost)) / window.BaselineDays;

    private static double SubjectValue(Finding f, InventoryDataset data, AnalysisWindow window)
    {
        if (f.Subject.Sku is not { } sku) return Math.Abs((f.Observed ?? 0) - (f.Baseline ?? 0)) * AverageCost(data, f.Subject.Warehouse);
        var cost = UnitCost(data, f.Subject.Warehouse, sku);
        return f.Kind == FindingKind.Stockout
            ? DailyDemand(data, window, f.Subject.Warehouse, sku) * cost * 5
            : Math.Abs((f.Observed ?? 0) - (f.Baseline ?? 0)) * cost;
    }

    private static double AverageCost(InventoryDataset data, WarehouseCode warehouse) =>
        data.In(warehouse).Select(r => (double)r.UnitCost).DefaultIfEmpty(0).Average();
}
