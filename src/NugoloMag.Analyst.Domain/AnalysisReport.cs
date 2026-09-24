namespace NugoloMag.Analyst.Domain;

/// <summary>Il prodotto finale: findings ordinati per importanza + sintesi in linguaggio naturale.</summary>
public sealed record AnalysisReport(
    AnalysisWindow Window,
    IReadOnlyList<WarehouseCode> Warehouses,
    int RowsAnalyzed,
    IReadOnlyList<Finding> Findings,
    string Narrative,
    DateTimeOffset GeneratedAt);
