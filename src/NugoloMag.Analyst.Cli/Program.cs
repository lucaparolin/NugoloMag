using System.Globalization;
using Anthropic;
using Microsoft.Data.SqlClient;
using NugoloMag.Analyst.Application;
using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Application.Detection;
using NugoloMag.Analyst.Application.Narration;
using NugoloMag.Analyst.Application.RootCause;
using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Infrastructure.Claude;
using NugoloMag.Analyst.Infrastructure.Data;
using NugoloMag.Analyst.Infrastructure.Demo;
using NugoloMag.Analyst.Infrastructure.Reports;

var options = CliOptions.Parse(args);
if (options.Command is null or "help")
{
    Console.WriteLine(CliOptions.Usage);
    return options.Command is null ? 1 : 0;
}

try
{
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
        repository,
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

    var report = await analyst.AnalyzeAsync(window);

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
catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or SqlException)
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
    if (options.Has("no-llm") || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
        return template;

    return new ClaudeInsightNarrator(new AnthropicClient(), template, options.Get("model") ?? ClaudeInsightNarrator.DefaultModel);
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

        Con ANTHROPIC_API_KEY impostata la sintesi è scritta da Claude; altrimenti da un template deterministico.
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
