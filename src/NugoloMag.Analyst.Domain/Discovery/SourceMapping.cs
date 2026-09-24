namespace NugoloMag.Analyst.Domain.Discovery;

/// <summary>Come si ricava il verso di un movimento.</summary>
public enum MovementDirection
{
    /// <summary>Quantità con segno: positiva entra, negativa esce.</summary>
    SignedQuantity,
    /// <summary>Due colonne distinte per quantità caricata e scaricata.</summary>
    SeparateColumns,
    /// <summary>Quantità positiva + causale/tipo movimento che ne stabilisce il verso.</summary>
    TypeCode
}

public sealed record MovementSource(
    TableName Table,
    string DateColumn,
    string? WarehouseColumn,
    string ItemColumn,
    MovementDirection Direction,
    string? QuantityColumn,
    string? InboundColumn,
    string? OutboundColumn,
    string? TypeColumn,
    IReadOnlyList<string> InboundCodes,
    IReadOnlyList<string> OutboundCodes,
    IReadOnlyList<string> AdjustmentCodes);

/// <summary>Tabella di saldi storici giornalieri (facoltativa): se c'è, la giacenza viene da qui.</summary>
public sealed record SnapshotSource(TableName Table, string DateColumn, string? WarehouseColumn, string ItemColumn, string OnHandColumn);

/// <summary>Tabella delle missioni di magazzino (facoltativa): abilita l'agente Produttività.</summary>
public sealed record TaskSource(
    TableName Table,
    string? WarehouseColumn,
    string StartColumn,
    string EndColumn,
    string? ZoneColumn,
    string? ActivityColumn,
    string? OrderColumn,
    string? ItemColumn,
    string? LocationColumn,
    string? QuantityColumn);

public sealed record ItemMasterSource(TableName Table, string KeyColumn, string? CategoryColumn, string? CostColumn);

/// <summary>
/// Il risultato "operativo" dell'analisi del database: come ottenere le posizioni giornaliere di magazzino
/// dalle tabelle del gestionale. Da qui si genera la query del monitoraggio.
/// </summary>
public sealed record SourceMapping(
    MovementSource Movements,
    SnapshotSource? Snapshot,
    ItemMasterSource? Items,
    string DefaultWarehouse = "MAG",
    TaskSource? Tasks = null)
{
    /// <summary>Verifica che ogni tabella e colonna esista davvero e abbia un tipo sensato. È la barriera anti-injection.</summary>
    public IReadOnlyList<string> Validate(DatabaseCatalog catalog)
    {
        var errors = new List<string>();

        var mov = Require(catalog, Movements.Table, errors);
        if (mov is not null)
        {
            RequireColumn(mov, Movements.DateColumn, errors, c => c.IsDate, "deve essere una data");
            RequireColumn(mov, Movements.ItemColumn, errors, c => c.IsKeyLike, "deve essere un codice");
            OptionalColumn(mov, Movements.WarehouseColumn, errors, c => c.IsKeyLike, "deve essere un codice");
            switch (Movements.Direction)
            {
                case MovementDirection.SeparateColumns:
                    RequireColumn(mov, Movements.InboundColumn, errors, c => c.IsNumeric, "deve essere numerica");
                    RequireColumn(mov, Movements.OutboundColumn, errors, c => c.IsNumeric, "deve essere numerica");
                    break;
                case MovementDirection.TypeCode:
                    RequireColumn(mov, Movements.QuantityColumn, errors, c => c.IsNumeric, "deve essere numerica");
                    RequireColumn(mov, Movements.TypeColumn, errors, c => c.IsKeyLike, "deve essere un codice");
                    if (Movements.InboundCodes.Count + Movements.OutboundCodes.Count == 0)
                        errors.Add("Nessuna causale assegnata a carichi o scarichi.");
                    break;
                default:
                    RequireColumn(mov, Movements.QuantityColumn, errors, c => c.IsNumeric, "deve essere numerica");
                    break;
            }
            foreach (var code in Movements.InboundCodes.Concat(Movements.OutboundCodes).Concat(Movements.AdjustmentCodes))
                if (code.Length > 50) errors.Add($"Causale troppo lunga: {code[..20]}…");
        }

        if (Snapshot is not null && Require(catalog, Snapshot.Table, errors) is { } snap)
        {
            RequireColumn(snap, Snapshot.DateColumn, errors, c => c.IsDate, "deve essere una data");
            RequireColumn(snap, Snapshot.ItemColumn, errors, c => c.IsKeyLike, "deve essere un codice");
            OptionalColumn(snap, Snapshot.WarehouseColumn, errors, c => c.IsKeyLike, "deve essere un codice");
            RequireColumn(snap, Snapshot.OnHandColumn, errors, c => c.IsNumeric, "deve essere numerica");
        }

        if (Items is not null && Require(catalog, Items.Table, errors) is { } items)
        {
            RequireColumn(items, Items.KeyColumn, errors, c => c.IsKeyLike, "deve essere un codice");
            OptionalColumn(items, Items.CategoryColumn, errors, _ => true, "");
            OptionalColumn(items, Items.CostColumn, errors, c => c.IsNumeric, "deve essere numerica");
        }

        if (Tasks is not null && Require(catalog, Tasks.Table, errors) is { } tasks)
        {
            RequireColumn(tasks, Tasks.StartColumn, errors, c => c.IsDate, "deve essere una data/ora");
            RequireColumn(tasks, Tasks.EndColumn, errors, c => c.IsDate, "deve essere una data/ora");
            foreach (var column in new[] { Tasks.WarehouseColumn, Tasks.ZoneColumn, Tasks.ActivityColumn, Tasks.OrderColumn, Tasks.ItemColumn, Tasks.LocationColumn })
                OptionalColumn(tasks, column, errors, c => c.IsKeyLike, "deve essere un codice");
            OptionalColumn(tasks, Tasks.QuantityColumn, errors, c => c.IsNumeric, "deve essere numerica");
        }

        if (string.IsNullOrWhiteSpace(DefaultWarehouse) || DefaultWarehouse.Length > 20)
            errors.Add("Codice magazzino predefinito non valido.");

        return errors;
    }

    private static CatalogTable? Require(DatabaseCatalog catalog, TableName name, List<string> errors)
    {
        var table = catalog.Find(name);
        if (table is null) errors.Add($"Tabella {name} non trovata.");
        return table;
    }

    private static void RequireColumn(CatalogTable table, string? column, List<string> errors, Func<CatalogColumn, bool> typeOk, string typeMessage)
    {
        if (string.IsNullOrWhiteSpace(column)) { errors.Add($"{table.Name}: colonna obbligatoria non indicata."); return; }
        OptionalColumn(table, column, errors, typeOk, typeMessage);
    }

    private static void OptionalColumn(CatalogTable table, string? column, List<string> errors, Func<CatalogColumn, bool> typeOk, string typeMessage)
    {
        if (string.IsNullOrWhiteSpace(column)) return;
        var c = table.Column(column);
        if (c is null) errors.Add($"{table.Name}: colonna {column} non trovata.");
        else if (!typeOk(c)) errors.Add($"{table.Name}.{column} ({c.SqlType}) {typeMessage}.");
    }
}
