namespace NugoloMag.Analyst.Domain;

/// <summary>
/// Finestra temporale dell'analisi: un periodo "baseline" (il normale) seguito da un periodo "recent" (ciò che valutiamo).
/// Il giorno <see cref="AsOf"/> è l'ultimo giorno del periodo recente.
/// </summary>
public sealed record AnalysisWindow
{
    public DateOnly AsOf { get; }
    public int BaselineDays { get; }
    public int RecentDays { get; }

    public AnalysisWindow(DateOnly asOf, int baselineDays = 28, int recentDays = 7)
    {
        if (baselineDays < 7) throw new ArgumentOutOfRangeException(nameof(baselineDays), "At least 7 baseline days are required.");
        if (recentDays < 1) throw new ArgumentOutOfRangeException(nameof(recentDays));
        AsOf = asOf;
        BaselineDays = baselineDays;
        RecentDays = recentDays;
    }

    public DateOnly RecentStart => AsOf.AddDays(-(RecentDays - 1));
    public DateOnly BaselineEnd => RecentStart.AddDays(-1);
    public DateOnly BaselineStart => BaselineEnd.AddDays(-(BaselineDays - 1));

    /// <summary>Primo giorno da caricare: include un giorno in più per la riconciliazione delle giacenze.</summary>
    public DateOnly LoadFrom => BaselineStart.AddDays(-1);

    public bool IsBaseline(DateOnly d) => d >= BaselineStart && d <= BaselineEnd;
    public bool IsRecent(DateOnly d) => d >= RecentStart && d <= AsOf;

    public IEnumerable<DateOnly> BaselineDates() => Range(BaselineStart, BaselineEnd);
    public IEnumerable<DateOnly> RecentDates() => Range(RecentStart, AsOf);

    private static IEnumerable<DateOnly> Range(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1)) yield return d;
    }
}
