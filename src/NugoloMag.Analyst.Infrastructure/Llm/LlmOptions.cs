using Microsoft.Extensions.Configuration;

namespace NugoloMag.Analyst.Infrastructure.Llm;

/// <summary>
/// Un connettore LLM (una voce di Llm:Connectors). Provider ammessi:
/// <list type="bullet">
/// <item><c>ollama</c>: API nativa /api/chat (BaseUrl http://host:11434);</item>
/// <item><c>openai</c>: qualsiasi API "chat completions" compatibile (OpenAI, Gemini, Mistral, Groq, OpenRouter, LM Studio, vLLM, llama.cpp);</item>
/// <item><c>azure</c>: Azure OpenAI (BaseUrl della risorsa, Deployment, ApiVersion, chiave nell'header api-key);</item>
/// <item><c>anthropic</c>: SDK ufficiale;</item>
/// <item><c>none</c>: connettore disattivato.</item>
/// </list>
/// Le chiavi non stanno mai in configurazione: <see cref="ApiKeyEnvironmentVariable"/> indica la variabile d'ambiente che le contiene.
/// </summary>
public sealed record LlmOptions
{
    /// <summary>Nome del connettore (la chiave in Llm:Connectors), impostato in lettura.</summary>
    public string Name { get; init; } = "default";
    public string Provider { get; init; } = "none";
    public string Model { get; init; } = "";
    public string? BaseUrl { get; init; }
    public string? ApiKeyEnvironmentVariable { get; init; }
    /// <summary>Se falso, gli strumenti vengono emulati con il protocollo JSON di agents/_protocols/tool-emulation.md.</summary>
    public bool SupportsTools { get; init; } = true;
    /// <summary>Se falso non si chiede la modalità JSON (response_format / format) al server.</summary>
    public bool SupportsJsonMode { get; init; } = true;
    /// <summary>Null = non inviata (alcuni modelli di ragionamento la rifiutano).</summary>
    public double? Temperature { get; init; } = 0.1;
    public int MaxTokens { get; init; } = 4000;
    /// <summary>Nome del parametro dei token in uscita per le API OpenAI: max_tokens oppure max_completion_tokens (modelli recenti).</summary>
    public string TokenParameter { get; init; } = "max_tokens";
    /// <summary>Finestra di contesto per Ollama (num_ctx): i default bassi troncano prompt e risultati.</summary>
    public int ContextWindow { get; init; } = 16384;
    public int TimeoutSeconds { get; init; } = 300;
    /// <summary>Azure OpenAI: nome del deployment (default: Model).</summary>
    public string? Deployment { get; init; }
    /// <summary>Azure OpenAI: versione dell'API.</summary>
    public string ApiVersion { get; init; } = "2024-10-21";
    /// <summary>Header aggiuntivi (es. HTTP-Referer per OpenRouter). Non metterci chiavi.</summary>
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Description { get; init; }

    public bool IsEnabled => !string.Equals(Provider, "none", StringComparison.OrdinalIgnoreCase) && Model.Length > 0;

    public string? ApiKey => ApiKeyEnvironmentVariable is { Length: > 0 } name ? Environment.GetEnvironmentVariable(name) : null;

    /// <summary>Provider normalizzato: ollama, openai, azure, anthropic, none.</summary>
    public string Kind => Provider.Trim().ToLowerInvariant() switch
    {
        "ollama" => "ollama",
        "openai" or "openai-compatible" or "lmstudio" or "vllm" or "llamacpp" or "gemini" or "mistral" or "groq" or "openrouter" => "openai",
        "azure" or "azure-openai" => "azure",
        "anthropic" or "claude" => "anthropic",
        "none" or "" => "none",
        var other => other
    };
}

/// <summary>Configurazione dei modelli linguistici: connettori con nome, connettore predefinito, instradamento per agente.</summary>
public sealed class LlmSettings
{
    public string? Default { get; set; }
    public Dictionary<string, LlmOptions> Connectors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>agente (cartella in agents/) → connettore. "none" disattiva il modello per quell'agente.</summary>
    public Dictionary<string, string> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Legge la sezione Llm con il binder di configurazione (reflection: qui conviene, la struttura è annidata e cresce).
/// Accetta anche la forma piatta precedente (Llm:Provider, Llm:Model, ...) come unico connettore "default".
/// </summary>
public static class LlmConfiguration
{
    public static LlmSettings Read(IConfiguration configuration, string section = "Llm")
    {
        var llm = configuration.GetSection(section);
        var settings = new LlmSettings();
        llm.Bind(settings);

        if (settings.Connectors.Count == 0 && llm["Provider"] is { Length: > 0 })
        {
            var legacy = new LlmOptions();
            llm.Bind(legacy);
            settings.Connectors["default"] = legacy;
        }

        var named = settings.Connectors.ToDictionary(
            c => c.Key,
            c => c.Value with { Name = c.Key, Temperature = TemperatureOmitted(llm.GetSection("Connectors").GetSection(c.Key)) ? null : c.Value.Temperature },
            StringComparer.OrdinalIgnoreCase);
        if (named.TryGetValue("default", out var flat) && llm.GetSection("Connectors").GetChildren().All(c => c.Key != "default") && TemperatureOmitted(llm))
            named["default"] = flat with { Temperature = null };
        settings.Connectors = named;
        if (string.IsNullOrWhiteSpace(settings.Default))
            settings.Default = named.Count == 1 ? named.Keys.Single() : named.ContainsKey("default") ? "default" : null;
        return settings;
    }

    /// <summary>"Temperature": null (valore vuoto) = non inviarla. Il binder ignora i valori vuoti, quindi si legge a mano.</summary>
    private static bool TemperatureOmitted(IConfigurationSection connector) =>
        connector.GetSection("Temperature") is { Value: { Length: 0 } };

    /// <summary>Errori di configurazione rilevabili senza chiamare i modelli (le chiavi mancanti le segnala la diagnostica).</summary>
    public static IReadOnlyList<string> Validate(LlmSettings settings)
    {
        var problems = new List<string>();
        if (settings.Default is { } d && !settings.Connectors.ContainsKey(d) && !string.Equals(d, "none", StringComparison.OrdinalIgnoreCase))
            problems.Add($"Llm:Default = '{d}' non corrisponde a nessun connettore.");
        foreach (var (agent, connector) in settings.Agents.Where(a => !string.IsNullOrWhiteSpace(a.Value)))
            if (!settings.Connectors.ContainsKey(connector) && !string.Equals(connector, "none", StringComparison.OrdinalIgnoreCase))
                problems.Add($"Llm:Agents:{agent} = '{connector}' non corrisponde a nessun connettore.");
        foreach (var c in settings.Connectors.Values)
        {
            if (c.Kind is not ("ollama" or "openai" or "azure" or "anthropic" or "none"))
                problems.Add($"Connettore '{c.Name}': provider '{c.Provider}' sconosciuto (ammessi: ollama, openai, azure, anthropic, none).");
            if (c.Kind != "none" && c.Model.Length == 0)
                problems.Add($"Connettore '{c.Name}': manca Model.");
            if (c.Kind == "azure" && string.IsNullOrWhiteSpace(c.BaseUrl))
                problems.Add($"Connettore '{c.Name}': Azure richiede BaseUrl (https://<risorsa>.openai.azure.com).");
            if (c.Kind is "openai" or "azure" && c.TokenParameter is not ("max_tokens" or "max_completion_tokens"))
                problems.Add($"Connettore '{c.Name}': TokenParameter deve essere max_tokens o max_completion_tokens.");
        }
        return problems;
    }
}
