using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using NugoloMag.Analyst.Application;
using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Application.Detection;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Application.Narration;
using NugoloMag.Analyst.Application.RootCause;
using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Infrastructure.Llm;
using NugoloMag.Analyst.Infrastructure.Data;
using NugoloMag.Analyst.Infrastructure.Demo;
using NugoloMag.Analyst.Infrastructure.Reports;
using NugoloMag.Analyst.Infrastructure.SqlServer;

var options = CliOptions.Parse(args);
if (options.Command is null or "help")
{
    Console.WriteLine(CliOptions.Usage);
    return options.Command is null ? 1 : 0;
}

try
{
    if (options.Command == "seed-demo")
    {
        var master = options.Get("master") ?? throw new ArgumentException("Specificare --master \"<stringa di connessione a SQL Server>\".");
        var seedAsOf = options.Get("asof") is { } sa ? DateOnly.ParseExact(sa, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateOnly.FromDateTime(DateTime.Today);
        await new DemoErpSeeder(master).SeedAsync(seedAsOf);
        Console.WriteLine($"Database GestionaleDemo creato con movimenti fino al {seedAsOf:dd/MM/yyyy}.");
        return 0;
    }

    if (options.Command == "llm-test")
        return await LlmTestAsync(options);

    if (options.Command == "discover")
    {
        var connection = options.Get("connection") ?? throw new ArgumentException("Specificare --connection \"...\".");
        var agent = new DatabaseDiscoveryAgent(new SourceValidator(), new NoSchemaAdvisor(), TimeProvider.System);
        var discovery = await agent.AnalyzeAsync(new SqlServerSourceDatabase("cli", connection));
        foreach (var step in discovery.Steps)
        {
            Console.WriteLine($"[{step.Status}] {step.Title} ({step.Seconds:0.0}s)");
            foreach (var line in step.Details) Console.WriteLine($"    {line}");
        }
        Console.WriteLine($"Esito: {discovery.Readiness}");
        if (options.Has("show-query") && discovery.SourceQuery is { } q) Console.WriteLine(q);
        return discovery.Readiness == NugoloMag.Analyst.Domain.Discovery.Readiness.Blocked ? 1 : 0;
    }

    var asOf = options.Get("asof") is { } a ? DateOnly.ParseExact(a, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
    var window = new AnalysisWindow(asOf, options.GetInt("baseline", 28), options.GetInt("recent", 7));
    var outDir = options.Get("out") ?? "report";
    Directory.CreateDirectory(outDir);

    IInventoryRepository repository;
    if (options.Command == "demo")
    {
        var rows = new SyntheticInventoryGenerator().Generate(asOf, days: window.BaselineDays + window.RecentDays + 7);
        var csv = Path.Combine(outDir, "demo-stock.csv");
        await CsvInventoryRepository.WriteAsync(csv, rows);
        Console.WriteLine($"Dati demo: {csv} ({rows.Count:N0} righe)");
        repository = new CsvInventoryRepository(csv);
    }
    else
    {
        repository = CreateRepository(options);
    }

    var settings = new DetectionSettings();
    var narrator = CreateNarrator(options);
    var analyst = new AnalystService(
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
        narrator,
        TimeProvider.System);

    var report = await analyst.AnalyzeAsync(repository, window);

    if (options.Command == "ask")
    {
        var question = options.Positional.FirstOrDefault() ?? throw new ArgumentException("Specificare la domanda: ask \"...\"");
        Console.WriteLine(await narrator.AnswerAsync(report, question));
        return 0;
    }

    foreach (var writer in new IReportWriter[] { new HtmlReportWriter(), new MarkdownReportWriter(), new JsonReportWriter() })
    {
        var file = Path.Combine(outDir, $"analisi-{asOf:yyyyMMdd}{writer.FileExtension}");
        await File.WriteAllTextAsync(file, writer.Render(report));
        Console.WriteLine($"Report: {file}");
    }

    Console.WriteLine();
    Console.WriteLine(report.Narrative);
    return report.Findings.Any(f => f.Severity == Severity.High) ? 2 : 0; // exit code utile per job schedulati / alert
}
catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or SqlException or SkillException or LlmException)
{
    Console.Error.WriteLine($"Errore: {ex.Message}");
    return 1;
}

static IInventoryRepository CreateRepository(CliOptions options)
{
    if (options.Get("csv") is { } csv) return new CsvInventoryRepository(csv);

    if ((options.Get("connection") ?? Environment.GetEnvironmentVariable("NUGOLOMAG_CONNECTION")) is { } connection)
    {
        var query = options.Get("query-file") is { } qf ? File.ReadAllText(qf) : null;
        return new DbInventoryRepository(SqlClientFactory.Instance, connection, query);
    }

    throw new ArgumentException("Indicare una sorgente: --csv <file> oppure --connection \"<stringa SQL Server>\" (o NUGOLOMAG_CONNECTION).");
}

static IInsightNarrator CreateNarrator(CliOptions options)
{
    var template = new TemplateInsightNarrator();
    if (options.Has("no-llm")) return template;
    var llm = CreateAgentLlm(options, out _);
    return llm.IsEnabled(SkillIds.ReportNarrator) ? new LlmInsightNarrator(llm, template) : template;
}

// Connettori: --config <appsettings.json> (sezione Llm, come nella web app) oppure un connettore "cli" dai flag
// --llm ollama|openai|azure|anthropic --model <id> [--llm-url <url>] [--api-key-env VAR] [--no-tools].
// Skill: --skills <cartella> oppure la copia di agents/ accanto all'eseguibile.
static AgentLlm CreateAgentLlm(CliOptions options, out LlmConnectorRegistry registry)
{
    LlmSettings settings;
    if (options.Get("config") is { } config)
    {
        settings = LlmConfiguration.Read(new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(config), optional: false).AddEnvironmentVariables().Build());
        if (options.Get("connector") is { } only) settings.Default = only;
    }
    else
    {
        var provider = options.Get("llm") ?? (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ? "none" : "anthropic");
        var cli = new LlmOptions
        {
            Name = "cli",
            Provider = provider,
            Model = options.Get("model") ?? (provider == "ollama" ? "qwen2.5:7b" : AnthropicChatModel.DefaultModel),
            BaseUrl = options.Get("llm-url"),
            ApiKeyEnvironmentVariable = options.Get("api-key-env") ?? provider switch { "openai" => "OPENAI_API_KEY", "azure" => "AZURE_OPENAI_API_KEY", _ => null },
            SupportsTools = !options.Has("no-tools"),
            Deployment = options.Get("deployment"),
            TimeoutSeconds = options.GetInt("timeout", 600)
        };
        settings = new LlmSettings { Default = "cli" };
        settings.Connectors["cli"] = cli;
    }

    var problems = LlmConfiguration.Validate(settings);
    if (problems.Count > 0) throw new ArgumentException("Configurazione LLM non valida:\n- " + string.Join("\n- ", problems));
    registry = new LlmConnectorRegistry(settings);
    return new AgentLlm(new FileSkillLibrary(options.Get("skills") ?? Path.Combine(AppContext.BaseDirectory, "agents")), registry, settings.Agents);
}

static async Task<int> LlmTestAsync(CliOptions options)
{
    var llm = CreateAgentLlm(options, out var registry);
    var names = options.Get("connector") is { } one ? [one] : registry.Names.Where(n => registry.Options(n)!.IsEnabled).ToList();
    if (names.Count == 0) throw new ArgumentException("Nessun connettore da collaudare: usare --config appsettings.json oppure --llm/--model.");

    var diagnostics = new LlmDiagnostics(llm.Skills);
    var allUsable = true;
    foreach (var name in names)
    {
        var o = registry.Options(name) ?? throw new ArgumentException($"Connettore '{name}' non configurato.");
        Console.WriteLine($"== {o.Name}: {o.Provider} / {o.Model}");
        var report = await diagnostics.RunAsync(o, registry.Get(name));
        foreach (var c in report.Checks)
            Console.WriteLine($"  [{c.Status switch { CheckStatus.Passed => " ok ", CheckStatus.Warning => "warn", CheckStatus.Failed => "FAIL", _ => "skip" }}] {c.Name,-30} {(c.Seconds > 0 ? $"{c.Seconds,6:0.0}s" : "       ")}  {c.Detail}");
        Console.WriteLine(report.IsUsable ? "  → utilizzabile" : "  → NON utilizzabile");
        allUsable &= report.IsUsable;
    }

    Console.WriteLine();
    foreach (var agent in SkillIds.All)
        Console.WriteLine($"  agente {agent,-16} → {llm.ConnectorFor(agent) ?? "none"}");
    return allUsable ? 0 : 3;
}

internal sealed class CliOptions
{
    public const string Usage = """
        NugoloMag Analyst — analisi automatica dei cambiamenti nei magazzini

        Uso:
          nugolomag demo    [--asof yyyy-MM-dd] [--out dir] [--no-llm]
          nugolomag analyze (--csv file | --connection "..." [--query-file q.sql]) [--asof yyyy-MM-dd]
                            [--baseline 28] [--recent 7] [--out dir] [--no-llm] [--model id]
          nugolomag ask "domanda" (--csv file | --connection "...") [--asof yyyy-MM-dd]
          nugolomag discover --connection "..." [--show-query]     analisi del database (agente)
          nugolomag seed-demo --master "..." [--asof yyyy-MM-dd]   crea il database GestionaleDemo
          nugolomag llm-test [--config appsettings.json] [--connector nome]   collaudo dei connettori LLM

        Connettore LLM: --config appsettings.json [--connector nome]  (sezione Llm, come la web app)
                    oppure --llm ollama|openai|azure|anthropic --model <id> [--llm-url <url>] [--api-key-env VAR]
                           [--deployment nome] [--no-tools] [--timeout secondi]
        (con ANTHROPIC_API_KEY impostata il default è Claude); senza LLM, o con --no-llm, sintesi da template deterministico.
        Istruzioni degli agenti: cartella agents/ accanto all'eseguibile, oppure --skills <cartella>.
        Exit code 2 se ci sono finding ad alta priorità (utile per alert da job schedulati).
        """;

    private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);
    public string? Command { get; private init; }
    public List<string> Positional { get; } = [];

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions { Command = args.FirstOrDefault()?.ToLowerInvariant() };
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                var key = args[i][2..];
                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
                o._named[key] = hasValue ? args[++i] : null;
            }
            else o.Positional.Add(args[i]);
        }
        return o;
    }

    public bool Has(string key) => _named.ContainsKey(key);
    public string? Get(string key) => _named.GetValueOrDefault(key);
    public int GetInt(string key, int fallback) => Get(key) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
}
