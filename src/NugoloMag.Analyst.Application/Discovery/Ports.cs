using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain.Discovery;

namespace NugoloMag.Analyst.Application.Discovery;

/// <summary>Le sorgenti configurate (nome → database). I segreti di connessione restano nella configurazione.</summary>
public interface ISourceRegistry
{
    IReadOnlyList<string> Names { get; }
    ISourceDatabase Get(string name);
}

/// <summary>Accesso in sola lettura al database del gestionale: schema, profilazione, query di magazzino.</summary>
public interface ISourceDatabase
{
    string Name { get; }
    Task<DatabaseCatalog> ReadCatalogAsync(CancellationToken ct = default);
    Task<TableProfile> ProfileAsync(TableCandidate candidate, DateOnly today, CancellationToken ct = default);
    Task<IReadOnlyList<CodeFrequency>> TopValuesAsync(TableName table, string column, string? quantityColumn, CancellationToken ct = default);
    string BuildInventoryQuery(SourceMapping mapping);
    IInventoryRepository Inventory(string query);

    /// <summary>Esegue una query già verificata da <c>ReadOnlySqlGuard</c>, in una transazione sempre annullata.</summary>
    Task<NugoloMag.Analyst.Domain.Assistant.QueryResult> QueryAsync(string sql, int maxRows, CancellationToken ct = default);
}

/// <summary>Revisione facoltativa della proposta da parte di un LLM. Non modifica il mapping, lo commenta.</summary>
public interface ISchemaAdvisor
{
    Task<string?> ReviewAsync(DiscoveryReport report, CancellationToken ct = default);
}

public sealed class NoSchemaAdvisor : ISchemaAdvisor
{
    public Task<string?> ReviewAsync(DiscoveryReport report, CancellationToken ct = default) => Task.FromResult<string?>(null);
}

public interface IDiscoveryStore
{
    Task<long> SaveAsync(DiscoveryReport report, CancellationToken ct = default);
    Task UpdateAsync(DiscoveryReport report, CancellationToken ct = default);
    Task<DiscoveryReport?> GetAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<DiscoverySummary>> ListAsync(int take, CancellationToken ct = default);
}

public sealed record DiscoverySummary(long Id, string SourceName, string Database, DateTimeOffset CreatedAt, Readiness Readiness);
