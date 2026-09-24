namespace NugoloMag.Analyst.Domain;

/// <summary>
/// Una missione di magazzino (riga di prelievo, imballo, ricevimento…) con zona e tempi: il fatto su cui si misura la produttività.
/// L'operatore non fa parte del modello per scelta: la produttività si spiega con le condizioni operative (blueprint §8, equità).
/// </summary>
public sealed record WarehouseTask(
    string TaskId,
    WarehouseCode Warehouse,
    string Activity,
    string Zone,
    string? OrderRef,
    Sku? Sku,
    string? Location,
    decimal Quantity,
    DateTime StartedAt,
    DateTime EndedAt)
{
    public double Minutes => Math.Max(0, (EndedAt - StartedAt).TotalMinutes);
    public DateOnly Date => DateOnly.FromDateTime(StartedAt);
}

/// <summary>Insieme di missioni caricate, con accessi indicizzati per magazzino e attività.</summary>
public sealed class TaskDataset
{
    private readonly ILookup<(WarehouseCode, string), WarehouseTask> _byActivity;

    public TaskDataset(IEnumerable<WarehouseTask> tasks)
    {
        Tasks = tasks.Where(t => t.EndedAt > t.StartedAt).ToList();
        _byActivity = Tasks.ToLookup(t => (t.Warehouse, t.Activity));
        Warehouses = Tasks.Select(t => t.Warehouse).Distinct().OrderBy(w => w.Value).ToList();
    }

    public IReadOnlyList<WarehouseTask> Tasks { get; }
    public IReadOnlyList<WarehouseCode> Warehouses { get; }
    public bool IsEmpty => Tasks.Count == 0;

    public IEnumerable<string> Activities(WarehouseCode warehouse) =>
        Tasks.Where(t => t.Warehouse == warehouse).Select(t => t.Activity).Distinct();

    public IEnumerable<WarehouseTask> For(WarehouseCode warehouse, string activity) => _byActivity[(warehouse, activity)];
}
