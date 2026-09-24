using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using NugoloMag.Analyst.Application.Llm;

namespace NugoloMag.Analyst.Infrastructure.Llm;

public enum CheckStatus { Passed, Warning, Failed, Skipped }

public sealed record DiagnosticCheck(string Name, CheckStatus Status, string Detail, double Seconds = 0);

public sealed record ConnectorReport(
    string Connector, string Provider, string Model, string Endpoint, bool ToolsNative,
    IReadOnlyList<DiagnosticCheck> Checks, DateTimeOffset At)
{
    public bool IsUsable => Checks.All(c => c.Status != CheckStatus.Failed);
}

/// <summary>
/// Collaudo di un connettore con prove fisse (testi in agents/connector-check/): configurazione, catalogo modelli,
/// risposta semplice, modalità JSON, uso di uno strumento (nativo o emulato). Ogni prova misura la latenza.
/// </summary>
public sealed class LlmDiagnostics(ISkillLibrary skills, HttpClient? catalogHttp = null)
{
    private readonly HttpClient _http = catalogHttp ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    private const string ExpectedStock = "137";

    public async Task<ConnectorReport> RunAsync(LlmOptions options, IChatModel? model, CancellationToken ct = default)
    {
        var checks = new List<DiagnosticCheck> { Configuration(options) };
        var endpoint = Endpoint(options);
        if (model is null || checks[0].Status == CheckStatus.Failed)
            return new ConnectorReport(options.Name, options.Provider, options.Model, endpoint, options.SupportsTools, checks, DateTimeOffset.UtcNow);

        checks.Add(await CatalogAsync(options, _http, ct));
        var skill = skills.Get(SkillIds.ConnectorCheck);
        var system = SkillTemplate.Render(skill.Instructions);
        var maxTokens = skill.MaxTokens(400);

        var reply = await TimedAsync("Risposta semplice", async () =>
        {
            var r = await model.CompleteAsync(new ChatRequest(system, [ChatMessage.User(skill.Prompt("reply"))], [], maxTokens), ct);
            return r.Stop == ChatStop.Refusal ? (CheckStatus.Failed, "il modello ha rifiutato")
                : string.IsNullOrWhiteSpace(r.Text) ? (CheckStatus.Failed, "risposta vuota")
                : (CheckStatus.Passed, $"«{Short(r.Text)}»");
        });
        checks.Add(reply);
        if (reply.Status == CheckStatus.Failed)
        {
            checks.Add(new DiagnosticCheck("Modalità JSON", CheckStatus.Skipped, "saltata: il modello non risponde"));
            checks.Add(new DiagnosticCheck("Strumenti", CheckStatus.Skipped, "saltata: il modello non risponde"));
            return new ConnectorReport(options.Name, options.Provider, options.Model, endpoint, options.SupportsTools, checks, DateTimeOffset.UtcNow);
        }

        checks.Add(await TimedAsync("Modalità JSON", async () =>
        {
            var r = await model.CompleteAsync(new ChatRequest(system, [ChatMessage.User(skill.Prompt("json"))], [], maxTokens, JsonOnly: true), ct);
            var json = ToolLoop.ExtractJsonObject(r.Text);
            if (json is null) return (CheckStatus.Failed, $"nessun oggetto JSON: «{Short(r.Text)}»");
            using var doc = JsonDocument.Parse(json);
            var ok = doc.RootElement.TryGetProperty("giacenza", out var g) && g.ToString() == ExpectedStock
                     && doc.RootElement.TryGetProperty("articolo", out var a) && a.GetString() == "ART-042";
            return ok ? (CheckStatus.Passed, json) : (CheckStatus.Warning, $"JSON valido ma con valori diversi: {Short(json)}");
        }));

        checks.Add(await TimedAsync(options.SupportsTools ? "Strumenti (nativi)" : "Strumenti (emulati via JSON)", async () =>
        {
            var toolbox = new StockToolbox(skill.Prompt("tool-giacenza"));
            var result = await ToolLoop.RunAsync(model, system, [ChatMessage.User(skill.Prompt("tools"))], toolbox,
                skill.MaxRounds(4), toolProtocol: skills.Protocol(SkillIds.ToolEmulationProtocol), maxTokens: maxTokens, ct: ct);
            if (toolbox.Calls.Count == 0)
                return (CheckStatus.Failed, $"lo strumento non è stato usato. Risposta: «{Short(result.FinalText)}»"
                    + (options.SupportsTools ? " — provare SupportsTools=false (emulazione)" : ""));
            if (!toolbox.Calls.Any(c => c.Contains("ART-042", StringComparison.OrdinalIgnoreCase)))
                return (CheckStatus.Warning, $"strumento usato con argomenti inattesi: {string.Join(" | ", toolbox.Calls)}");
            return result.FinalText.Contains(ExpectedStock, StringComparison.Ordinal)
                ? (CheckStatus.Passed, $"giacenza({toolbox.Calls[0]}) → «{Short(result.FinalText)}»")
                : (CheckStatus.Warning, $"strumento usato ma risposta senza il dato: «{Short(result.FinalText)}»");
        }));

        return new ConnectorReport(options.Name, options.Provider, options.Model, endpoint, options.SupportsTools, checks, DateTimeOffset.UtcNow);
    }

    private static DiagnosticCheck Configuration(LlmOptions o)
    {
        if (!o.IsEnabled) return new DiagnosticCheck("Configurazione", CheckStatus.Failed, "connettore disattivato (Provider none o Model vuoto)");
        if (o.Kind is not ("ollama" or "openai" or "azure" or "anthropic"))
            return new DiagnosticCheck("Configurazione", CheckStatus.Failed, $"provider sconosciuto: {o.Provider}");
        if (o.ApiKeyEnvironmentVariable is { Length: > 0 } env && string.IsNullOrEmpty(o.ApiKey))
            return new DiagnosticCheck("Configurazione", CheckStatus.Failed, $"la variabile d'ambiente {env} con la chiave non è impostata");
        if (o.Kind == "anthropic" && string.IsNullOrEmpty(o.ApiKey) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            return new DiagnosticCheck("Configurazione", CheckStatus.Failed, "chiave Anthropic assente (ANTHROPIC_API_KEY)");
        if (o.Kind is "openai" or "azure" && o.ApiKeyEnvironmentVariable is null && !IsLocal(o.BaseUrl))
            return new DiagnosticCheck("Configurazione", CheckStatus.Warning, "nessuna chiave configurata per un servizio remoto (ApiKeyEnvironmentVariable)");
        return new DiagnosticCheck("Configurazione", CheckStatus.Passed,
            $"{o.Kind}, modello {o.Model}, timeout {o.TimeoutSeconds}s, strumenti {(o.SupportsTools ? "nativi" : "emulati")}");
    }

    /// <summary>Il modello è disponibile sul server? Ollama: /api/tags; OpenAI-compatibili: /models. Esito informativo.</summary>
    private static async Task<DiagnosticCheck> CatalogAsync(LlmOptions o, HttpClient http, CancellationToken ct)
    {
        string url;
        if (o.Kind == "ollama") url = $"{(o.BaseUrl ?? "http://localhost:11434").TrimEnd('/')}/api/tags";
        else if (o.Kind == "openai") url = $"{(o.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/')}/models";
        else return new DiagnosticCheck("Catalogo modelli", CheckStatus.Skipped, "non previsto per questo fornitore");

        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (o.ApiKey is { Length: > 0 } key) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            foreach (var (name, value) in o.Headers) request.Headers.TryAddWithoutValidation(name, value);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await http.SendAsync(request, cts.Token);
            var text = await response.Content.ReadAsStringAsync(cts.Token);
            if (!response.IsSuccessStatusCode)
                return new DiagnosticCheck("Catalogo modelli", CheckStatus.Warning, $"HTTP {(int)response.StatusCode} su {url}", sw.Elapsed.TotalSeconds);

            using var doc = JsonDocument.Parse(text);
            var names = (o.Kind == "ollama" ? Items(doc.RootElement, "models", "name") : Items(doc.RootElement, "data", "id")).ToList();
            var found = names.Any(n => string.Equals(n, o.Model, StringComparison.OrdinalIgnoreCase)
                                       || (o.Kind == "ollama" && string.Equals(n, o.Model + ":latest", StringComparison.OrdinalIgnoreCase)));
            return found
                ? new DiagnosticCheck("Catalogo modelli", CheckStatus.Passed, $"{o.Model} presente ({names.Count} modelli sul server)", sw.Elapsed.TotalSeconds)
                : new DiagnosticCheck("Catalogo modelli", o.Kind == "ollama" ? CheckStatus.Failed : CheckStatus.Warning,
                    $"{o.Model} non trovato{(o.Kind == "ollama" ? $" (ollama pull {o.Model})" : "")}. Disponibili: {string.Join(", ", names.Take(8))}{(names.Count > 8 ? "…" : "")}",
                    sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new DiagnosticCheck("Catalogo modelli", o.Kind == "ollama" ? CheckStatus.Failed : CheckStatus.Warning,
                $"server non raggiungibile su {url}: {ex.Message}", sw.Elapsed.TotalSeconds);
        }
    }

    private static IEnumerable<string> Items(JsonElement root, string array, string field) =>
        root.TryGetProperty(array, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(e => Json.String(e, field)).OfType<string>()
            : [];

    private static async Task<DiagnosticCheck> TimedAsync(string name, Func<Task<(CheckStatus, string)>> check)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (status, detail) = await check();
            return new DiagnosticCheck(name, status, detail, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ex is TaskCanceledException)
        {
            var detail = ex is TaskCanceledException ? "tempo scaduto (aumentare TimeoutSeconds o usare un modello più piccolo)" : ex.Message;
            return new DiagnosticCheck(name, CheckStatus.Failed, Short(detail, 400), sw.Elapsed.TotalSeconds);
        }
    }

    private static string Endpoint(LlmOptions o) => o.Kind switch
    {
        "ollama" => $"{(o.BaseUrl ?? "http://localhost:11434").TrimEnd('/')}/api/chat",
        "openai" or "azure" when o.IsEnabled => SafeEndpoint(o),
        "anthropic" => o.BaseUrl ?? "https://api.anthropic.com",
        _ => "—"
    };

    private static string SafeEndpoint(LlmOptions o)
    {
        try { return OpenAiCompatibleChatModel.Endpoint(o); }
        catch (LlmException ex) { return ex.Message; }
    }

    private static bool IsLocal(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase));

    private static string Short(string s, int max = 160)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>Uno strumento finto con un dato noto: verifica che il modello lo chiami e ne usi il risultato.</summary>
    private sealed class StockToolbox(string description) : IToolbox
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<ToolSpec> Specs { get; } =
        [
            new("giacenza", description, """{"type":"object","properties":{"articolo":{"type":"string","description":"codice articolo"}},"required":["articolo"]}""")
        ];

        public Task<ToolOutcome> ExecuteAsync(string name, JsonElement input, CancellationToken ct = default)
        {
            var code = input.TryGetProperty("articolo", out var a) ? a.ToString() : input.GetRawText();
            Calls.Add(code);
            return Task.FromResult(code.Contains("ART-042", StringComparison.OrdinalIgnoreCase)
                ? new ToolOutcome($"ART-042: {ExpectedStock} pezzi in giacenza", false)
                : new ToolOutcome($"articolo {code} non trovato", true));
        }
    }
}
