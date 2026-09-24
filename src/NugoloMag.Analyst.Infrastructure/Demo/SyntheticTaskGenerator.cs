using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Infrastructure.Demo;

public sealed record DemoTask(WarehouseTask Task, string Operator);

/// <summary>
/// Genera le missioni di prelievo e imballo coerenti con le uscite dei dati sintetici, con due scenari del blueprint:
/// MI01 — negli ultimi 7 giorni i 19 articoli più movimentati vengono spostati in zona C (lontana): percorsi più lunghi e congestione;
/// RM02 — righe più pesanti (clienti all'ingrosso: più pezzi per riga): la produttività grezza cala
/// ma è spiegata dalla complessità del lavoro (equità verso gli operatori).
/// </summary>
public sealed class SyntheticTaskGenerator(int seed = 11, bool injectScenarios = true)
{
    public const int RelocatedSkus = 19;
    private static readonly Dictionary<string, double> ZoneMinutesPerLine = new() { ["A"] = 0.6, ["B"] = 0.9, ["C"] = 1.4 };

    public IReadOnlyList<DemoTask> Generate(IReadOnlyList<StockDay> rows, DateOnly asOf, int recentDays = 7)
    {
        var rng = new Random(seed);
        var recentStart = asOf.AddDays(-(recentDays - 1));
        var tasks = new List<DemoTask>();
        var id = 0;

        foreach (var warehouse in rows.Select(r => r.Warehouse).Distinct())
        {
            var whRows = rows.Where(r => r.Warehouse == warehouse).ToList();
            var ranked = whRows.GroupBy(r => r.Sku).OrderByDescending(g => g.Sum(r => r.Outbound)).Select(g => g.Key).ToList();
            var baseZone = ranked.Select((sku, i) => (sku, zone: i < ranked.Count * 0.3 ? "A" : i < ranked.Count * 0.7 ? "B" : "C"))
                .ToDictionary(x => x.sku, x => x.zone);

            foreach (var day in whRows.Select(r => r.Date).Distinct().OrderBy(d => d))
            {
                var recent = day >= recentStart;
                var relocation = injectScenarios && recent && warehouse.Value == "MI01";
                var bulky = injectScenarios && recent && warehouse.Value == "RM02";
                string ZoneOf(Sku sku) => relocation && ranked.IndexOf(sku) < RelocatedSkus ? "C" : baseZone[sku];

                // Righe d'ordine: ogni uscita giornaliera viene spezzata in righe da 1 a 8 pezzi (max 12 righe per articolo).
                var lines = new List<(Sku Sku, decimal Qty)>();
                foreach (var r in whRows.Where(r => r.Date == day && r.Outbound > 0))
                {
                    var left = r.Outbound;
                    for (var n = 0; left > 0 && n < 12; n++)
                    {
                        var q = Math.Min(left, bulky ? rng.Next(8, 25) : rng.Next(1, 9));
                        lines.Add((r.Sku, q));
                        left -= q;
                    }
                }
                if (lines.Count == 0) continue;
                lines = lines.OrderBy(_ => rng.Next()).ToList();

                var operators = Enumerable.Range(1, 4).ToDictionary(i => $"OP{i}", _ => day.ToDateTime(new TimeOnly(8, 0)));
                var orderNumber = 0;
                var index = 0;
                while (index < lines.Count)
                {
                    var size = rng.NextDouble() switch { < 0.4 => 1, < 0.85 => rng.Next(2, 5), _ => rng.Next(5, 9) };
                    var order = lines.Skip(index).Take(size).ToList();
                    index += order.Count;
                    var orderRef = $"{warehouse.Value}-{day:yyMMdd}-{++orderNumber:0000}";
                    var op = operators.OrderBy(o => o.Value).First().Key;
                    var clock = operators[op];

                    string? previousZone = null;
                    foreach (var (sku, qty) in order.OrderBy(l => ZoneOf(l.Sku)))
                    {
                        var zone = ZoneOf(sku);
                        var minutes = ZoneMinutesPerLine[zone] * (0.8 + rng.NextDouble() * 0.4) + 0.06 * (double)qty;
                        if (previousZone is null) minutes += 1.5;                    // presa in carico dell'ordine
                        else if (previousZone != zone) minutes += 0.8;               // spostamento tra zone
                        if (relocation && zone == "C") minutes *= 1.25;              // congestione della zona C
                        previousZone = zone;

                        var end = clock.AddMinutes(minutes);
                        tasks.Add(new DemoTask(new WarehouseTask($"{++id}", warehouse, "PICK", zone, orderRef, sku,
                            $"{zone}-{rng.Next(1, 30):00}-{rng.Next(1, 5)}", qty, clock, end), op));
                        clock = end;
                    }

                    // Imballo: una missione per ordine, non toccata dagli scenari.
                    var pack = 0.5 + 0.2 * order.Count;
                    var packStart = clock.AddMinutes(0.3);
                    tasks.Add(new DemoTask(new WarehouseTask($"{++id}", warehouse, "PACK", "IMB", orderRef, null, null,
                        order.Sum(o => o.Qty), packStart, packStart.AddMinutes(pack * (0.85 + rng.NextDouble() * 0.3))), op));
                    operators[op] = packStart.AddMinutes(pack + 0.2);
                }
            }
        }
        return tasks;
    }
}
