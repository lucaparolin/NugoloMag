namespace NugoloMag.Analyst.Domain;

/// <summary>
/// Oggetto di un'osservazione: un intero magazzino o un singolo articolo in un magazzino.
/// </summary>
public readonly record struct Subject(WarehouseCode Warehouse, Sku? Sku = null)
{
    public bool IsWarehouseLevel => Sku is null;

    public override string ToString() => Sku is null ? Warehouse.Value : $"{Warehouse.Value}/{Sku.Value}";
}
