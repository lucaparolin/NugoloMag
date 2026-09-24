namespace NugoloMag.Analyst.Domain.Discovery;

/// <summary>Il significato di magazzino che l'agente attribuisce a una colonna.</summary>
public enum ColumnRole
{
    Date,
    Warehouse,
    Item,
    Category,
    Quantity,
    InboundQuantity,
    OutboundQuantity,
    OnHand,
    MovementType,
    UnitCost,
    Description
}

/// <summary>Il ruolo di una tabella nel modello di magazzino.</summary>
public enum TableRole
{
    Movements,
    StockSnapshot,
    ItemMaster
}

public sealed record ColumnAssignment(ColumnRole Role, string Column, int Score);

/// <summary>Una tabella candidata a svolgere un ruolo, con punteggio e motivazioni leggibili.</summary>
public sealed record TableCandidate(
    TableName Table,
    TableRole Role,
    int Score,
    IReadOnlyList<ColumnAssignment> Columns,
    IReadOnlyList<string> Reasons)
{
    public string? ColumnFor(ColumnRole role) => Columns.FirstOrDefault(c => c.Role == role)?.Column;
}

/// <summary>Frequenza di un valore (es. causale di magazzino) con la quantità netta movimentata.</summary>
public sealed record CodeFrequency(string Code, long Rows, double NetQuantity);

/// <summary>Profilo dei dati di una tabella candidata.</summary>
public sealed record TableProfile(
    long Rows,
    DateOnly? MinDate,
    DateOnly? MaxDate,
    long DistinctItems,
    long DistinctWarehouses,
    long DistinctDatesLast30Days,
    long RowsLast30Days,
    double NegativeQuantityShare);
