using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Application.Agentic;

/// <summary>Contributo di un segmento (zona, articolo) ai minuti in eccesso rispetto all'atteso.</summary>
public sealed record ExcessContribution(string Member, double ExcessMinutes, double Share, int Lines);

/// <summary>Articolo che ha cambiato zona prevalente tra baseline e periodo recente.</summary>
public sealed record Relocation(string Sku, string FromZone, string ToZone, int RecentLines, double ExcessMinutes, DateOnly FirstSeen);

/// <summary>
/// Analisi della produttività normalizzata per il carico (blueprint §8): confronto a parità di mix degli ordini,
/// scomposizione dello scostamento in effetto mix e inefficienza, attribuzione a zone e articoli.
/// Ogni riga di missione è un'unità di lavoro; il tempo per riga dipende dalla complessità dell'ordine.
/// </summary>
public sealed class ProductivityAnalysis
{
    public required string Warehouse { get; init; }
    public required string Activity { get; init; }
    public required int BaselineDays { get; init; }
    public required int RecentDays { get; init; }
    public required double BaselineLinesPerDay { get; init; }
    public required double RecentLinesPerDay { get; init; }
    public required double BaselineMinutesPerLine { get; init; }
    public required double RecentMinutesPerLine { get; init; }
    /// <summary>Minuti per riga attesi nel periodo recente dato il suo mix, ai ritmi della baseline.</summary>
    public required double ExpectedMinutesPerLine { get; init; }
    public required double BaselineMultiZoneShare { get; init; }
    public required double RecentMultiZoneShare { get; init; }
    public required double BaselineLinesPerOrder { get; init; }
    public required double BaselineUnitsPerLine { get; init; }
    public required double RecentUnitsPerLine { get; init; }
    public required double RecentLinesPerOrder { get; init; }
    public required double EfficiencyZ { get; init; }
    public required DateOnly? FirstDeviation { get; init; }
    public required IReadOnlyList<ExcessContribution> ByZone { get; init; }
    public required IReadOnlyList<ExcessContribution> BySku { get; init; }
    public required IReadOnlyList<Relocation> Relocations { get; init; }
    /// <summary>Variazione dei minuti per riga degli articoli NON spostati, per zona: rallentamento a parità di posizione (congestione).</summary>
    public required IReadOnlyDictionary<string, double> StayersSlowdownByZone { get; init; }
    public required double ExcessMinutesPerDay { get; init; }

    /// <summary>Produttività (righe/ora) recente rispetto alla baseline: variazione grezza.</summary>
    public double RawChange => BaselineMinutesPerLine / RecentMinutesPerLine - 1;
    /// <summary>Produttività recente rispetto all'atteso a parità di mix (negativo = sotto l'atteso contestuale).</summary>
    public double EfficiencyGap => ExpectedMinutesPerLine / RecentMinutesPerLine - 1;
    /// <summary>Quanto la sola complessità del lavoro avrebbe cambiato la produttività.</summary>
    public double MixEffect => BaselineMinutesPerLine / ExpectedMinutesPerLine - 1;
    public double VolumeChange => BaselineLinesPerDay == 0 ? 0 : RecentLinesPerDay / BaselineLinesPerDay - 1;
    public double BaselineLinesPerHour => 60 / BaselineMinutesPerLine;
    public double RecentLinesPerHour => 60 / RecentMinutesPerLine;

    public static ProductivityAnalysis? Compute(IEnumerable<WarehouseTask> tasks, AnalysisWindow window, string warehouse, string activity)
    {
        var all = tasks.ToList();
        var orderInfo = all.Where(t => t.OrderRef is not null).GroupBy(t => t.OrderRef!)
            .ToDictionary(g => g.Key, g => (Lines: g.Count(), Zones: g.Select(t => t.Zone).Distinct().Count()));

        string Bucket(WarehouseTask t)
        {
            if (t.OrderRef is null || !orderInfo.TryGetValue(t.OrderRef, out var o)) return "single";
            var size = o.Lines == 1 ? "1" : o.Lines <= 4 ? "2-4" : "5+";
            return o.Zones > 1 ? $"{size}|multi" : size;
        }

        var baseline = all.Where(t => window.IsBaseline(t.Date)).ToList();
        var recent = all.Where(t => window.IsRecent(t.Date)).ToList();
        if (baseline.Count < 200 || recent.Count < 50) return null;

        var baseMpl = baseline.Sum(t => t.Minutes) / baseline.Count;
        var bucketMpl = baseline.GroupBy(Bucket).ToDictionary(g => g.Key, g => g.Sum(t => t.Minutes) / g.Count());
        var bucketQty = baseline.GroupBy(Bucket).ToDictionary(g => g.Key, g => g.Average(t => (double)t.Quantity));

        // Effetto della quantità per riga (movimentazione): pendenza minuti/pezzo stimata in baseline dentro ogni classe d'ordine.
        var centered = baseline.Select(t => (Q: (double)t.Quantity - bucketQty[Bucket(t)], M: t.Minutes - bucketMpl[Bucket(t)])).ToList();
        var varQ = centered.Sum(c => c.Q * c.Q);
        var minutesPerUnit = varQ <= 0 ? 0 : Math.Max(0, centered.Sum(c => c.Q * c.M) / varQ);

        double Expected(WarehouseTask t)
        {
            var b = Bucket(t);
            return bucketMpl.TryGetValue(b, out var m)
                ? m + minutesPerUnit * ((double)t.Quantity - bucketQty[b])
                : baseMpl + minutesPerUnit * ((double)t.Quantity - baseline.Average(x => (double)x.Quantity));
        }

        // Scostamento giornaliero dall'atteso: la dispersione in baseline misura il rumore normale.
        var dailyRatio = all.GroupBy(t => t.Date)
            .Where(g => g.Count() >= 20)
            .ToDictionary(g => g.Key, g => g.Sum(Expected) / g.Sum(t => t.Minutes));
        var baseRatios = dailyRatio.Where(d => window.IsBaseline(d.Key)).Select(d => d.Value).ToArray();
        var recentRatios = dailyRatio.Where(d => window.IsRecent(d.Key)).Select(d => d.Value).ToArray();
        var sd = Math.Max(TimeSeries.StdDev(baseRatios), 0.01);
        var z = recentRatios.Length == 0 ? 0 : (TimeSeries.Mean(recentRatios) - TimeSeries.Mean(baseRatios)) / (sd / Math.Sqrt(recentRatios.Length));
        var threshold = TimeSeries.Mean(baseRatios) - 2 * sd;
        DateOnly? firstDeviation = dailyRatio.Where(d => d.Key > window.BaselineEnd.AddDays(-7) && d.Value < threshold)
            .OrderBy(d => d.Key).Select(d => (DateOnly?)d.Key).FirstOrDefault();

        // Attribuzione: ogni articolo confrontato con il proprio tempo per riga di baseline (stessa complessità).
        var skuBaseMpl = baseline.Where(t => t.Sku is not null).GroupBy(t => t.Sku!.Value.Value)
            .Where(g => g.Count() >= 5).ToDictionary(g => g.Key, g => g.Sum(t => t.Minutes) / g.Count());
        double SkuExpected(WarehouseTask t) => t.Sku is { } s && skuBaseMpl.TryGetValue(s.Value, out var m) ? m : Expected(t);

        var recentExcess = recent.Select(t => (Task: t, Excess: t.Minutes - SkuExpected(t))).ToList();
        var totalExcess = recentExcess.Where(e => e.Excess > 0).Sum(e => e.Excess);

        List<ExcessContribution> Contributions(Func<WarehouseTask, string?> key) =>
            recentExcess.Where(e => key(e.Task) is not null).GroupBy(e => key(e.Task)!)
                .Select(g => new ExcessContribution(g.Key, g.Sum(e => e.Excess), totalExcess <= 0 ? 0 : g.Sum(e => e.Excess) / totalExcess, g.Count()))
                .Where(c => c.ExcessMinutes > 0)
                .OrderByDescending(c => c.ExcessMinutes)
                .ToList();

        static string Mode(IEnumerable<WarehouseTask> ts) => ts.GroupBy(t => t.Zone).OrderByDescending(g => g.Count()).First().Key;
        var baseZone = baseline.Where(t => t.Sku is not null).GroupBy(t => t.Sku!.Value.Value).ToDictionary(g => g.Key, Mode);
        var relocations = recent.Where(t => t.Sku is not null).GroupBy(t => t.Sku!.Value.Value)
            .Where(g => baseZone.TryGetValue(g.Key, out var from) && from != Mode(g))
            .Select(g => new Relocation(g.Key, baseZone[g.Key], Mode(g), g.Count(), recentExcess.Where(e => e.Task.Sku?.Value == g.Key).Sum(e => e.Excess),
                g.Where(t => t.Zone == Mode(g)).Min(t => t.Date)))
            .OrderByDescending(r => r.RecentLines)
            .ToList();
        var moved = relocations.Select(r => r.Sku).ToHashSet();

        var stayers = recent.Where(t => t.Sku is not null && !moved.Contains(t.Sku.Value.Value)).GroupBy(t => t.Zone)
            .Where(g => g.Count() >= 20)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Minutes) / g.Sum(SkuExpected) - 1);

        double MultiZoneShare(IEnumerable<WarehouseTask> ts)
        {
            var orders = ts.Where(t => t.OrderRef is not null).Select(t => t.OrderRef!).Distinct().ToList();
            return orders.Count == 0 ? 0 : orders.Count(o => orderInfo[o].Zones > 1) / (double)orders.Count;
        }
        double LinesPerOrder(IEnumerable<WarehouseTask> ts)
        {
            var list = ts.Where(t => t.OrderRef is not null).ToList();
            var orders = list.Select(t => t.OrderRef).Distinct().Count();
            return orders == 0 ? 1 : list.Count / (double)orders;
        }

        return new ProductivityAnalysis
        {
            Warehouse = warehouse,
            Activity = activity,
            BaselineDays = baseline.Select(t => t.Date).Distinct().Count(),
            RecentDays = recent.Select(t => t.Date).Distinct().Count(),
            BaselineLinesPerDay = baseline.Count / (double)Math.Max(1, baseline.Select(t => t.Date).Distinct().Count()),
            RecentLinesPerDay = recent.Count / (double)Math.Max(1, recent.Select(t => t.Date).Distinct().Count()),
            BaselineMinutesPerLine = baseMpl,
            RecentMinutesPerLine = recent.Sum(t => t.Minutes) / recent.Count,
            ExpectedMinutesPerLine = recent.Sum(Expected) / recent.Count,
            BaselineMultiZoneShare = MultiZoneShare(baseline),
            RecentMultiZoneShare = MultiZoneShare(recent),
            BaselineLinesPerOrder = LinesPerOrder(baseline),
            BaselineUnitsPerLine = baseline.Average(t => (double)t.Quantity),
            RecentUnitsPerLine = recent.Average(t => (double)t.Quantity),
            RecentLinesPerOrder = LinesPerOrder(recent),
            EfficiencyZ = z,
            FirstDeviation = firstDeviation,
            ByZone = Contributions(t => t.Zone),
            BySku = Contributions(t => t.Sku?.Value),
            Relocations = relocations,
            StayersSlowdownByZone = stayers,
            ExcessMinutesPerDay = recent.Sum(t => t.Minutes - Expected(t)) / Math.Max(1, recent.Select(t => t.Date).Distinct().Count())
        };
    }
}
