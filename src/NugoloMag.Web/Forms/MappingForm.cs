using NugoloMag.Analyst.Domain.Discovery;

namespace NugoloMag.Web.Forms;

/// <summary>
/// Il mapping come l'utente lo vede e lo modifica nel form. Conversione esplicita da/verso <see cref="SourceMapping"/>.
/// La validazione vera (esistenza di tabelle e colonne) la fa il dominio sul catalogo analizzato.
/// </summary>
public sealed record MappingForm
{
    public const string DirectionSigned = "signed";
    public const string DirectionSeparate = "separate";
    public const string DirectionType = "type";

    public string MovTable { get; init; } = "";
    public string? MovDate { get; init; }
    public string? MovWarehouse { get; init; }
    public string? MovItem { get; init; }
    public string Direction { get; init; } = DirectionSigned;
    public string? MovQuantity { get; init; }
    public string? MovInbound { get; init; }
    public string? MovOutbound { get; init; }
    public string? MovType { get; init; }
    /// <summary>Causale → "in" | "out" | "adj" | "none".</summary>
    public IReadOnlyDictionary<string, string> Codes { get; init; } = new Dictionary<string, string>();

    public bool UseSnapshot { get; init; }
    public string? SnapTable { get; init; }
    public string? SnapDate { get; init; }
    public string? SnapWarehouse { get; init; }
    public string? SnapItem { get; init; }
    public string? SnapOnHand { get; init; }

    public bool UseItems { get; init; }
    public string? ItemsTable { get; init; }
    public string? ItemsKey { get; init; }
    public string? ItemsCategory { get; init; }
    public string? ItemsCost { get; init; }

    public string DefaultWarehouse { get; init; } = "MAG";

    public bool UseTasks { get; init; }
    public string? TaskTable { get; init; }
    public string? TaskWarehouse { get; init; }
    public string? TaskStart { get; init; }
    public string? TaskEnd { get; init; }
    public string? TaskZone { get; init; }
    public string? TaskActivity { get; init; }
    public string? TaskOrder { get; init; }
    public string? TaskItem { get; init; }
    public string? TaskLocation { get; init; }
    public string? TaskQuantity { get; init; }

    public static MappingForm FromMapping(SourceMapping? mapping, IReadOnlyList<CodeFrequency> codes)
    {
        if (mapping is null) return new MappingForm { Codes = codes.ToDictionary(c => c.Code, _ => "none") };
        var m = mapping.Movements;
        return new MappingForm
        {
            MovTable = m.Table.ToString(),
            MovDate = m.DateColumn,
            MovWarehouse = m.WarehouseColumn,
            MovItem = m.ItemColumn,
            Direction = m.Direction switch
            {
                MovementDirection.SeparateColumns => DirectionSeparate,
                MovementDirection.TypeCode => DirectionType,
                _ => DirectionSigned
            },
            MovQuantity = m.QuantityColumn,
            MovInbound = m.InboundColumn,
            MovOutbound = m.OutboundColumn,
            MovType = m.TypeColumn,
            Codes = codes.Select(c => c.Code)
                .Concat(m.InboundCodes).Concat(m.OutboundCodes).Concat(m.AdjustmentCodes)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(c => c, c =>
                    m.InboundCodes.Contains(c) ? "in" :
                    m.OutboundCodes.Contains(c) ? "out" :
                    m.AdjustmentCodes.Contains(c) ? "adj" : "none"),
            UseSnapshot = mapping.Snapshot is not null,
            SnapTable = mapping.Snapshot?.Table.ToString(),
            SnapDate = mapping.Snapshot?.DateColumn,
            SnapWarehouse = mapping.Snapshot?.WarehouseColumn,
            SnapItem = mapping.Snapshot?.ItemColumn,
            SnapOnHand = mapping.Snapshot?.OnHandColumn,
            UseItems = mapping.Items is not null,
            ItemsTable = mapping.Items?.Table.ToString(),
            ItemsKey = mapping.Items?.KeyColumn,
            ItemsCategory = mapping.Items?.CategoryColumn,
            ItemsCost = mapping.Items?.CostColumn,
            DefaultWarehouse = mapping.DefaultWarehouse,
            UseTasks = mapping.Tasks is not null,
            TaskTable = mapping.Tasks?.Table.ToString(),
            TaskWarehouse = mapping.Tasks?.WarehouseColumn,
            TaskStart = mapping.Tasks?.StartColumn,
            TaskEnd = mapping.Tasks?.EndColumn,
            TaskZone = mapping.Tasks?.ZoneColumn,
            TaskActivity = mapping.Tasks?.ActivityColumn,
            TaskOrder = mapping.Tasks?.OrderColumn,
            TaskItem = mapping.Tasks?.ItemColumn,
            TaskLocation = mapping.Tasks?.LocationColumn,
            TaskQuantity = mapping.Tasks?.QuantityColumn
        };
    }

    public static MappingForm FromForm(FormReader f)
    {
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in f.Keys.Where(k => k.StartsWith("codev.", StringComparison.Ordinal)))
        {
            var index = key["codev.".Length..];
            if (f.Text(key) is { } code)
                codes[code] = f.Text($"code.{index}") is "in" or "out" or "adj" ? f.Text($"code.{index}")! : "none";
        }

        return new MappingForm
        {
            MovTable = f.Required("mov.table", "Tabella movimenti"),
            MovDate = f.Text("mov.date"),
            MovWarehouse = f.Text("mov.warehouse"),
            MovItem = f.Text("mov.item"),
            Direction = f.Text("mov.direction") is DirectionSeparate or DirectionType ? f.Text("mov.direction")! : DirectionSigned,
            MovQuantity = f.Text("mov.quantity"),
            MovInbound = f.Text("mov.inbound"),
            MovOutbound = f.Text("mov.outbound"),
            MovType = f.Text("mov.type"),
            Codes = codes,
            UseSnapshot = f.Flag("snap.enabled"),
            SnapTable = f.Text("snap.table"),
            SnapDate = f.Text("snap.date"),
            SnapWarehouse = f.Text("snap.warehouse"),
            SnapItem = f.Text("snap.item"),
            SnapOnHand = f.Text("snap.onhand"),
            UseItems = f.Flag("items.enabled"),
            ItemsTable = f.Text("items.table"),
            ItemsKey = f.Text("items.key"),
            ItemsCategory = f.Text("items.category"),
            ItemsCost = f.Text("items.cost"),
            DefaultWarehouse = f.Text("defaultWarehouse") ?? "MAG",
            UseTasks = f.Flag("tasks.enabled"),
            TaskTable = f.Text("tasks.table"),
            TaskWarehouse = f.Text("tasks.warehouse"),
            TaskStart = f.Text("tasks.start"),
            TaskEnd = f.Text("tasks.end"),
            TaskZone = f.Text("tasks.zone"),
            TaskActivity = f.Text("tasks.activity"),
            TaskOrder = f.Text("tasks.order"),
            TaskItem = f.Text("tasks.item"),
            TaskLocation = f.Text("tasks.location"),
            TaskQuantity = f.Text("tasks.quantity")
        };
    }

    /// <summary>
    /// Dopo il cambio di una tabella nel form, le colonne della vecchia tabella non valgono più:
    /// si ripartisce dai suggerimenti dell'agente per la nuova tabella.
    /// </summary>
    public MappingForm WithSuggestionsFor(MappingForm previous, IReadOnlyList<TableCandidate> candidates)
    {
        var result = this;
        if (!SameTable(MovTable, previous.MovTable) && Suggest(candidates, MovTable, TableRole.Movements) is { } mov)
            result = result with
            {
                MovDate = mov.ColumnFor(ColumnRole.Date), MovWarehouse = mov.ColumnFor(ColumnRole.Warehouse), MovItem = mov.ColumnFor(ColumnRole.Item),
                MovQuantity = mov.ColumnFor(ColumnRole.Quantity), MovInbound = mov.ColumnFor(ColumnRole.InboundQuantity),
                MovOutbound = mov.ColumnFor(ColumnRole.OutboundQuantity), MovType = mov.ColumnFor(ColumnRole.MovementType)
            };
        if (!SameTable(SnapTable, previous.SnapTable) && Suggest(candidates, SnapTable, TableRole.StockSnapshot) is { } snap)
            result = result with
            {
                SnapDate = snap.ColumnFor(ColumnRole.Date), SnapWarehouse = snap.ColumnFor(ColumnRole.Warehouse),
                SnapItem = snap.ColumnFor(ColumnRole.Item), SnapOnHand = snap.ColumnFor(ColumnRole.OnHand)
            };
        if (!SameTable(TaskTable, previous.TaskTable) && Suggest(candidates, TaskTable, TableRole.Tasks) is { } tasks)
            result = result with
            {
                TaskWarehouse = tasks.ColumnFor(ColumnRole.Warehouse), TaskStart = tasks.ColumnFor(ColumnRole.StartTime), TaskEnd = tasks.ColumnFor(ColumnRole.EndTime),
                TaskZone = tasks.ColumnFor(ColumnRole.Zone), TaskActivity = tasks.ColumnFor(ColumnRole.Activity), TaskOrder = tasks.ColumnFor(ColumnRole.OrderRef),
                TaskItem = tasks.ColumnFor(ColumnRole.Item), TaskLocation = tasks.ColumnFor(ColumnRole.Location), TaskQuantity = tasks.ColumnFor(ColumnRole.Quantity)
            };
        if (!SameTable(ItemsTable, previous.ItemsTable) && Suggest(candidates, ItemsTable, TableRole.ItemMaster) is { } items)
            result = result with
            {
                ItemsKey = items.ColumnFor(ColumnRole.Item), ItemsCategory = items.ColumnFor(ColumnRole.Category), ItemsCost = items.ColumnFor(ColumnRole.UnitCost)
            };
        return result;
    }

    public SourceMapping ToMapping()
    {
        string Req(string? v, string label) => v ?? throw new FormException($"{label}: campo obbligatorio.");
        List<string> CodesFor(string target) => Codes.Where(c => c.Value == target).Select(c => c.Key).ToList();

        var direction = Direction switch
        {
            DirectionSeparate => MovementDirection.SeparateColumns,
            DirectionType => MovementDirection.TypeCode,
            _ => MovementDirection.SignedQuantity
        };

        var movements = new MovementSource(
            TableName.Parse(MovTable), Req(MovDate, "Data movimento"), MovWarehouse, Req(MovItem, "Articolo"), direction,
            direction == MovementDirection.SeparateColumns ? null : Req(MovQuantity, "Quantità"),
            direction == MovementDirection.SeparateColumns ? Req(MovInbound, "Quantità caricata") : null,
            direction == MovementDirection.SeparateColumns ? Req(MovOutbound, "Quantità scaricata") : null,
            direction == MovementDirection.TypeCode ? Req(MovType, "Causale") : null,
            direction == MovementDirection.TypeCode ? CodesFor("in") : [],
            direction == MovementDirection.TypeCode ? CodesFor("out") : [],
            direction == MovementDirection.TypeCode ? CodesFor("adj") : []);

        var snapshot = UseSnapshot
            ? new SnapshotSource(TableName.Parse(Req(SnapTable, "Tabella saldi")), Req(SnapDate, "Data saldo"), SnapWarehouse,
                Req(SnapItem, "Articolo saldo"), Req(SnapOnHand, "Giacenza"))
            : null;

        var items = UseItems
            ? new ItemMasterSource(TableName.Parse(Req(ItemsTable, "Anagrafica articoli")), Req(ItemsKey, "Codice articolo"), ItemsCategory, ItemsCost)
            : null;

        var tasks = UseTasks
            ? new TaskSource(TableName.Parse(Req(TaskTable, "Tabella missioni")), TaskWarehouse, Req(TaskStart, "Inizio missione"), Req(TaskEnd, "Fine missione"),
                TaskZone, TaskActivity, TaskOrder, TaskItem, TaskLocation, TaskQuantity)
            : null;

        return new SourceMapping(movements, snapshot, items, DefaultWarehouse, tasks);
    }

    private static bool SameTable(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static TableCandidate? Suggest(IReadOnlyList<TableCandidate> candidates, string? table, TableRole role) =>
        table is null ? null : candidates.FirstOrDefault(c => c.Role == role && SameTable(c.Table.ToString(), table));
}
