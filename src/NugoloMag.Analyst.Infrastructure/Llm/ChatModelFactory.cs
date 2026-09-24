using Anthropic;
using NugoloMag.Analyst.Application.Llm;

namespace NugoloMag.Analyst.Infrastructure.Llm;

/// <summary>Sceglie il fornitore in base alla configurazione. Nessun modello = funzioni LLM disattivate (il resto funziona).</summary>
public static class ChatModelFactory
{
    public static IChatModel? Create(LlmOptions options, HttpClient http)
    {
        if (!options.IsEnabled) return null;
        return options.Provider.ToLowerInvariant() switch
        {
            "ollama" => new OllamaChatModel(http, options),
            "openai" or "openai-compatible" or "azure" or "lmstudio" or "vllm" => new OpenAiCompatibleChatModel(http, options),
            "anthropic" or "claude" => new AnthropicChatModel(
                new AnthropicClient
                {
                    ApiKey = options.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
                    BaseUrl = options.BaseUrl ?? "https://api.anthropic.com",
                    HttpClient = http
                },
                options),
            _ => throw new ArgumentException($"Fornitore LLM sconosciuto: {options.Provider} (ammessi: ollama, openai, anthropic, none).")
        };
    }
}
