using NugoloMag.Analyst.Domain.Discovery;

namespace NugoloMag.Analyst.Domain.Monitoring;

/// <summary>Un monitoraggio attivo: quale sorgente, con quale query, quando e su quali finestre.</summary>
public sealed record MonitorDefinition
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public required string SourceName { get; init; }
    public required long DiscoveryId { get; init; }
    public required SourceMapping Mapping { get; init; }
    public required string SourceQuery { get; init; }
    public required TimeOnly RunAt { get; init; }
    public required int BaselineDays { get; init; }
    public required int RecentDays { get; init; }
    public required bool IsActive { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? NextRunAt { get; init; }

    public bool IsDue(DateTimeOffset now) => IsActive && NextRunAt is { } next && next <= now;

    /// <summary>Prossima esecuzione all'orario <see cref="RunAt"/> nel fuso indicato, strettamente dopo <paramref name="now"/>.</summary>
    public DateTimeOffset NextOccurrenceAfter(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = local.Date + RunAt.ToTimeSpan();
        if (candidate <= local.DateTime) candidate = candidate.AddDays(1);
        return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
    }
}

public enum RunStatus { Running, Succeeded, Failed }

public sealed record MonitorRun
{
    public long Id { get; init; }
    public required long MonitorId { get; init; }
    public required DateOnly AsOf { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required RunStatus Status { get; init; }
    public int RowsAnalyzed { get; init; }
    public int FindingsCount { get; init; }
    public int HighCount { get; init; }
    public string? Narrative { get; init; }
    public string? Error { get; init; }
}
