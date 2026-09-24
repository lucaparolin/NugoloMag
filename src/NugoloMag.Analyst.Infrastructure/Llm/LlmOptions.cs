namespace NugoloMag.Analyst.Infrastructure.Llm;

/// <summary>
/// Configurazione del modello linguistico (sezione "Llm" di appsettings). Esempi:
/// Ollama locale: Provider=ollama, Model=qwen2.5:7b, BaseUrl=http://localhost:11434;
/// Anthropic: Provider=anthropic, Model=claude-opus-5 (chiave in ANTHROPIC_API_KEY);
/// OpenAI-compatibile (OpenAI, Azure, LM Studio, vLLM, llama.cpp): Provider=openai, BaseUrl=.../v1.
/// </summary>
public sealed record LlmOptions
{
    public string Provider { get; init; } = "none";
    public string Model { get; init; } = "";
    public string? BaseUrl { get; init; }
    /// <summary>Nome della variabile d'ambiente con la chiave (la chiave non sta nella configurazione).</summary>
    public string? ApiKeyEnvironmentVariable { get; init; }
    /// <summary>Se il modello non supporta il tool calling nativo, gli strumenti vengono emulati via JSON.</summary>
    public bool SupportsTools { get; init; } = true;
    public double Temperature { get; init; } = 0.1;
    public int MaxTokens { get; init; } = 4000;
    /// <summary>Finestra di contesto per Ollama (num_ctx): i default bassi troncano prompt e risultati.</summary>
    public int ContextWindow { get; init; } = 16384;
    public int TimeoutSeconds { get; init; } = 300;

    public bool IsEnabled => !string.Equals(Provider, "none", StringComparison.OrdinalIgnoreCase) && Model.Length > 0;

    public string? ApiKey => ApiKeyEnvironmentVariable is { Length: > 0 } name ? Environment.GetEnvironmentVariable(name) : null;
}

/// <summary>Lettura esplicita della configurazione (chiave → valore), senza binder basato su reflection.</summary>
public static class LlmOptionsReader
{
    public static LlmOptions Read(Func<string, string?> get, string section = "Llm")
    {
        string? V(string key) => get($"{section}:{key}") is { Length: > 0 } v ? v : null;
        var defaults = new LlmOptions();
        return new LlmOptions
        {
            Provider = V("Provider") ?? defaults.Provider,
            Model = V("Model") ?? defaults.Model,
            BaseUrl = V("BaseUrl"),
            ApiKeyEnvironmentVariable = V("ApiKeyEnvironmentVariable"),
            SupportsTools = V("SupportsTools") is { } t ? bool.Parse(t) : defaults.SupportsTools,
            Temperature = V("Temperature") is { } temp ? double.Parse(temp, System.Globalization.CultureInfo.InvariantCulture) : defaults.Temperature,
            MaxTokens = V("MaxTokens") is { } mt ? int.Parse(mt, System.Globalization.CultureInfo.InvariantCulture) : defaults.MaxTokens,
            ContextWindow = V("ContextWindow") is { } cw ? int.Parse(cw, System.Globalization.CultureInfo.InvariantCulture) : defaults.ContextWindow,
            TimeoutSeconds = V("TimeoutSeconds") is { } ts ? int.Parse(ts, System.Globalization.CultureInfo.InvariantCulture) : defaults.TimeoutSeconds
        };
    }
}
