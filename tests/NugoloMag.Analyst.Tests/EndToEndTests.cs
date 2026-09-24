using Microsoft.Data.Sqlite;
using NugoloMag.Analyst.Application;
using NugoloMag.Analyst.Application.Detection;
using NugoloMag.Analyst.Application.Narration;
using NugoloMag.Analyst.Application.RootCause;
using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Infrastructure.Data;
using NugoloMag.Analyst.Infrastructure.Demo;
using NugoloMag.Analyst.Infrastructure.Reports;
using static NugoloMag.Analyst.Tests.Builders;

namespace NugoloMag.Analyst.Tests;

public class EndToEndTests
{
    private static AnalystService CreateService()
    {
        var s = new DetectionSettings();
        return new AnalystService(
            [new DataFreshnessDetector(s), new StockIntegrityDetector(s), new PointAnomalyDetector(s),
             new TrendReversalDetector(s), new ItemLifecycleDetector(s), new MixDriftDetector(s)],
            new ContributionAnalyzer(), new FindingRanker(s), new FindingConsolidator(), new TemplateInsightNarrator(), TimeProvider.System);
    }

    [Fact]
    public async Task All_injected_demo_anomalies_are_found()
    {
        var rows = new SyntheticInventoryGenerator().Generate(AsOf, days: 42);
        var report = await CreateService().AnalyzeAsync(new InMemoryInventoryRepository(rows), Window);

        bool Has(FindingKind kind, string subjectPrefix) =>
            report.Findings.Any(f => f.Kind == kind && f.Subject.ToString().StartsWith(subjectPrefix));

        Assert.True(Has(FindingKind.Spike, "MI01"));
        Assert.Equal("Elettronica", report.Findings.First(f => f.Kind == FindingKind.Spike && f.Subject.ToString() == "MI01").RootCauses[0].Member);
        Assert.True(Has(FindingKind.Stockout, "RM02/"));
        Assert.True(Has(FindingKind.StalledItem, "RM02/"));
        Assert.True(Has(FindingKind.NewItem, "NA03"));
        Assert.True(Has(FindingKind.MixDrift, "NA03"));
        Assert.True(Has(FindingKind.Integrity, "NA03"));
        Assert.True(Has(FindingKind.Drop, "MI01")); // rettifica inventariale anomala
        Assert.False(string.IsNullOrWhiteSpace(report.Narrative));

        Assert.Contains("<svg", new HtmlReportWriter().Render(report));
    }

    [Fact]
    public async Task Ado_net_repository_reads_through_any_provider()
    {
        await using var connection = new SqliteConnection("Data Source=nugolo-test;Mode=Memory;Cache=Shared");
        await connection.OpenAsync(); // tiene vivo il database in memoria condiviso

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE StockDaily (StockDate TEXT, WarehouseCode TEXT, Sku TEXT, Category TEXT,
                  OnHand NUMERIC, Inbound NUMERIC, Outbound NUMERIC, Adjustment NUMERIC, UnitCost NUMERIC);
                INSERT INTO StockDaily VALUES
                  ('2026-09-21','mi01','a','Casa',100,0,5,0,2.5),
                  ('2026-09-22','MI01','A','Casa',95,0,5,0,2.5),
                  ('2026-09-23','MI01','A',NULL,90,0,5,0,NULL),
                  ('2026-09-24','MI01','A','Casa',85,0,5,0,2.5);
                """;
            await create.ExecuteNonQueryAsync();
        }

        // Query personalizzata: è anche il modo di mappare lo schema del proprio gestionale.
        const string query = """
            SELECT StockDate, WarehouseCode, Sku, Category, OnHand, Inbound, Outbound, Adjustment, UnitCost
            FROM StockDaily WHERE date(StockDate) BETWEEN date(@from) AND date(@to) ORDER BY StockDate
            """;
        var repo = new DbInventoryRepository(SqliteFactory.Instance, "Data Source=nugolo-test;Mode=Memory;Cache=Shared", query);
        var rows = await repo.LoadAsync(new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 23));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(new WarehouseCode("MI01"), r.Warehouse));
        Assert.Equal("N/D", rows[1].Category);
        Assert.Equal(95m, rows[0].OnHand);
    }

    [Fact]
    public async Task Csv_round_trip_preserves_rows()
    {
        var rows = Item("MI01", "A", "Casa", Steady(10), days: 5);
        var path = Path.GetTempFileName();
        try
        {
            await CsvInventoryRepository.WriteAsync(path, rows);
            var loaded = await new CsvInventoryRepository(path).LoadAsync(AsOf.AddDays(-10), AsOf);
            Assert.Equal(rows, loaded);
        }
        finally { File.Delete(path); }
    }
}
