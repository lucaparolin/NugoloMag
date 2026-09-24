using Microsoft.AspNetCore.Mvc;
using NugoloMag.Analyst.Application;
using NugoloMag.Analyst.Application.Abstractions;
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
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        var storeConnection = configuration.GetConnectionString("NugoloStore")
                              ?? throw new InvalidOperationException("ConnectionStrings:NugoloStore mancante.");
        var sources = configuration.GetSection("Sources").GetChildren()
            .Where(s => !string.IsNullOrWhiteSpace(s.Value))
            .ToDictionary(s => s.Key, s => s.Value!, StringComparer.OrdinalIgnoreCase);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(configuration["Monitoring:TimeZone"] ?? "Europe/Rome");
        var poll = TimeSpan.FromSeconds(int.TryParse(configuration["Monitoring:PollSeconds"], out var p) ? p : 60);
        var llmOptions = LlmOptionsReader.Read(key => configuration[key]);
        var llmHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(llmOptions.TimeoutSeconds) };
        var chatModel = ChatModelFactory.Create(llmOptions, llmHttp);
        var llmEnabled = chatModel is not null;

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

        if (chatModel is not null)
        {
            services.AddSingleton(chatModel);
            services.AddSingleton<IInsightNarrator>(new LlmInsightNarrator(chatModel, new TemplateInsightNarrator()));
            services.AddSingleton<ISchemaAdvisor>(new LlmSchemaAdvisor(chatModel));
            services.AddSingleton<IDataAgent>(new LlmDataAgent(chatModel));
        }
        else
        {
            services.AddSingleton<IInsightNarrator>(new TemplateInsightNarrator());
            services.AddSingleton<ISchemaAdvisor>(new NoSchemaAdvisor());
            services.AddSingleton<IDataAgent>(new UnavailableDataAgent());
        }

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
        services.AddSingleton(sp => new MonitoringService(
            sp.GetRequiredService<IDiscoveryStore>(),
            sp.GetRequiredService<IMonitorStore>(),
            sp.GetRequiredService<ISourceRegistry>(),
            sp.GetRequiredService<AnalystService>(),
            sp.GetRequiredService<TimeProvider>(),
            zone));
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
        services.AddTransient(sp => new QueriesController(
            sp.GetRequiredService<ISavedQueryStore>(), sp.GetRequiredService<ISourceRegistry>(), sp.GetRequiredService<TimeProvider>()));

        services
            .AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
            .AddControllersAsServices();
    }
}
