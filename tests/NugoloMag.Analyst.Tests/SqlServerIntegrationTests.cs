using Microsoft.Data.SqlClient;
using NugoloMag.Analyst.Application;
using NugoloMag.Analyst.Application.Detection;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Application.Monitoring;
using NugoloMag.Analyst.Application.Narration;
using NugoloMag.Analyst.Application.RootCause;
using NugoloMag.Analyst.Domain.Discovery;
using NugoloMag.Analyst.Domain.Monitoring;
using NugoloMag.Analyst.Infrastructure.Demo;
using NugoloMag.Analyst.Infrastructure.SqlServer;
using NugoloMag.Analyst.Infrastructure.Store;

namespace NugoloMag.Analyst.Tests;

/// <summary>Eseguito solo se NUGOLO_TEST_SQLSERVER contiene una stringa di connessione (senza database) a un SQL Server di prova.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public static readonly string? Connection = Environment.GetEnvironmentVariable("NUGOLO_TEST_SQLSERVER");

    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Connection)) Skip = "NUGOLO_TEST_SQLSERVER non impostata";
    }
}

public class SqlServerIntegrationTests
{
    private static string Db(string name) =>
        new SqlConnectionStringBuilder(SqlServerFactAttribute.Connection) { InitialCatalog = name }.ConnectionString;

    [SqlServerFact]
    public async Task Agent_analyzes_database_then_monitoring_runs_and_stores_the_report()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        await new DemoErpSeeder(SqlServerFactAttribute.Connection!, "NugoloTestErp").SeedAsync(today.AddDays(-1));

        // 1. L'agente analizza il DB
        var registry = new SqlServerSourceRegistry(new Dictionary<string, string> { ["erp"] = Db("NugoloTestErp") });
        var agent = new DatabaseDiscoveryAgent(new SourceValidator(), new NoSchemaAdvisor(), TimeProvider.System);
        var discovery = await agent.AnalyzeAsync(registry.Get("erp"));

        Assert.Equal(Readiness.Ready, discovery.Readiness);
        Assert.Equal("MovMag", discovery.Mapping!.Movements.Table.Name);
        Assert.Equal(MovementDirection.TypeCode, discovery.Mapping.Movements.Direction);
        Assert.Null(discovery.Mapping.Snapshot); // SaldiMagazzino è solo il saldo corrente

        // 2. Salvataggio e rilettura (JSON source-generated)
        var connections = new SqlConnectionFactory(Db("NugoloTestStore"));
        await new StoreSchemaInstaller(connections).InstallAsync();
        var discoveries = new SqlDiscoveryStore(connections);
        var id = await discoveries.SaveAsync(discovery);
        var reloaded = await discoveries.GetAsync(id);
        Assert.Equal(discovery.SourceQuery, reloaded!.SourceQuery);
        Assert.Equal(discovery.Mapping.Movements.OutboundCodes, reloaded.Mapping!.Movements.OutboundCodes);
        Assert.Equal(InventoryQueryBuilder.Build(discovery.Mapping), InventoryQueryBuilder.Build(reloaded.Mapping));

        // 3. Attivazione ed esecuzione del monitoraggio
        var monitors = new SqlMonitorStore(connections);
        var settings = new DetectionSettings();
        var analyst = new AnalystService(
            [new DataFreshnessDetector(settings), new StockIntegrityDetector(settings), new PointAnomalyDetector(settings),
             new TrendReversalDetector(settings), new ItemLifecycleDetector(settings), new MixDriftDetector(settings)],
            new ContributionAnalyzer(), new FindingRanker(settings), new FindingConsolidator(), new TemplateInsightNarrator(), TimeProvider.System);
        var service = new MonitoringService(discoveries, monitors, registry, analyst, TimeProvider.System, TimeZoneInfo.Local);

        var monitorId = await service.ActivateAsync(new ActivationRequest(id, "Test", new TimeOnly(6, 0), 28, 7));
        Assert.Equal(1, await service.RunDueAsync());

        var run = (await monitors.ListRunsAsync(monitorId, 1)).Single();
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.True(run.FindingsCount > 0);
        var report = await monitors.GetReportAsync(run.Id);
        Assert.Contains(report!.Findings, f => f.Kind == "spike" && f.Subject == "MI01");

        // Ripianificato per domani: non riparte subito.
        Assert.Equal(0, await service.RunDueAsync());
    }
}
