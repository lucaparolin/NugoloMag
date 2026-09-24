namespace NugoloMag.Analyst.Domain;

/// <summary>Codice magazzino (es. "MI01").</summary>
public readonly record struct WarehouseCode
{
    public string Value { get; }

    public WarehouseCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Warehouse code is required.", nameof(value));
        Value = value.Trim().ToUpperInvariant();
    }

    public override string ToString() => Value;
}

/// <summary>Codice articolo.</summary>
public readonly record struct Sku
{
    public string Value { get; }

    public Sku(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("SKU is required.", nameof(value));
        Value = value.Trim().ToUpperInvariant();
    }

    public override string ToString() => Value;
}
