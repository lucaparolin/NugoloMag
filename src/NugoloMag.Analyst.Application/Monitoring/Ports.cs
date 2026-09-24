using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Domain.Monitoring;

namespace NugoloMag.Analyst.Application.Monitoring;

public interface IMonitorStore
{
    Task<long> CreateAsync(MonitorDefinition monitor, CancellationToken ct = default);
    Task<MonitorDefinition?> GetAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<MonitorDefinition>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MonitorDefinition>> ListDueAsync(DateTimeOffset now, CancellationToken ct = default);
    Task SetScheduleAsync(long id, bool isActive, DateTimeOffset? nextRunAt, CancellationToken ct = default);

    Task<long> StartRunAsync(MonitorRun run, CancellationToken ct = default);
    Task CompleteRunAsync(long runId, AnalysisReport report, DateTimeOffset completedAt, CancellationToken ct = default);
    Task FailRunAsync(long runId, string error, DateTimeOffset completedAt, CancellationToken ct = default);
    Task<IReadOnlyList<MonitorRun>> ListRunsAsync(long monitorId, int take, CancellationToken ct = default);
    Task<MonitorRun?> GetRunAsync(long runId, CancellationToken ct = default);
}
