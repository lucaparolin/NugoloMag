using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Infrastructure.Demo;

/// <summary>
/// Genera 3 magazzini con stagionalità settimanale e rumore, poi inietta anomalie note
/// nell'ultimo periodo: serve a provare lo strumento e come banco di prova per i test.
/// </summary>
public sealed class SyntheticInventoryGenerator(int seed = 42)
{
    private static readonly string[] Categories = ["Alimentari", "Bevande", "Casa", "Elettronica", "Cura persona"];
    private static readonly string[] Warehouses = ["MI01", "RM02", "NA03"];

    public IReadOnlyList<StockDay> Generate(DateOnly asOf, int days = 60, int skusPerWarehouse = 40)
    {
        var rng = new Random(seed);
        var start = asOf.AddDays(-(days - 1));
        var rows = new List<StockDay>();

        foreach (var wh in Warehouses)
        {
            for (var i = 1; i <= skusPerWarehouse; i++)
            {
                var sku = new Sku($"{wh[..2]}-{i:000}");
                var category = Categories[i % Categories.Length];
                var baseDemand = 5 + rng.Next(0, 40);
                var unitCost = Math.Round((decimal)(2 + rng.NextDouble() * 60), 2);
                decimal onHand = baseDemand * 12;

                for (var d = start; d <= asOf; d = d.AddDays(1))
                {
                    var isSunday = d.DayOfWeek == DayOfWeek.Sunday;
                    var weekly = d.DayOfWeek switch { DayOfWeek.Monday => 1.3, DayOfWeek.Saturday => 0.6, DayOfWeek.Sunday => 0, _ => 1.0 };
                    var demand = isSunday ? 0 : Math.Max(0, (int)Math.Round(baseDemand * weekly * (0.85 + rng.NextDouble() * 0.3)));

                    var outbound = (decimal)Math.Min(demand, (double)Math.Max(onHand, 0));
                    var inbound = !isSunday && onHand - outbound < baseDemand * 5 ? baseDemand * 8m : 0m;
                    var adjustment = rng.NextDouble() < 0.02 ? -1m : 0m;

                    rows.Add(new StockDay(d, new WarehouseCode(wh), sku, category, 0, inbound, outbound, adjustment, unitCost));
                    onHand += inbound - outbound + adjustment;
                    rows[^1] = rows[^1] with { OnHand = onHand };
                }
            }
        }

        return InjectAnomalies(rows, asOf);
    }

    private static List<StockDay> InjectAnomalies(List<StockDay> rows, DateOnly asOf)
    {
        var recent = asOf.AddDays(-6);

        // 1) MI01: la categoria Elettronica esplode oggi (promo non comunicata) → picco uscite con root cause.
        Update(rows, r => r.Warehouse.Value == "MI01" && r.Category == "Elettronica" && r.Date == asOf,
            r => r with { Outbound = r.Outbound * 5, OnHand = r.OnHand - r.Outbound * 4 });

        // 2) RM02: articolo top esaurito tre giorni fa e mai riassortito → rottura di stock.
        var rmTop = rows.Where(r => r.Warehouse.Value == "RM02").GroupBy(r => r.Sku)
            .OrderByDescending(g => g.Sum(r => r.Outbound)).First().Key;
        var lastStock = rows.Single(r => r.Sku == rmTop && r.Date == asOf.AddDays(-4)).OnHand;
        Update(rows, r => r.Sku == rmTop && r.Date == asOf.AddDays(-3), r => r with { Outbound = lastStock, Inbound = 0, OnHand = 0 });
        Update(rows, r => r.Sku == rmTop && r.Date > asOf.AddDays(-3), r => r with { Outbound = 0, Inbound = 0, OnHand = 0 });

        // 3) RM02: articolo con 3 giorni senza uscite ma merce a stock (bloccato? ubicazione errata?).
        var rmStalled = rows.Where(r => r.Warehouse.Value == "RM02" && r.Sku != rmTop).GroupBy(r => r.Sku)
            .OrderByDescending(g => g.Sum(r => r.Outbound)).First().Key;
        var stalledStock = rows.Single(r => r.Sku == rmStalled && r.Date == asOf.AddDays(-3)).OnHand;
        Update(rows, r => r.Sku == rmStalled && r.Date >= asOf.AddDays(-2),
            r => r with { Outbound = 0, Inbound = 0, Adjustment = 0, OnHand = stalledStock });

        // 4) NA03: nuovi articoli di una linea appena lanciata + mix spostato su Bevande.
        var na = new WarehouseCode("NA03");
        for (var i = 1; i <= 6; i++)
        {
            decimal stock = 0;
            for (var d = recent; d <= asOf; d = d.AddDays(1))
            {
                var inbound = d == recent ? 1000m : 0m;
                var outbound = d.DayOfWeek == DayOfWeek.Sunday ? 0m : 120m;
                stock += inbound - outbound;
                rows.Add(new StockDay(d, na, new Sku($"NA-NEW{i}"), "Bevande", stock, inbound, outbound, 0, 3.5m));
            }
        }

        // 5) NA03: squadrature e giacenze negative (movimenti non registrati).
        var naSkus = rows.Where(r => r.Warehouse == na && !r.Sku.Value.Contains("NEW")).Select(r => r.Sku).Distinct().Take(4).ToList();
        Update(rows, r => naSkus.Contains(r.Sku) && r.Date == asOf.AddDays(-1), r => r with { OnHand = r.OnHand - 150 });

        // 6) MI01: rettifica inventariale anomala oggi (ammanco).
        var miSku = rows.First(r => r.Warehouse.Value == "MI01" && r.Category == "Casa").Sku;
        Update(rows, r => r.Sku == miSku && r.Date == asOf, r => r with { Adjustment = -80, OnHand = r.OnHand - 80 });

        return rows;
    }

    private static void Update(List<StockDay> rows, Func<StockDay, bool> where, Func<StockDay, StockDay> change)
    {
        for (var i = 0; i < rows.Count; i++)
            if (where(rows[i])) rows[i] = change(rows[i]);
    }
}
