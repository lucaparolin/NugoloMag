using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Application.Detection;

/// <summary>
/// "Il normale" di una serie, consapevole del giorno della settimana: il lunedì si confronta con i lunedì.
/// Il rumore è misurato sui residui rispetto a questa attesa, con statistiche robuste (mediana/MAD).
/// </summary>
public sealed class SeasonalBaseline
{
    private readonly Dictionary<DayOfWeek, double> _expectedByDay;

    public double Noise { get; }
    public double Mean { get; }

    public SeasonalBaseline(TimeSeries series, AnalysisWindow window)
    {
        var dates = window.BaselineDates().ToArray();
        var values = series.Slice(dates);
        Mean = TimeSeries.Mean(values);

        var overallMedian = TimeSeries.Median(values);
        _expectedByDay = dates
            .Select((d, i) => (d.DayOfWeek, v: values[i]))
            .GroupBy(x => x.DayOfWeek)
            .ToDictionary(g => g.Key, g => g.Count() >= 3 ? TimeSeries.Median(g.Select(x => x.v).ToArray()) : overallMedian);

        var residuals = dates.Select((d, i) => values[i] - Expected(d)).ToArray();
        var mad = TimeSeries.ScaledMad(residuals);
        // Con serie molto regolari la MAD può essere 0: si ripiega su deviazione standard e su un minimo relativo.
        Noise = new[] { mad, TimeSeries.StdDev(residuals), Math.Abs(Mean) * 0.05, 1d }.Max();
    }

    public double Expected(DateOnly date) => _expectedByDay.TryGetValue(date.DayOfWeek, out var v) ? v : Mean;

    public double ZScore(double observed, DateOnly date) => (observed - Expected(date)) / Noise;

    /// <summary>Media mobile a 7 giorni: neutralizza la stagionalità settimanale nel calcolo dei trend.</summary>
    public static double[] Rolling7(TimeSeries series, IEnumerable<DateOnly> dates) =>
        dates.Select(d => Enumerable.Range(0, 7).Average(k => series[d.AddDays(-k)])).ToArray();
}
