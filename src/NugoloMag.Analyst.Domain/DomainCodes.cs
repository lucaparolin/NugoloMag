namespace NugoloMag.Analyst.Domain;

/// <summary>
/// Codici stabili per persistenza e integrazioni. Mappatura esplicita (niente Enum.ToString/Parse):
/// rinominare un membro dell'enum non cambia i dati salvati.
/// </summary>
public static class DomainCodes
{
    public static string Code(this FindingKind kind) => kind switch
    {
        FindingKind.Spike => "spike",
        FindingKind.Drop => "drop",
        FindingKind.TrendReversal => "trend-reversal",
        FindingKind.NewItem => "new-item",
        FindingKind.StalledItem => "stalled-item",
        FindingKind.Stockout => "stockout",
        FindingKind.MixDrift => "mix-drift",
        FindingKind.DataFreshness => "data-freshness",
        FindingKind.Integrity => "integrity",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string Code(this Severity severity) => severity switch
    {
        Severity.High => "high",
        Severity.Medium => "medium",
        Severity.Low => "low",
        _ => throw new ArgumentOutOfRangeException(nameof(severity))
    };

    public static string Code(this Metric metric) => metric switch
    {
        Metric.OnHand => "on-hand",
        Metric.Inbound => "inbound",
        Metric.Outbound => "outbound",
        Metric.Adjustment => "adjustment",
        Metric.StockValue => "stock-value",
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };
}
