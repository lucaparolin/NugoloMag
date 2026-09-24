using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Domain.Agentic;
using static NugoloMag.Analyst.Application.Agentic.Evidence;

namespace NugoloMag.Analyst.Application.Agentic;

/// <summary>
/// Agente Produttività (blueprint §8): capisce se la produttività è cambiata e perché, a parità di complessità del lavoro.
/// Non attribuisce mai il calo alle persone: l'operatore non è nemmeno nei dati analizzati (equità operativa).
/// </summary>
public sealed class ProductivityAgent(AgenticSettings settings) : ISpecialistAgent
{
    public string Name => AgentNames.Productivity;
    public OperationalDomain Domain => OperationalDomain.Productivity;
    public string Mission => "Capire la produttività operativa e spiegare perché cambia, a parità di carico e complessità.";

    public IReadOnlyDictionary<string, string> Aspects { get; } = new Dictionary<string, string>
    {
        ["efficiency"] = "Produttività rispetto all'atteso contestuale (a parità di mix) per magazzino e attività",
        ["workload"] = "Volume di lavoro (righe al giorno) rispetto alla baseline",
        ["complexity"] = "Complessità degli ordini: righe per ordine, quota di ordini su più zone",
        ["relocation"] = "Articoli che hanno cambiato zona di prelievo e il loro effetto sui tempi",
        ["congestion"] = "Rallentamento per zona degli articoli non spostati (segnale di congestione)"
    };

    public IReadOnlyList<AgentFinding> Observe(AgentWorkspace ws)
    {
        var findings = new List<AgentFinding>();
        foreach (var warehouse in ws.Tasks.Warehouses)
        foreach (var activity in ws.Tasks.Activities(warehouse))
        {
            var a = ProductivityAnalysis.Compute(ws.Tasks.For(warehouse, activity), ws.Window, warehouse.Value, activity);
            if (a is null) continue;

            if (a.EfficiencyGap <= -settings.MinProductivityGap && a.EfficiencyZ <= -settings.MinProductivityZ)
                findings.Add(BelowExpectation(a));
            else if (a.RawChange <= -settings.MinProductivityGap && a.MixEffect <= -settings.MinProductivityGap / 2)
                findings.Add(ExplainedByComplexity(a));
        }
        return findings;
    }

    public AgentFinding? Examine(AgentWorkspace ws, AgentQuery query)
    {
        var warehouse = new WarehouseCode(query.Warehouse);
        var activity = query.Activity ?? "PICK";
        var a = ProductivityAnalysis.Compute(ws.Tasks.For(warehouse, activity), ws.Window, warehouse.Value, activity);
        if (a is null) return null;

        return query.Aspect switch
        {
            "workload" => Examination(a, "workload", $"Volume {activity} {a.Warehouse}: {Num(a.RecentLinesPerDay)} righe/giorno contro {Num(a.BaselineLinesPerDay)} ({Pct(a.VolumeChange)})",
                Math.Abs(a.VolumeChange) < 0.1 ? "Il carico di lavoro è nella norma: non spiega da solo lo scostamento." : "Il carico di lavoro è cambiato in modo rilevante.",
                [Fact(Name, "Righe al giorno", ("baseline", Num(a.BaselineLinesPerDay)), ("recente", Num(a.RecentLinesPerDay)))]),
            "complexity" => Examination(a, "complexity",
                $"Complessità ordini {a.Warehouse}: pezzi per riga {a.BaselineUnitsPerLine:0.0} → {a.RecentUnitsPerLine:0.0}, ordini multi-zona {a.BaselineMultiZoneShare:P0} → {a.RecentMultiZoneShare:P0}, righe per ordine {a.BaselineLinesPerOrder:0.0} → {a.RecentLinesPerOrder:0.0}",
                Math.Abs(a.MixEffect) < 0.03 ? "Il mix degli ordini non cambia in modo rilevante." : $"Il cambio di mix da solo sposta la produttività del {Pct(a.MixEffect)}.",
                [Fact(Name, "Quota ordini multi-zona", ("baseline", a.BaselineMultiZoneShare.ToString("P0")), ("recente", a.RecentMultiZoneShare.ToString("P0"))),
                 Signal(Name, $"Effetto mix sulla produttività: {Pct(a.MixEffect)}")]),
            "relocation" => Relocations(a, query.Skus),
            "congestion" => Congestion(a, query.Zone),
            _ => Examination(a, "efficiency", $"Produttività {activity} {a.Warehouse}: {Pct(a.EfficiencyGap)} rispetto all'atteso a parità di mix",
                $"Grezza {Pct(a.RawChange)}, effetto mix {Pct(a.MixEffect)}.",
                [Fact(Name, "Righe/ora", ("baseline", Num(a.BaselineLinesPerHour)), ("recente", Num(a.RecentLinesPerHour)))])
        };
    }

    private AgentFinding BelowExpectation(ProductivityAnalysis a)
    {
        var evidence = new List<EvidenceItem>
        {
            Fact(Name, $"Righe/ora {a.Activity}: {Num(a.RecentLinesPerHour)} contro {Num(a.BaselineLinesPerHour)} in baseline ({Pct(a.RawChange)})",
                ("minuti per riga baseline", a.BaselineMinutesPerLine.ToString("0.00")), ("minuti per riga recente", a.RecentMinutesPerLine.ToString("0.00"))),
            Fact(Name, $"Volume: {Num(a.RecentLinesPerDay)} righe/giorno contro {Num(a.BaselineLinesPerDay)} ({Pct(a.VolumeChange)})"),
            Fact(Name, $"Complessità: ordini multi-zona {a.BaselineMultiZoneShare:P0} → {a.RecentMultiZoneShare:P0}, pezzi per riga {a.BaselineUnitsPerLine:0.0} → {a.RecentUnitsPerLine:0.0}; effetto del mix sulla produttività {Pct(a.MixEffect)}"),
            Signal(Name, $"A parità di mix la produttività è {Pct(a.EfficiencyGap)} rispetto all'atteso (z = {a.EfficiencyZ:0.0})")
        };
        if (a.ByZone.FirstOrDefault() is { } zone)
            evidence.Add(Signal(Name, $"La zona {zone.Member} concentra il {zone.Share:P0} dei minuti in eccesso",
                a.ByZone.Take(4).Select(z => ($"zona {z.Member}", $"{Num(z.ExcessMinutes)} min ({z.Share:P0})")).ToArray()));
        if (a.Relocations.Count > 0)
        {
            var share = a.Relocations.Sum(r => Math.Max(0, r.ExcessMinutes)) / Math.Max(1, a.ByZone.Sum(z => z.ExcessMinutes));
            evidence.Add(Signal(Name, $"{a.Relocations.Count} articoli hanno cambiato zona di prelievo nel periodo; spiegano il {share:P0} dei minuti in eccesso",
                a.Relocations.Take(5).Select(r => (r.Sku, $"{r.FromZone}→{r.ToZone}, {r.RecentLines} righe")).ToArray()));
        }

        var lostHoursPerDay = Math.Max(0, a.ExcessMinutesPerDay) / 60;
        return new AgentFinding
        {
            Agent = Name,
            Domain = Domain,
            Signature = $"productivity:below-expectation:{a.Warehouse}:{a.Activity}",
            Warehouse = a.Warehouse,
            Title = $"Produttività {a.Activity} in {a.Warehouse} {Pct(a.EfficiencyGap)} rispetto all'atteso",
            Observation = $"La produttività di {a.Activity} in {a.Warehouse} è {Pct(a.EfficiencyGap)} rispetto all'atteso contestuale " +
                          $"({Num(a.RecentLinesPerHour)} righe/ora contro {Num(60 / a.ExpectedMinutesPerLine)} attese con questo mix di ordini).",
            WhyItMatters = $"Circa {Num(lostHoursPerDay)} ore di lavoro in più al giorno a parità di volume: meno capacità di evasione e rischio sul servizio.",
            Evidence = evidence,
            Baseline = $"{Num(a.BaselineLinesPerHour)} righe/ora nelle {a.BaselineDays} giornate di baseline",
            Deviation = $"{Pct(a.EfficiencyGap)} a parità di mix; variazione grezza {Pct(a.RawChange)}",
            Scope = $"{a.Warehouse}, attività {a.Activity}" + (a.ByZone.FirstOrDefault() is { } z1 ? $", concentrato in zona {z1.Member}" : ""),
            Hypotheses = ProductivityHypotheses(a),
            Confidence = a.EfficiencyZ <= -5 ? ConfidenceLevel.High : ConfidenceLevel.Medium,
            SuggestedNext =
            [
                new NextStep(AgentNames.Investigation, "Ricostruire la catena causale (spostamenti, congestione, disponibilità)"),
                new NextStep(AgentNames.Inventory, "Verificare la disponibilità degli articoli coinvolti"),
                new NextStep(AgentNames.Impact, "Stimare ore e costo persi")
            ],
            Severity = new SeverityInputs(
                Magnitude: Math.Min(1, -a.EfficiencyGap / 0.3),
                Scope: a.ByZone.FirstOrDefault()?.Share is { } s && s > 0.6 ? 0.6 : 0.9,
                BusinessImpact: Math.Min(1, lostHoursPerDay / 8),
                Urgency: 0.7,
                a.EfficiencyZ <= -5 ? ConfidenceLevel.High : ConfidenceLevel.Medium),
            Intensity = -a.EfficiencyGap,
            FirstObserved = a.FirstDeviation,
            Measures = new Dictionary<string, double>
            {
                ["efficiency_gap"] = a.EfficiencyGap,
                ["excess_minutes_per_day"] = a.ExcessMinutesPerDay,
                ["lines_per_day"] = a.RecentLinesPerDay,
                ["relocated_skus"] = a.Relocations.Count
            }
        };
    }

    private AgentFinding ExplainedByComplexity(ProductivityAnalysis a) => new()
    {
        Agent = Name,
        Domain = Domain,
        Signature = $"productivity:mix-driven:{a.Warehouse}:{a.Activity}",
        Warehouse = a.Warehouse,
        Title = $"Produttività {a.Activity} in {a.Warehouse} in calo, spiegata dalla complessità degli ordini",
        Observation = $"La produttività di {a.Activity} in {a.Warehouse} è cambiata del {Pct(a.RawChange)} mentre la complessità del lavoro è aumentata " +
                      $"(pezzi per riga {a.BaselineUnitsPerLine:0.0} → {a.RecentUnitsPerLine:0.0}, ordini multi-zona {a.BaselineMultiZoneShare:P0} → {a.RecentMultiZoneShare:P0}). " +
                      $"A parità di mix è in linea ({Pct(a.EfficiencyGap)}).",
        WhyItMatters = "Il calo non indica un problema di esecuzione: riguarda come vengono composti e rilasciati gli ordini.",
        Evidence =
        [
            Fact(Name, $"Righe/ora {Num(a.RecentLinesPerHour)} contro {Num(a.BaselineLinesPerHour)} ({Pct(a.RawChange)})"),
            Fact(Name, $"Pezzi per riga {a.BaselineUnitsPerLine:0.0} → {a.RecentUnitsPerLine:0.0}; ordini multi-zona {a.BaselineMultiZoneShare:P0} → {a.RecentMultiZoneShare:P0}; righe per ordine {a.BaselineLinesPerOrder:0.0} → {a.RecentLinesPerOrder:0.0}"),
            Signal(Name, $"Effetto mix {Pct(a.MixEffect)}; scostamento a parità di mix {Pct(a.EfficiencyGap)}")
        ],
        Baseline = $"{Num(a.BaselineLinesPerHour)} righe/ora",
        Deviation = $"grezza {Pct(a.RawChange)}, a parità di mix {Pct(a.EfficiencyGap)}",
        Scope = $"{a.Warehouse}, attività {a.Activity}",
        Hypotheses =
        [
            new Hypothesis("order-mix", "Il calo è dovuto al cambio di composizione del lavoro (complessità per riga)",
                HypothesisStatus.Probable, ConfidenceLevel.High,
                [$"pezzi per riga {a.BaselineUnitsPerLine:0.0} → {a.RecentUnitsPerLine:0.0}", $"quota multi-zona {a.BaselineMultiZoneShare:P0} → {a.RecentMultiZoneShare:P0}", $"a parità di mix produttività {Pct(a.EfficiencyGap)}"], [],
                ["tipo di clienti/ordini che hanno cambiato il mix"],
                "lavoro più complesso per riga → più tempo per riga → meno righe/ora"),
            new Hypothesis("operators", "Minor rendimento degli operatori", HypothesisStatus.Rejected, ConfidenceLevel.High, [],
                ["a parità di complessità i tempi sono in linea con la baseline"], [])
        ],
        Confidence = ConfidenceLevel.High,
        SuggestedNext = [new NextStep(AgentNames.Recommendation, "Valutare il raggruppamento degli ordini multi-zona")],
        // Informazione più che allarme: nessuna inefficienza, la capacità però cala.
        Severity = new SeverityInputs(Math.Min(1, -a.RawChange / 0.4), 0.5, 0.1, 0.2, ConfidenceLevel.High),
        Intensity = -a.RawChange,
        FirstObserved = a.FirstDeviation,
        Measures = new Dictionary<string, double> { ["raw_change"] = a.RawChange, ["mix_effect"] = a.MixEffect, ["efficiency_gap"] = a.EfficiencyGap }
    };

    private static IReadOnlyList<Hypothesis> ProductivityHypotheses(ProductivityAnalysis a)
    {
        var list = new List<Hypothesis>();
        if (a.Relocations.Count > 0)
        {
            var target = a.Relocations.GroupBy(r => r.ToZone).OrderByDescending(g => g.Count()).First();
            list.Add(new Hypothesis("relocation", $"Lo spostamento di {a.Relocations.Count} articoli (soprattutto verso la zona {target.Key}) ha allungato i percorsi",
                HypothesisStatus.Candidate, ConfidenceLevel.Medium,
                [$"{a.Relocations.Count} articoli cambiano zona", $"{target.Count()} verso la zona {target.Key}"], [], ["data e motivo dello spostamento"],
                $"spostamento articoli → percorsi più lunghi in zona {target.Key} → meno righe/ora"));
        }
        if (a.StayersSlowdownByZone.Where(z => z.Value > 0.1).OrderByDescending(z => z.Value).FirstOrDefault() is { Key: not null } slow)
            list.Add(new Hypothesis("congestion", $"Congestione in zona {slow.Key}: rallentano anche gli articoli non spostati ({Pct(slow.Value)})",
                HypothesisStatus.Candidate, ConfidenceLevel.Medium, [$"articoli non spostati in zona {slow.Key}: {Pct(slow.Value)} sul tempo per riga"], [], ["occupazione e flussi della zona"]));
        list.Add(new Hypothesis("workload", "Aumento del carico di lavoro", Math.Abs(a.VolumeChange) < 0.1 ? HypothesisStatus.Rejected : HypothesisStatus.Candidate,
            ConfidenceLevel.Medium, Math.Abs(a.VolumeChange) >= 0.1 ? [$"volume {Pct(a.VolumeChange)}"] : [],
            Math.Abs(a.VolumeChange) < 0.1 ? [$"volume {Pct(a.VolumeChange)}: nella norma"] : [], []));
        list.Add(new Hypothesis("order-mix", "Ordini più complessi", Math.Abs(a.MixEffect) < 0.03 ? HypothesisStatus.Rejected : HypothesisStatus.Candidate,
            ConfidenceLevel.Medium, Math.Abs(a.MixEffect) >= 0.03 ? [$"effetto mix {Pct(a.MixEffect)}"] : [],
            Math.Abs(a.MixEffect) < 0.03 ? [$"effetto mix {Pct(a.MixEffect)}: trascurabile"] : [], []));
        return list;
    }

    private AgentFinding Relocations(ProductivityAnalysis a, IReadOnlyList<string> skus)
    {
        var list = skus.Count == 0 ? a.Relocations : a.Relocations.Where(r => skus.Contains(r.Sku, StringComparer.OrdinalIgnoreCase)).ToList();
        var total = Math.Max(1, a.ByZone.Sum(z => z.ExcessMinutes));
        var share = list.Sum(r => Math.Max(0, r.ExcessMinutes)) / total;
        return Examination(a, "relocation",
            list.Count == 0 ? $"Nessun articolo ha cambiato zona in {a.Warehouse}" : $"{list.Count} articoli spostati di zona in {a.Warehouse}: spiegano il {share:P0} dei minuti in eccesso",
            list.Count == 0 ? "Gli spostamenti non spiegano lo scostamento." : $"Senza questi spostamenti i minuti in eccesso sarebbero circa il {1 - share:P0} di quelli osservati.",
            list.Take(12).Select(r => Fact(Name, $"{r.Sku}: zona {r.FromZone} → {r.ToZone}, {r.RecentLines} righe, {Num(r.ExcessMinutes)} minuti in più")).ToList(),
            new Dictionary<string, double>
            {
                ["relocated"] = list.Count,
                ["share_of_excess"] = share,
                ["first_seen"] = list.Count == 0 ? 0 : list.Min(r => r.FirstSeen).DayNumber
            });
    }

    private AgentFinding Congestion(ProductivityAnalysis a, string? zone)
    {
        var zones = zone is null ? a.StayersSlowdownByZone : a.StayersSlowdownByZone.Where(z => z.Key == zone).ToDictionary(z => z.Key, z => z.Value);
        var worst = zones.OrderByDescending(z => z.Value).FirstOrDefault();
        return Examination(a, "congestion",
            worst.Key is null ? "Dati insufficienti per valutare la congestione" : $"Articoli non spostati in zona {worst.Key}: tempo per riga {Pct(worst.Value)} rispetto alla loro baseline",
            worst.Value > 0.1 ? "Rallentano anche gli articoli rimasti nella stessa posizione: segnale di congestione della zona." : "Nessun rallentamento rilevante a parità di posizione.",
            zones.Select(z => Fact(Name, $"Zona {z.Key}: {Pct(z.Value)} sul tempo per riga degli articoli non spostati")).ToList(),
            new Dictionary<string, double> { ["max_slowdown"] = worst.Value });
    }

    private AgentFinding Examination(ProductivityAnalysis a, string aspect, string observation, string why, IReadOnlyList<EvidenceItem> evidence,
        IReadOnlyDictionary<string, double>? measures = null) => new()
    {
        Agent = Name,
        Domain = Domain,
        Signature = $"productivity:{aspect}:{a.Warehouse}:{a.Activity}",
        Warehouse = a.Warehouse,
        Title = observation,
        Observation = observation,
        WhyItMatters = why,
        Evidence = evidence,
        Baseline = $"{Num(a.BaselineLinesPerHour)} righe/ora",
        Deviation = $"{Pct(a.EfficiencyGap)} a parità di mix",
        Scope = $"{a.Warehouse}, {a.Activity}",
        Confidence = ConfidenceLevel.Medium,
        Severity = new SeverityInputs(0, 0, 0, 0, ConfidenceLevel.Medium),
        Intensity = 0,
        Measures = measures ?? new Dictionary<string, double>()
    };
}
