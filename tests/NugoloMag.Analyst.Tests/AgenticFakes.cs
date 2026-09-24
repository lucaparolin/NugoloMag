using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Application.Agentic;
using NugoloMag.Analyst.Application.Assistant;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Application.Monitoring;
using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Domain.Agentic;
using NugoloMag.Analyst.Domain.Assistant;
using NugoloMag.Analyst.Domain.Discovery;
using NugoloMag.Analyst.Domain.Monitoring;

namespace NugoloMag.Analyst.Tests;

internal sealed class InMemoryIncidentStore : IIncidentStore
{
    private readonly Dictionary<long, Incident> _items = [];
    private readonly List<IncidentFeedback> _feedback = [];

    public Task<IReadOnlyList<Incident>> ListOpenAsync(string sourceName, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Incident>>(_items.Values.Where(i => i.SourceName == sourceName && i.IsOpen).ToList());
    public Task<IReadOnlyList<Incident>> ListAsync(string? sourceName, bool includeResolved, int take, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Incident>>(_items.Values.Where(i => (sourceName is null || i.SourceName == sourceName) && (includeResolved || i.IsOpen)).Take(take).ToList());
    public Task<Incident?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(_items.GetValueOrDefault(id));
    public Task<long> SaveAsync(Incident incident, CancellationToken ct = default)
    {
        var id = incident.Id == 0 ? _items.Count + 1 : incident.Id;
        _items[id] = incident with { Id = id };
        return Task.FromResult(id);
    }
    public Task AddFeedbackAsync(IncidentFeedback feedback, CancellationToken ct = default) { _feedback.Add(feedback); return Task.CompletedTask; }
    public Task<IReadOnlyList<IncidentFeedback>> FeedbackAsync(long incidentId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IncidentFeedback>>(_feedback.Where(f => f.IncidentId == incidentId).ToList());
    public Task<int> DismissalsAsync(string sourceName, string signature, CancellationToken ct = default) =>
        Task.FromResult(_feedback.Count(f => f.Verdict is FeedbackVerdict.Irrelevant or FeedbackVerdict.Redundant && _items.TryGetValue(f.IncidentId, out var i) && i.Signature == signature));
    public IReadOnlyCollection<Incident> All => _items.Values;
}

internal sealed class InMemoryBriefingStore : IBriefingStore
{
    public List<ExecutiveBriefing> Items { get; } = [];
    public Task<long> SaveAsync(ExecutiveBriefing briefing, CancellationToken ct = default) { Items.Add(briefing with { Id = Items.Count + 1 }); return Task.FromResult((long)Items.Count); }
    public Task<ExecutiveBriefing?> LatestAsync(string sourceName, CancellationToken ct = default) => Task.FromResult(Items.LastOrDefault());
    public Task<ExecutiveBriefing?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(b => b.Id == id));
    public Task<IReadOnlyList<(long Id, string SourceName, DateOnly AsOf, DateTimeOffset CreatedAt)>> ListAsync(int take, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(long, string, DateOnly, DateTimeOffset)>>(Items.Select(b => (b.Id, b.SourceName, b.AsOf, b.CreatedAt)).ToList());
}

internal sealed class InMemoryInvestigationStore : IInvestigationStore
{
    public List<InvestigationCase> Items { get; } = [];
    public Task<long> SaveAsync(InvestigationCase investigation, CancellationToken ct = default) { Items.Add(investigation with { Id = Items.Count + 1 }); return Task.FromResult((long)Items.Count); }
    public Task<InvestigationCase?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
    public Task<IReadOnlyList<InvestigationCase>> ListAsync(int take, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InvestigationCase>>(Items);
}

/// <summary>Sorgente finta: restituisce righe e missioni in memoria, la query è ignorata.</summary>
internal sealed class FakeSourceDatabase(IReadOnlyList<StockDay> rows, IReadOnlyList<WarehouseTask> tasks) : ISourceDatabase, ISourceRegistry
{
    public IReadOnlyList<StockDay> Rows { get; set; } = rows;
    public IReadOnlyList<WarehouseTask> TaskRows { get; set; } = tasks;
    public string Name => "demo";
    public IReadOnlyList<string> Names => ["demo"];
    public ISourceDatabase Get(string name) => this;
    public Task<DatabaseCatalog> ReadCatalogAsync(CancellationToken ct = default) => Task.FromResult(new DatabaseCatalog("demo", [], []));
    public Task<TableProfile> ProfileAsync(TableCandidate candidate, DateOnly today, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<CodeFrequency>> TopValuesAsync(TableName table, string column, string? quantityColumn, CancellationToken ct = default) => throw new NotSupportedException();
    public string BuildInventoryQuery(SourceMapping mapping) => "q";
    public IInventoryRepository Inventory(string query) => new Infrastructure.Data.InMemoryInventoryRepository(Rows);
    public string? BuildTaskQuery(SourceMapping mapping) => "t";
    public ITaskRepository Tasks(string query) => new InMemoryTaskRepository(TaskRows);
    public Task<QueryResult> QueryAsync(string sql, int maxRows, CancellationToken ct = default) =>
        Task.FromResult(new QueryResult(["N"], [["42"]], false, 0.01));

    private sealed class InMemoryTaskRepository(IReadOnlyList<WarehouseTask> tasks) : ITaskRepository
    {
        public Task<IReadOnlyList<WarehouseTask>> LoadAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WarehouseTask>>(tasks.Where(t => t.Date >= from && t.Date <= to).ToList());
    }
}

internal sealed class SingleMonitorStore(MonitorDefinition monitor) : IMonitorStore
{
    public Task<long> CreateAsync(MonitorDefinition m, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<MonitorDefinition?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult<MonitorDefinition?>(monitor);
    public Task<IReadOnlyList<MonitorDefinition>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MonitorDefinition>>([monitor]);
    public Task<IReadOnlyList<MonitorDefinition>> ListDueAsync(DateTimeOffset now, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MonitorDefinition>>([]);
    public Task SetScheduleAsync(long id, bool isActive, DateTimeOffset? nextRunAt, CancellationToken ct = default) => Task.CompletedTask;
    public Task<long> StartRunAsync(MonitorRun run, CancellationToken ct = default) => Task.FromResult(1L);
    public Task CompleteRunAsync(long runId, AnalysisReport report, DateTimeOffset completedAt, CancellationToken ct = default) => Task.CompletedTask;
    public Task FailRunAsync(long runId, string error, DateTimeOffset completedAt, CancellationToken ct = default) => Task.CompletedTask;
    public Task AnnotateRunAsync(long runId, string note, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<MonitorRun>> ListRunsAsync(long monitorId, int take, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MonitorRun>>([]);
    public Task<MonitorRun?> GetRunAsync(long runId, CancellationToken ct = default) => Task.FromResult<MonitorRun?>(null);
}

internal sealed class NoSavedQueries : ISavedQueryStore
{
    public Task<long> SaveAsync(SavedQuery query, CancellationToken ct = default) => Task.FromResult(1L);
    public Task<SavedQuery?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult<SavedQuery?>(null);
    public Task<IReadOnlyList<SavedQuery>> ListAsync(string? sourceName, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SavedQuery>>([]);
    public Task DeleteAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>LLM finto con risposte in sequenza: verifica che l'orchestratore usi gli strumenti e il loro esito.</summary>
internal sealed class ScriptedChatModel(bool supportsTools, params ChatResponse[] responses) : IChatModel
{
    private int _next;
    public List<ChatRequest> Requests { get; } = [];
    public ChatModelInfo Info => new("fake", "scripted", supportsTools);
    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(responses[Math.Min(_next++, responses.Length - 1)]);
    }
}
