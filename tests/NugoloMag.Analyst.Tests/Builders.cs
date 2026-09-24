using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Tests;

/// <summary>Costruisce serie di posizioni coerenti (giacenza = ieri + entrate - uscite + rettifiche).</summary>
internal static class Builders
{
    public static readonly DateOnly AsOf = new(2026, 9, 23);
    public static readonly AnalysisWindow Window = new(AsOf, baselineDays: 28, recentDays: 7);

    public static List<StockDay> Item(
        string warehouse, string sku, string category,
        Func<DateOnly, decimal> outbound, decimal startStock = 10_000, int days = 40, decimal unitCost = 10m)
    {
        var rows = new List<StockDay>();
        var stock = startStock;
        for (var d = AsOf.AddDays(-(days - 1)); d <= AsOf; d = d.AddDays(1))
        {
            var o = outbound(d);
            stock -= o;
            rows.Add(new StockDay(d, new WarehouseCode(warehouse), new Sku(sku), category, stock, 0, o, 0, unitCost));
        }
        return rows;
    }

    /// <summary>Domanda stabile con piccolo rumore deterministico.</summary>
    public static Func<DateOnly, decimal> Steady(decimal level) => d => level + (d.DayNumber % 3) - 1;
}
