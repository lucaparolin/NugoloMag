using Anthropic;
using NugoloMag.Analyst.Application.Llm;

namespace NugoloMag.Analyst.Infrastructure.Llm;

/// <summary>Crea il client del fornitore indicato dal connettore. Connettore disattivato = null (le funzioni LLM si spengono, il resto funziona).</summary>
public static class ChatModelFactory
{
    public static IChatModel? Create(LlmOptions options, HttpClient http)
    {
        if (!options.IsEnabled) return null;
        return options.Kind switch
        {
            "ollama" => new OllamaChatModel(http, options),
            "openai" or "azure" => new OpenAiCompatibleChatModel(http, options),
            "anthropic" => new AnthropicChatModel(
                new AnthropicClient
                {
                    ApiKey = options.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
                    BaseUrl = options.BaseUrl ?? "https://api.anthropic.com",
                    HttpClient = http
                },
                options),
            _ => throw new LlmException($"Connettore '{options.Name}': fornitore sconosciuto '{options.Provider}' (ammessi: ollama, openai, azure, anthropic, none).")
        };
    }
}

/// <summary>
/// I connettori configurati. Ogni connettore ha il suo HttpClient (timeout proprio) e il client del modello viene creato
/// alla prima richiesta, così un connettore mal configurato non blocca gli altri.
/// </summary>
public sealed class LlmConnectorRegistry : IConnectorCatalog, IDisposable
{
    private readonly LlmSettings _settings;
    private readonly Func<LlmOptions, HttpClient> _http;
    private readonly Dictionary<string, (HttpClient Http, IChatModel? Model)> _created = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public LlmConnectorRegistry(LlmSettings settings, Func<LlmOptions, HttpClient>? httpFactory = null)
    {
        _settings = settings;
        _http = httpFactory ?? (o => new HttpClient { Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds) });
    }

    public LlmSettings Settings => _settings;
    public string? DefaultConnector => _settings.Default;
    public IReadOnlyList<string> Names => _settings.Connectors.Keys.ToList();

    public LlmOptions? Options(string name) => _settings.Connectors.GetValueOrDefault(name);

    public IChatModel? Get(string name)
    {
        var options = Options(name) ?? throw new LlmException($"Connettore LLM '{name}' non configurato (Llm:Connectors).");
        lock (_lock)
        {
            if (_created.TryGetValue(name, out var existing)) return existing.Model;
            var http = _http(options);
            var model = ChatModelFactory.Create(options, http);
            _created[name] = (http, model);
            return model;
        }
    }

    public void Dispose()
    {
        foreach (var (http, _) in _created.Values) http.Dispose();
        _created.Clear();
    }
}
