using System.Text.Json;

namespace NugoloMag.Analyst.Application.Llm;

/// <summary>
/// Astrazione neutra rispetto al fornitore dell'LLM (Anthropic, OpenAI-compatibili, Ollama, ...).
/// Gli agenti dipendono solo da questa interfaccia: cambiare modello è una riga di configurazione.
/// </summary>
public interface IChatModel
{
    ChatModelInfo Info { get; }
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default);
}

/// <param name="SupportsTools">Se falso, gli strumenti vengono emulati con un protocollo JSON (vedi <see cref="ToolLoop"/>).</param>
public sealed record ChatModelInfo(string Provider, string Model, bool SupportsTools);

public enum ChatRole { User, Assistant, Tool }

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <param name="ProviderPayload">
/// Contenuto opaco del fornitore che ha prodotto il messaggio (es. blocchi di ragionamento firmati) da rispedire
/// identico nel turno successivo. Gli altri fornitori lo ignorano.
/// </param>
public sealed record ChatMessage(
    ChatRole Role,
    string? Text,
    IReadOnlyList<ToolCall> ToolCalls,
    string? ToolCallId = null,
    string? ToolName = null,
    bool IsError = false,
    object? ProviderPayload = null)
{
    public static ChatMessage User(string text) => new(ChatRole.User, text, []);
    public static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text, []);
    public static ChatMessage ToolResult(ToolCall call, string content, bool isError) =>
        new(ChatRole.Tool, content, [], call.Id, call.Name, isError);
}

public sealed record ChatRequest(
    string System,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolSpec> Tools,
    int MaxTokens = 4000,
    bool JsonOnly = false);

public enum ChatStop { EndTurn, ToolUse, MaxTokens, Refusal }

public sealed record ChatResponse(string Text, IReadOnlyList<ToolCall> ToolCalls, ChatStop Stop, object? ProviderPayload = null)
{
    public ChatMessage ToAssistantMessage() => new(ChatRole.Assistant, Text, ToolCalls, ProviderPayload: ProviderPayload);
}

/// <summary>Definizione di uno strumento: nome, descrizione e JSON Schema dell'input come testo (niente reflection).</summary>
public sealed record ToolSpec(string Name, string Description, string InputSchemaJson);

public sealed record ToolOutcome(string Content, bool IsError);

/// <summary>Insieme di strumenti eseguibili da un agente, indipendente dal fornitore dell'LLM.</summary>
public interface IToolbox
{
    IReadOnlyList<ToolSpec> Specs { get; }
    Task<ToolOutcome> ExecuteAsync(string name, JsonElement input, CancellationToken ct = default);
}

/// <summary>Unisce più toolbox (es. strumenti SQL + agenti specialisti + strumento di consegna del risultato).</summary>
public sealed class CompositeToolbox(params IToolbox[] toolboxes) : IToolbox
{
    public IReadOnlyList<ToolSpec> Specs { get; } = toolboxes.SelectMany(t => t.Specs).ToList();

    public Task<ToolOutcome> ExecuteAsync(string name, JsonElement input, CancellationToken ct = default)
    {
        var owner = toolboxes.FirstOrDefault(t => t.Specs.Any(s => s.Name == name));
        return owner is null
            ? Task.FromResult(new ToolOutcome($"Strumento sconosciuto: {name}", true))
            : owner.ExecuteAsync(name, input, ct);
    }
}

public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);
