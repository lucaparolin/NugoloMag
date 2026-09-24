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
        var connections = new SqlConnectionFactory(Db("NugoloTestStoreMonitor"));
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
        Assert.True(await service.RunDueAsync() >= 1);

        var run = (await monitors.ListRunsAsync(monitorId, 1)).Single();
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.True(run.FindingsCount > 0);
        var report = await monitors.GetReportAsync(run.Id);
        Assert.Contains(report!.Findings, f => f.Kind == "spike" && f.Subject == "MI01");

        // Ripianificato per domani: non riparte subito.
        Assert.DoesNotContain(await monitors.ListDueAsync(DateTimeOffset.UtcNow), m => m.Id == monitorId);
    }
}

public class AssistantToolsIntegrationTests
{
    private static string Db(string name) =>
        new SqlConnectionStringBuilder(SqlServerFactAttribute.Connection) { InitialCatalog = name }.ConnectionString;

    [SqlServerFact]
    public async Task Tools_explore_query_and_save_on_a_real_database_without_ever_writing_to_it()
    {
        await new DemoErpSeeder(SqlServerFactAttribute.Connection!, "NugoloTestTools").SeedAsync(DateOnly.FromDateTime(DateTime.Today));
        var source = new SqlServerSourceDatabase("erp", Db("NugoloTestTools"));
        var connections = new SqlConnectionFactory(Db("NugoloTestStoreTools"));
        await new StoreSchemaInstaller(connections).InstallAsync();
        var saved = new SqlSavedQueryStore(connections);
        var tools = new NugoloMag.Analyst.Application.Assistant.DataAgentToolbox(source, await source.ReadCatalogAsync(), saved, 0, TimeProvider.System);

        static System.Text.Json.JsonElement Json(string s) => System.Text.Json.JsonDocument.Parse(s).RootElement.Clone();

        var describe = await tools.ExecuteAsync("describe_table", Json("""{"table":"dbo.MovMag"}"""));
        Assert.Contains("Causale", describe.Content);
        Assert.Contains("FK CodArt → dbo.Articoli.CodArt", describe.Content);

        var query = await tools.ExecuteAsync("run_query", Json("""{"sql":"SELECT Causale, COUNT(*) AS N FROM dbo.MovMag GROUP BY Causale ORDER BY N DESC","purpose":"causali"}"""));
        Assert.False(query.IsError, query.Content);
        Assert.StartsWith("Causale\tN", query.Content);
        Assert.Contains("VEN", query.Content);

        var write = await tools.ExecuteAsync("run_query", Json("""{"sql":"DELETE FROM dbo.MovMag","purpose":"x"}"""));
        Assert.True(write.IsError);

        var save = await tools.ExecuteAsync("save_query", Json("""{"name":"Movimenti per causale","description":"Conteggio","sql":"SELECT Causale, COUNT(*) AS N FROM dbo.MovMag GROUP BY Causale"}"""));
        Assert.False(save.IsError, save.Content);
        Assert.Contains(await saved.ListAsync("erp"), q => q.Name == "Movimenti per causale");

        // Seconda barriera: anche aggirando il filtro, l'esecutore annulla sempre la transazione.
        var before = (await source.QueryAsync("SELECT COUNT(*) FROM dbo.MovMag", 1)).Rows[0][0];
        await source.QueryAsync("DELETE FROM dbo.MovMag; SELECT 1", 1);
        var after = (await source.QueryAsync("SELECT COUNT(*) FROM dbo.MovMag", 1)).Rows[0][0];
        Assert.Equal(before, after);
    }
}

public class AgenticStoresIntegrationTests
{
    private static string Db(string name) =>
        new SqlConnectionStringBuilder(SqlServerFactAttribute.Connection) { InitialCatalog = name }.ConnectionString;

    [SqlServerFact]
    public async Task Incidents_briefings_and_feedback_round_trip_through_sql_server()
    {
        var connections = new SqlConnectionFactory(Db("NugoloTestStoreAgentic"));
        await new StoreSchemaInstaller(connections).InstallAsync();
        var incidents = new SqlIncidentStore(connections);
        var briefings = new SqlBriefingStore(connections);

        var finding = new NugoloMag.Analyst.Domain.Agentic.AgentFinding
        {
            Agent = "productivity", Domain = NugoloMag.Analyst.Domain.Agentic.OperationalDomain.Productivity,
            Signature = $"productivity:below-expectation:T{Guid.NewGuid():N}"[..60], Warehouse = "MI01", Title = "Produttività -33%",
            Observation = "oss", WhyItMatters = "perché", Baseline = "b", Deviation = "d", Scope = "s",
            Evidence = [new(NugoloMag.Analyst.Domain.Agentic.ClaimKind.Fact, "19 articoli spostati", "productivity", new Dictionary<string, string> { ["zona"] = "C" })],
            Confidence = NugoloMag.Analyst.Domain.Agentic.ConfidenceLevel.High,
            Severity = new(1, 0.6, 0.4, 0.7, NugoloMag.Analyst.Domain.Agentic.ConfidenceLevel.High), Intensity = 0.33,
            Measures = new Dictionary<string, double> { ["efficiency_gap"] = -0.33 }
        };
        var change = NugoloMag.Analyst.Application.Agentic.IncidentManager.Reconcile("erp", [], [finding], ["MI01"], DateTimeOffset.UtcNow).Single();
        var id = await incidents.SaveAsync(change.Incident with { ProbableRootCause = "spostamento articoli" });

        var loaded = (await incidents.ListOpenAsync("erp")).Single(i => i.Id == id);
        Assert.Equal("spostamento articoli", loaded.ProbableRootCause);
        Assert.Equal(-0.33, loaded.Signals[0].Measures["efficiency_gap"]);
        Assert.Equal("C", loaded.Evidence[0].Data["zona"]);

        await incidents.AddFeedbackAsync(new(id, DateTimeOffset.UtcNow, NugoloMag.Analyst.Domain.Agentic.FeedbackVerdict.Irrelevant, "promo", "test"));
        Assert.Equal(1, await incidents.DismissalsAsync("erp", finding.Signature));

        var briefingId = await briefings.SaveAsync(new NugoloMag.Analyst.Domain.Agentic.ExecutiveBriefing
        {
            SourceName = "erp", AsOf = new DateOnly(2026, 9, 23), CreatedAt = DateTimeOffset.UtcNow,
            ExecutiveSummary = ["Nuovo · MI01: produttività -33%"], CriticalRisks = [], PositiveSignals = [], RootCauses = [], RecommendedActions = [], WatchList = []
        });
        Assert.Equal("Nuovo · MI01: produttività -33%", (await briefings.GetAsync(briefingId))!.ExecutiveSummary[0]);
    }
}
