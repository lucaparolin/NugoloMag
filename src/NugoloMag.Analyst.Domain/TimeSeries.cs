namespace NugoloMag.Analyst.Domain;

/// <summary>Serie storica giornaliera densa (i giorni mancanti valgono 0) con statistiche robuste.</summary>
public sealed class TimeSeries
{
    private readonly SortedDictionary<DateOnly, double> _points;

    public TimeSeries(IEnumerable<KeyValuePair<DateOnly, double>> points)
    {
        _points = new SortedDictionary<DateOnly, double>(points.ToDictionary(p => p.Key, p => p.Value));
    }

    public double this[DateOnly date] => _points.TryGetValue(date, out var v) ? v : 0d;

    public double[] Slice(IEnumerable<DateOnly> dates) => dates.Select(d => this[d]).ToArray();

    public static double Mean(IReadOnlyList<double> xs) => xs.Count == 0 ? 0 : xs.Average();

    public static double Median(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0) return 0;
        var sorted = xs.OrderBy(x => x).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2d : sorted[mid];
    }

    /// <summary>Median Absolute Deviation scalata per essere confrontabile con la deviazione standard.</summary>
    public static double ScaledMad(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0) return 0;
        var median = Median(xs);
        return 1.4826 * Median(xs.Select(x => Math.Abs(x - median)).ToArray());
    }

    public static double StdDev(IReadOnlyList<double> xs)
    {
        if (xs.Count < 2) return 0;
        var mean = Mean(xs);
        return Math.Sqrt(xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1));
    }

    /// <summary>Pendenza della retta ai minimi quadrati (unità per giorno).</summary>
    public static double Slope(IReadOnlyList<double> ys)
    {
        var n = ys.Count;
        if (n < 2) return 0;
        var xMean = (n - 1) / 2d;
        var yMean = Mean(ys);
        double num = 0, den = 0;
        for (var i = 0; i < n; i++)
        {
            num += (i - xMean) * (ys[i] - yMean);
            den += (i - xMean) * (i - xMean);
        }
        return den == 0 ? 0 : num / den;
    }
}
