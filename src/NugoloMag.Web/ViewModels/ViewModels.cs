using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Domain.Discovery;
using NugoloMag.Analyst.Domain.Monitoring;
using NugoloMag.Analyst.Infrastructure.Reports;
using NugoloMag.Web.Forms;

namespace NugoloMag.Web.ViewModels;

public sealed record MonitorRow(MonitorDefinition Monitor, MonitorRun? LastRun);

public sealed record DashboardViewModel(
    IReadOnlyList<MonitorRow> Monitors,
    IReadOnlyList<DiscoverySummary> Discoveries,
    IReadOnlyList<string> Sources,
    bool ClaudeEnabled,
    string? Error = null);

public sealed record DiscoveryReviewViewModel(
    DiscoveryReport Report,
    MappingForm Form,
    string? Error = null,
    string? ActivationError = null)
{
    public IReadOnlyList<string> TablesFor(TableRole role, string? current)
    {
        var tables = Report.Candidates.Where(c => c.Role == role).OrderByDescending(c => c.Score).Select(c => c.Table.ToString()).ToList();
        if (current is not null && !tables.Contains(current, StringComparer.OrdinalIgnoreCase)) tables.Insert(0, current);
        return tables;
    }

    public IReadOnlyList<CatalogColumn> ColumnsOf(string? table) =>
        table is null ? [] : Report.CandidateTables.FirstOrDefault(t => string.Equals(t.Name.ToString(), table, StringComparison.OrdinalIgnoreCase))?.Columns ?? [];

    public long RowsFor(string code) => Report.MovementCodes.FirstOrDefault(c => c.Code == code)?.Rows ?? 0;
}

public sealed record MonitorDetailsViewModel(MonitorDefinition Monitor, IReadOnlyList<MonitorRun> Runs, TimeZoneInfo Zone);

public sealed record RunDetailsViewModel(MonitorDefinition Monitor, MonitorRun Run, ReportDocument? Report);
