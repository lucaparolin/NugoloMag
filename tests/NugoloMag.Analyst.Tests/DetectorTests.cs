using NugoloMag.Analyst.Application;
using NugoloMag.Analyst.Application.Detection;
using NugoloMag.Analyst.Application.RootCause;
using NugoloMag.Analyst.Domain;
using static NugoloMag.Analyst.Tests.Builders;

namespace NugoloMag.Analyst.Tests;

public class DetectorTests
{
    private readonly DetectionSettings _settings = new();

    [Fact]
    public void Stable_data_produces_no_findings()
    {
        var data = new InventoryDataset(Item("MI01", "A", "Casa", Steady(50)).Concat(Item("MI01", "B", "Bevande", Steady(30))));

        var detectors = new Application.Abstractions.IChangeDetector[]
        {
            new PointAnomalyDetector(_settings), new TrendReversalDetector(_settings), new ItemLifecycleDetector(_settings),
            new MixDriftDetector(_settings), new DataFreshnessDetector(_settings), new StockIntegrityDetector(_settings)
        };

        Assert.Empty(detectors.SelectMany(d => d.Detect(data, Window)));
    }

    [Fact]
    public void Spike_on_as_of_day_is_detected_and_explained_by_its_category()
    {
        var rows = Item("MI01", "A", "Casa", Steady(50))
            .Concat(Item("MI01", "B", "Elettronica", d => d == AsOf ? 200 : Steady(30)(d)))
            .ToList();
        var data = new InventoryDataset(rows);

        var spike = new PointAnomalyDetector(_settings).Detect(data, Window)
            .Single(f => f.Subject.IsWarehouseLevel && f.Metric == Metric.Outbound);
        var causes = new ContributionAnalyzer().Explain(spike, data, Window);

        Assert.Equal(FindingKind.Spike, spike.Kind);
        Assert.Equal("Elettronica", causes.First(c => c.Dimension == "Categoria").Member);
        Assert.True(causes.First().Share > 0.9);
    }

    [Fact]
    public void Weekly_seasonality_is_not_reported_as_anomaly()
    {
        // Domenica sempre a zero: non deve essere un "crollo".
        var sunday = new DateOnly(2026, 9, 20);
        var window = new AnalysisWindow(sunday);
        var rows = Item("MI01", "A", "Casa", d => d.DayOfWeek == DayOfWeek.Sunday ? 0 : 60);
        var data = new InventoryDataset(rows.Where(r => r.Date <= sunday));

        Assert.Empty(new PointAnomalyDetector(_settings).Detect(data, window));
    }

    [Fact]
    public void Regular_mover_without_outbound_is_stockout_when_stock_is_zero_and_stalled_otherwise()
    {
        var stockout = Item("RM02", "OUT", "Casa", d => d > AsOf.AddDays(-3) ? 0 : 40, startStock: 40 * 37);
        var stalled = Item("RM02", "STALL", "Casa", d => d > AsOf.AddDays(-3) ? 0 : 40);
        var data = new InventoryDataset(stockout.Concat(stalled));

        var findings = new ItemLifecycleDetector(_settings).Detect(data, Window).ToList();

        Assert.Contains(findings, f => f.Kind == FindingKind.Stockout && f.Subject.Sku == new Sku("OUT"));
        Assert.Contains(findings, f => f.Kind == FindingKind.StalledItem && f.Subject.Sku == new Sku("STALL"));
    }

    [Fact]
    public void Category_mix_shift_is_reported_with_psi_breakdown()
    {
        var rows = Item("NA03", "A", "Bevande", d => Window.IsRecent(d) ? 150 : 30)
            .Concat(Item("NA03", "B", "Casa", Steady(70)));
        var finding = new MixDriftDetector(_settings).Detect(new InventoryDataset(rows), Window).Single();

        Assert.Equal(FindingKind.MixDrift, finding.Kind);
        Assert.Equal("Bevande", finding.RootCauses[0].Member);
    }

    [Fact]
    public void Stock_that_does_not_reconcile_with_movements_is_an_integrity_finding()
    {
        var rows = Item("NA03", "A", "Casa", Steady(20));
        var i = rows.FindIndex(r => r.Date == AsOf.AddDays(-2));
        rows[i] = rows[i] with { OnHand = rows[i].OnHand - 500 };

        var finding = new StockIntegrityDetector(_settings).Detect(new InventoryDataset(rows), Window).Single();

        Assert.Equal(FindingKind.Integrity, finding.Kind);
        Assert.StartsWith("2 squadrature", finding.Headline); // il giorno alterato e quello successivo
    }

    [Fact]
    public void Missing_latest_days_are_reported_and_suppress_misleading_comparisons()
    {
        var rows = Item("RM02", "A", "Casa", Steady(50)).Where(r => r.Date <= AsOf.AddDays(-2));
        var data = new InventoryDataset(rows);

        var freshness = new DataFreshnessDetector(_settings).Detect(data, Window).Single();

        Assert.Contains("2 giorni mancanti", freshness.Headline);
        Assert.Empty(new ItemLifecycleDetector(_settings).Detect(data, Window));
        Assert.Empty(new PointAnomalyDetector(_settings).Detect(data, Window));
    }

    [Fact]
    public void Trend_reversal_is_detected_when_growth_turns_into_decline()
    {
        var rows = Item("MI01", "A", "Casa", d => Window.IsRecent(d)
            ? 200 - 25 * (d.DayNumber - Window.RecentStart.DayNumber)
            : 50 + 3 * (d.DayNumber - AsOf.AddDays(-40).DayNumber));

        var finding = new TrendReversalDetector(_settings).Detect(new InventoryDataset(rows), Window)
            .Single(f => f.Metric == Metric.Outbound);

        Assert.Equal(FindingKind.TrendReversal, finding.Kind);
    }
}
