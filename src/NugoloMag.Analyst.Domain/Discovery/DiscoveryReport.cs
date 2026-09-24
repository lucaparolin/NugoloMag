namespace NugoloMag.Analyst.Domain.Discovery;

public enum StepStatus { Ok, Warning, Failed, Skipped }

/// <summary>Un passo del ragionamento dell'agente, mostrato all'utente come traccia verificabile.</summary>
public sealed record AgentStep(string Title, StepStatus Status, IReadOnlyList<string> Details, double Seconds);

public sealed record ValidationCheck(string Name, StepStatus Status, string Detail);

/// <summary>Esito della prova della query sui dati reali.</summary>
public sealed record SourceValidation(
    DateOnly From,
    DateOnly To,
    int Rows,
    int Warehouses,
    int Items,
    IReadOnlyList<ValidationCheck> Checks,
    IReadOnlyList<StockDay> Preview)
{
    public bool HasFailures => Checks.Any(c => c.Status == StepStatus.Failed);
}

public enum Readiness { Ready, NeedsReview, Blocked }

/// <summary>
/// Esito completo dell'analisi del database: cosa c'è, cosa l'agente ha capito, cosa propone e con quale fiducia.
/// </summary>
public sealed record DiscoveryReport
{
    public long Id { get; init; }
    public required string SourceName { get; init; }
    public required string Database { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required int TableCount { get; init; }
    public required int ColumnCount { get; init; }
    public required IReadOnlyList<AgentStep> Steps { get; init; }
    public required IReadOnlyList<TableCandidate> Candidates { get; init; }
    /// <summary>Solo le tabelle candidate (non tutto il catalogo): bastano per rivedere e validare il mapping.</summary>
    public required IReadOnlyList<CatalogTable> CandidateTables { get; init; }
    public IReadOnlyList<CodeFrequency> MovementCodes { get; init; } = [];
    public SourceMapping? Mapping { get; init; }
    public string? SourceQuery { get; init; }
    public SourceValidation? Validation { get; init; }
    public string? AdvisorNotes { get; init; }
    public required Readiness Readiness { get; init; }

    public DatabaseCatalog ToCandidateCatalog() => new(Database, CandidateTables, []);
}
