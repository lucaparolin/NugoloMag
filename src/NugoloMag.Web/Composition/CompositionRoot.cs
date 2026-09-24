using Microsoft.AspNetCore.Mvc;
using NugoloMag.Analyst.Application;
using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Application.Agentic;
using NugoloMag.Analyst.Application.Assistant;
using NugoloMag.Analyst.Application.Detection;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Application.Monitoring;
using NugoloMag.Analyst.Application.Narration;
using NugoloMag.Analyst.Application.RootCause;
using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Infrastructure.Llm;
using NugoloMag.Analyst.Infrastructure.SqlServer;
using NugoloMag.Analyst.Infrastructure.Store;
using NugoloMag.Web.Controllers;

namespace NugoloMag.Web.Composition;

/// <summary>
/// Unico punto in cui gli oggetti vengono creati. Ogni registrazione è una factory esplicita (new ...):
/// nessuna scansione di assembly, nessuna attivazione via reflection dei nostri tipi, controller compresi.
/// </summary>
public static class CompositionRoot
{
    /// <summary>Parametri economici degli agenti (sezione "Agentic"), letti in modo esplicito.</summary>
    private static AgenticSettings ReadAgenticSettings(IConfiguration configuration)
    {
        var defaults = new AgenticSettings();
        double D(string key, double fallback) =>
            double.TryParse(configuration[$"Agentic:{key}"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
        return defaults with
        {
            LaborCostPerHour = D("LaborCostPerHour", defaults.LaborCostPerHour),
            MaterialAmount = D("MaterialAmount", defaults.MaterialAmount),
            StockoutCoverDays = D("StockoutCoverDays", defaults.StockoutCoverDays),
            OverstockCoverDays = D("OverstockCoverDays", defaults.OverstockCoverDays),
            ImpactHorizonDays = (int)D("ImpactHorizonDays", defaults.ImpactHorizonDays)
        };
    }

    /// <summary>
    /// Cartella delle skill: Skills:Path (relativo alla content root) oppure la copia accanto all'eseguibile.
    /// In sviluppo appsettings.Development.json punta alla cartella agents/ del repository: le modifiche valgono subito.
    /// </summary>
    private static string SkillsPath(IConfiguration configuration)
    {
        if (configuration["Skills:Path"] is not { Length: > 0 } path) return Path.Combine(AppContext.BaseDirectory, "agents");
        var contentRoot = configuration["contentRoot"] is { Length: > 0 } root ? root : Directory.GetCurrentDirectory();
        return Path.GetFullPath(path, contentRoot);
    }

    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        var storeConnection = configuration.GetConnectionString("NugoloStore")
                              ?? throw new InvalidOperationException("ConnectionStrings:NugoloStore mancante.");
        var sources = configuration.GetSection("Sources").GetChildren()
            .Where(s => !string.IsNullOrWhiteSpace(s.Value))
            .ToDictionary(s => s.Key, s => s.Value!, StringComparer.OrdinalIgnoreCase);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(configuration["Monitoring:TimeZone"] ?? "Europe/Rome");
        var poll = TimeSpan.FromSeconds(int.TryParse(configuration["Monitoring:PollSeconds"], out var p) ? p : 60);

        // Modelli linguistici: connettori con nome (Llm:Connectors), skill degli agenti su file (agents/), instradamento per agente.
        var llmSettings = LlmConfiguration.Read(configuration);
        var connectors = new LlmConnectorRegistry(llmSettings);
        var skills = new FileSkillLibrary(SkillsPath(configuration));
        var agentLlm = new AgentLlm(skills, connectors, llmSettings.Agents);
        var problems = LlmConfiguration.Validate(llmSettings).Concat(skills.Validate([.. SkillIds.All, SkillIds.ConnectorCheck])).Concat(agentLlm.Validate(SkillIds.All)).Distinct().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException("Configurazione LLM o skill non valida:\n- " + string.Join("\n- ", problems));
        bool Uses(string agent) => agentLlm.IsEnabled(agent);
        var llmEnabled = SkillIds.All.Any(Uses);

        // Infrastruttura
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new SqlConnectionFactory(storeConnection));
        services.AddSingleton(sp => new StoreSchemaInstaller(sp.GetRequiredService<SqlConnectionFactory>()));
        services.AddSingleton<ISourceRegistry>(new SqlServerSourceRegistry(sources));
        services.AddSingleton<IDiscoveryStore>(sp => new SqlDiscoveryStore(sp.GetRequiredService<SqlConnectionFactory>()));
        services.AddSingleton(sp => new SqlMonitorStore(sp.GetRequiredService<SqlConnectionFactory>()));
        services.AddSingleton<IMonitorStore>(sp => sp.GetRequiredService<SqlMonitorStore>());
        services.AddSingleton<IRunReportQuery>(sp => sp.GetRequiredService<SqlMonitorStore>());
        services.AddSingleton<IConversationStore>(sp => new SqlConversationStore(sp.GetRequiredService<SqlConnectionFactory>()));
        services.AddSingleton<ISavedQueryStore>(sp => new SqlSavedQueryStore(sp.GetRequiredService<SqlConnectionFactory>()));
        services.AddSingleton<IIncidentStore>(sp => new SqlIncidentStore(sp.GetRequiredService<SqlConnectionFactory>()));
        services.AddSingleton<IBriefingStore>(sp => new SqlBriefingStore(sp.GetRequiredService<SqlConnectionFactory>()));
        services.AddSingleton<IInvestigationStore>(sp => new SqlInvestigationStore(sp.GetRequiredService<SqlConnectionFactory>()));

        services.AddSingleton(connectors);
        services.AddSingleton<ISkillLibrary>(skills);
        services.AddSingleton(agentLlm);
        services.AddSingleton(new LlmDiagnostics(skills));
        services.AddSingleton<IInsightNarrator>(Uses(SkillIds.ReportNarrator)
            ? new LlmInsightNarrator(agentLlm, new TemplateInsightNarrator()) : new TemplateInsightNarrator());
        services.AddSingleton<ISchemaAdvisor>(Uses(SkillIds.SchemaAdvisor) ? new LlmSchemaAdvisor(agentLlm) : new NoSchemaAdvisor());
        services.AddSingleton<IDataAgent>(Uses(SkillIds.DataAssistant) ? new LlmDataAgent(agentLlm) : new UnavailableDataAgent());

        // Applicazione
        services.AddSingleton(sp =>
        {
            var settings = new DetectionSettings();
            return new AnalystService(
                [
                    new DataFreshnessDetector(settings),
                    new StockIntegrityDetector(settings),
                    new PointAnomalyDetector(settings),
                    new TrendReversalDetector(settings),
                    new ItemLifecycleDetector(settings),
                    new MixDriftDetector(settings)
                ],
                new ContributionAnalyzer(),
                new FindingRanker(settings),
                new FindingConsolidator(),
                sp.GetRequiredService<IInsightNarrator>(),
                sp.GetRequiredService<TimeProvider>());
        });
        services.AddSingleton(sp => new DatabaseDiscoveryAgent(
            new SourceValidator(), sp.GetRequiredService<ISchemaAdvisor>(), sp.GetRequiredService<TimeProvider>()));
        // Sistema agentico (blueprint MVP): specialisti, indagine, impatto, raccomandazioni, briefing, apprendimento, orchestratore.
        var agentic = ReadAgenticSettings(configuration);
        services.AddSingleton(agentic);
        services.AddSingleton(sp =>
        {
            ISpecialistAgent[] specialists = [new InventoryAgent(new DetectionSettings(), agentic), new ProductivityAgent(agentic)];
            var learning = new LearningAgent(sp.GetRequiredService<IIncidentStore>(), agentic, sp.GetRequiredService<TimeProvider>());
            return new AgentTeam(specialists, new InvestigationAgent(specialists), new ImpactAgent(agentic), new RecommendationAgent(),
                new BriefingAgent(agentLlm), learning);
        });
        services.AddSingleton(sp => sp.GetRequiredService<AgentTeam>().Learning);
        services.AddSingleton(sp => new WarehouseOrchestrator(
            sp.GetRequiredService<AgentTeam>(),
            sp.GetRequiredService<IIncidentStore>(),
            sp.GetRequiredService<IBriefingStore>(),
            sp.GetRequiredService<IInvestigationStore>(),
            sp.GetRequiredService<ISourceRegistry>(),
            sp.GetRequiredService<IMonitorStore>(),
            sp.GetRequiredService<ISavedQueryStore>(),
            agentic,
            agentLlm,
            sp.GetRequiredService<TimeProvider>(),
            zone));

        services.AddSingleton(sp => new MonitoringService(
            sp.GetRequiredService<IDiscoveryStore>(),
            sp.GetRequiredService<IMonitorStore>(),
            sp.GetRequiredService<ISourceRegistry>(),
            sp.GetRequiredService<AnalystService>(),
            sp.GetRequiredService<TimeProvider>(),
            zone,
            sp.GetRequiredService<WarehouseOrchestrator>()));
        services.AddSingleton(sp => new ConversationService(
            sp.GetRequiredService<IConversationStore>(),
            sp.GetRequiredService<ISavedQueryStore>(),
            sp.GetRequiredService<IDiscoveryStore>(),
            sp.GetRequiredService<ISourceRegistry>(),
            sp.GetRequiredService<IDataAgent>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService(sp => new MonitoringWorker(
            sp.GetRequiredService<MonitoringService>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("NugoloMag.Monitoring"),
            poll));

        // Controller MVC: registrati prima di AddControllersAsServices, che quindi non li ricrea via reflection.
        services.AddTransient(sp => new HomeController(
            sp.GetRequiredService<IMonitorStore>(), sp.GetRequiredService<IDiscoveryStore>(), sp.GetRequiredService<ISourceRegistry>(), llmEnabled));
        services.AddTransient(sp => new DiscoveryController(
            sp.GetRequiredService<ISourceRegistry>(), sp.GetRequiredService<IDiscoveryStore>(),
            sp.GetRequiredService<DatabaseDiscoveryAgent>(), sp.GetRequiredService<MonitoringService>()));
        services.AddTransient(sp => new MonitorsController(
            sp.GetRequiredService<IMonitorStore>(), sp.GetRequiredService<MonitoringService>(), zone));
        services.AddTransient(sp => new RunsController(
            sp.GetRequiredService<IMonitorStore>(), sp.GetRequiredService<IRunReportQuery>()));

        services.AddTransient(sp => new AssistantController(
            sp.GetRequiredService<ConversationService>(), sp.GetRequiredService<IConversationStore>(), sp.GetRequiredService<ISourceRegistry>()));
        services.AddTransient(sp => new OperationsController(
            sp.GetRequiredService<IIncidentStore>(), sp.GetRequiredService<LearningAgent>(), sp.GetRequiredService<TimeProvider>()));
        services.AddTransient(sp => new BriefingController(
            sp.GetRequiredService<IBriefingStore>(), sp.GetRequiredService<IMonitorStore>(), sp.GetRequiredService<WarehouseOrchestrator>(),
            sp.GetRequiredService<TimeProvider>(), zone));
        services.AddTransient(sp => new InvestigationsController(
            sp.GetRequiredService<IInvestigationStore>(), sp.GetRequiredService<WarehouseOrchestrator>(), sp.GetRequiredService<ISourceRegistry>(),
            Uses(SkillIds.Orchestrator) ? agentLlm.Resolve(SkillIds.Orchestrator)!.Model.Info : null));
        services.AddTransient(sp => new ConnectorsController(
            sp.GetRequiredService<LlmConnectorRegistry>(), sp.GetRequiredService<AgentLlm>(), sp.GetRequiredService<LlmDiagnostics>()));
        services.AddTransient(sp => new QueriesController(
            sp.GetRequiredService<ISavedQueryStore>(), sp.GetRequiredService<ISourceRegistry>(), sp.GetRequiredService<TimeProvider>()));

        services
            .AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
            .AddControllersAsServices();
    }
}
