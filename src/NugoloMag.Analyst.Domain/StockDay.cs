namespace NugoloMag.Analyst.Domain;

/// <summary>
/// Posizione giornaliera di un articolo in un magazzino: il "fatto" su cui lavora l'analista.
/// OnHand è la giacenza a fine giornata; Inbound/Outbound/Adjustment sono i movimenti del giorno.
/// </summary>
public sealed record StockDay(
    DateOnly Date,
    WarehouseCode Warehouse,
    Sku Sku,
    string Category,
    decimal OnHand,
    decimal Inbound,
    decimal Outbound,
    decimal Adjustment,
    decimal UnitCost)
{
    public decimal StockValue => OnHand * UnitCost;

    public decimal Measure(Metric metric) => metric switch
    {
        Metric.OnHand => OnHand,
        Metric.Inbound => Inbound,
        Metric.Outbound => Outbound,
        Metric.Adjustment => Adjustment,
        Metric.StockValue => StockValue,
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null)
    };

    /// <summary>Vero se l'articolo è "vivo" quel giorno (ha giacenza o movimenti).</summary>
    public bool IsActive => OnHand != 0 || Inbound != 0 || Outbound != 0 || Adjustment != 0;
}

public enum Metric
{
    OnHand,
    Inbound,
    Outbound,
    Adjustment,
    StockValue
}
