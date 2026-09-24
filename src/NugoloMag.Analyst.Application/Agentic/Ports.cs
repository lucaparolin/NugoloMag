using NugoloMag.Analyst.Domain.Agentic;

namespace NugoloMag.Analyst.Application.Agentic;

public interface IIncidentStore
{
    Task<IReadOnlyList<Incident>> ListOpenAsync(string sourceName, CancellationToken ct = default);
    Task<IReadOnlyList<Incident>> ListAsync(string? sourceName, bool includeResolved, int take, CancellationToken ct = default);
    Task<Incident?> GetAsync(long id, CancellationToken ct = default);
    /// <summary>Inserisce (Id = 0) o aggiorna; restituisce l'Id.</summary>
    Task<long> SaveAsync(Incident incident, CancellationToken ct = default);
    Task AddFeedbackAsync(IncidentFeedback feedback, CancellationToken ct = default);
    Task<IReadOnlyList<IncidentFeedback>> FeedbackAsync(long incidentId, CancellationToken ct = default);
    /// <summary>Quante volte condizioni con questa firma sono state giudicate irrilevanti o ridondanti.</summary>
    Task<int> DismissalsAsync(string sourceName, string signature, CancellationToken ct = default);
}

public interface IBriefingStore
{
    Task<long> SaveAsync(ExecutiveBriefing briefing, CancellationToken ct = default);
    Task<ExecutiveBriefing?> LatestAsync(string sourceName, CancellationToken ct = default);
    Task<ExecutiveBriefing?> GetAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<(long Id, string SourceName, DateOnly AsOf, DateTimeOffset CreatedAt)>> ListAsync(int take, CancellationToken ct = default);
}

public interface IInvestigationStore
{
    Task<long> SaveAsync(InvestigationCase investigation, CancellationToken ct = default);
    Task<InvestigationCase?> GetAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<InvestigationCase>> ListAsync(int take, CancellationToken ct = default);
}
