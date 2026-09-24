namespace NugoloMag.Analyst.Domain;

/// <summary>
/// Aggregato di sola lettura con le posizioni giornaliere caricate; espone serie storiche per soggetto e metrica.
/// </summary>
public sealed class InventoryDataset
{
    private readonly IReadOnlyList<StockDay> _rows;
    private readonly ILookup<WarehouseCode, StockDay> _byWarehouse;
    private readonly ILookup<(WarehouseCode, Sku), StockDay> _bySku;
    private readonly Dictionary<(Subject, Metric), TimeSeries> _cache = new();

    public InventoryDataset(IEnumerable<StockDay> rows)
    {
        _rows = rows.ToList();
        _byWarehouse = _rows.ToLookup(r => r.Warehouse);
        _bySku = _rows.ToLookup(r => (r.Warehouse, r.Sku));
        Warehouses = _byWarehouse.Select(g => g.Key).OrderBy(w => w.Value).ToList();
    }

    public IReadOnlyList<StockDay> Rows => _rows;
    public IReadOnlyList<WarehouseCode> Warehouses { get; }
    public bool IsEmpty => _rows.Count == 0;

    public IEnumerable<StockDay> In(WarehouseCode warehouse) => _byWarehouse[warehouse];

    public IEnumerable<StockDay> In(Subject subject) =>
        subject.Sku is { } sku ? _bySku[(subject.Warehouse, sku)] : _byWarehouse[subject.Warehouse];

    public IEnumerable<Sku> SkusIn(WarehouseCode warehouse) => In(warehouse).Select(r => r.Sku).Distinct();

    public DateOnly? LastDate(WarehouseCode warehouse) =>
        In(warehouse).Select(r => (DateOnly?)r.Date).DefaultIfEmpty(null).Max();

    /// <summary>Vero se il magazzino ha dati fino alla data indicata (altrimenti i confronti sarebbero falsati).</summary>
    public bool IsLoadedThrough(WarehouseCode warehouse, DateOnly date) => LastDate(warehouse) >= date;

    public TimeSeries Series(Subject subject, Metric metric)
    {
        if (_cache.TryGetValue((subject, metric), out var cached)) return cached;

        var series = new TimeSeries(
            In(subject)
                .GroupBy(r => r.Date)
                .Select(g => KeyValuePair.Create(g.Key, (double)g.Sum(r => r.Measure(metric)))));

        _cache[(subject, metric)] = series;
        return series;
    }

    /// <summary>Serie di una categoria merceologica dentro un magazzino (usata per la root cause).</summary>
    public TimeSeries CategorySeries(WarehouseCode warehouse, string category, Metric metric) =>
        new(In(warehouse)
            .Where(r => r.Category == category)
            .GroupBy(r => r.Date)
            .Select(g => KeyValuePair.Create(g.Key, (double)g.Sum(r => r.Measure(metric)))));

    public string CategoryOf(WarehouseCode warehouse, Sku sku) =>
        _bySku[(warehouse, sku)].Select(r => r.Category).FirstOrDefault() ?? "?";
}
