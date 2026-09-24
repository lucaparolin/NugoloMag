using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Tests;

public class StatisticsTests
{
    [Fact]
    public void Median_and_mad_are_robust_to_outliers()
    {
        double[] xs = [10, 11, 9, 10, 10, 1000];
        Assert.Equal(10, TimeSeries.Median(xs));
        Assert.InRange(TimeSeries.ScaledMad(xs), 0.5, 1.5);
    }

    [Fact]
    public void Slope_of_a_line_is_its_coefficient()
    {
        Assert.Equal(2, TimeSeries.Slope([1, 3, 5, 7, 9]), 6);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(30, 1, 99.3)]
    public void Magnitude_combines_surprise_and_impact(double surprise, double impact, double expected)
    {
        Assert.Equal(expected, MagnitudeScore.From(surprise, impact), 1);
    }

    [Fact]
    public void Analysis_window_splits_baseline_and_recent_without_overlap()
    {
        var w = new AnalysisWindow(new DateOnly(2026, 9, 23), 28, 7);
        Assert.Equal(new DateOnly(2026, 9, 17), w.RecentStart);
        Assert.Equal(new DateOnly(2026, 9, 16), w.BaselineEnd);
        Assert.Equal(28, w.BaselineDates().Count());
    }
}
