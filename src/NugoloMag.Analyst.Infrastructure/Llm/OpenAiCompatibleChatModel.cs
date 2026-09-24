using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NugoloMag.Analyst.Application.Llm;

namespace NugoloMag.Analyst.Infrastructure.Llm;

/// <summary>
/// Client per l'API "chat completions" in stile OpenAI: OpenAI, Azure OpenAI, Gemini (endpoint OpenAI), Mistral, Groq,
/// OpenRouter, LM Studio, vLLM, llama.cpp server e anche Ollama (endpoint /v1). HTTP e JSON scritti a mano.
/// Azure differisce solo per URL (deployment + api-version) e header della chiave (api-key).
/// </summary>
public sealed class OpenAiCompatibleChatModel(HttpClient http, LlmOptions options) : IChatModel
{
    public ChatModelInfo Info { get; } = new(options.Kind == "azure" ? "azure-openai" : "openai-compatible", options.Model, options.SupportsTools);

    private bool IsAzure => options.Kind == "azure";

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var body = Json.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("model", options.Model);
            w.WriteNumber(options.TokenParameter, Math.Min(request.MaxTokens, options.MaxTokens));
            if (options.Temperature is { } temperature) w.WriteNumber("temperature", temperature);
            if (request.JsonOnly && options.SupportsJsonMode)
            {
                w.WriteStartObject("response_format");
                w.WriteString("type", "json_object");
                w.WriteEndObject();
            }

            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "system");
            w.WriteString("content", request.System);
            w.WriteEndObject();
            foreach (var m in request.Messages) WriteMessage(w, m);
            w.WriteEndArray();

            if (request.Tools.Count > 0)
            {
                w.WriteStartArray("tools");
                foreach (var t in request.Tools)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "function");
                    w.WriteStartObject("function");
                    w.WriteString("name", t.Name);
                    w.WriteString("description", t.Description);
                    Json.WriteRaw(w, "parameters", t.InputSchemaJson);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        });

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint(options))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (options.ApiKey is { Length: > 0 } key)
        {
            if (IsAzure) message.Headers.Add("api-key", key);
            else message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        foreach (var (name, value) in options.Headers) message.Headers.TryAddWithoutValidation(name, value);

        using var response = await http.SendAsync(message, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new LlmException($"{Info.Provider} HTTP {(int)response.StatusCode}: {Trim(text)}");

        using var doc = JsonDocument.Parse(text);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var msg = choice.GetProperty("message");
        var calls = new List<ToolCall>();
        if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var call in tc.EnumerateArray())
            {
                var fn = call.GetProperty("function");
                var args = fn.TryGetProperty("arguments", out var a) ? (a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetRawText()) : "{}";
                calls.Add(new ToolCall(Json.String(call, "id") ?? $"call_{i}", Json.String(fn, "name") ?? "", args));
                i++;
            }
        }

        var finish = Json.String(choice, "finish_reason");
        var stop = finish switch
        {
            "length" => ChatStop.MaxTokens,
            "content_filter" => ChatStop.Refusal,
            _ when calls.Count > 0 => ChatStop.ToolUse,
            _ => ChatStop.EndTurn
        };
        return new ChatResponse(Json.String(msg, "content") ?? "", calls, stop);
    }

    public static string Endpoint(LlmOptions options)
    {
        if (options.Kind == "azure")
        {
            var resource = (options.BaseUrl ?? throw new LlmException($"Connettore '{options.Name}': Azure richiede BaseUrl.")).TrimEnd('/');
            var deployment = Uri.EscapeDataString(options.Deployment ?? options.Model);
            return $"{resource}/openai/deployments/{deployment}/chat/completions?api-version={Uri.EscapeDataString(options.ApiVersion)}";
        }
        return $"{(options.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/')}/chat/completions";
    }

    private static void WriteMessage(Utf8JsonWriter w, ChatMessage m)
    {
        w.WriteStartObject();
        switch (m.Role)
        {
            case ChatRole.User:
                w.WriteString("role", "user");
                w.WriteString("content", m.Text ?? "");
                break;
            case ChatRole.Assistant:
                w.WriteString("role", "assistant");
                if (m.Text is { Length: > 0 } t) w.WriteString("content", t); else w.WriteNull("content");
                if (m.ToolCalls.Count > 0)
                {
                    w.WriteStartArray("tool_calls");
                    foreach (var c in m.ToolCalls)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", c.Id);
                        w.WriteString("type", "function");
                        w.WriteStartObject("function");
                        w.WriteString("name", c.Name);
                        w.WriteString("arguments", c.ArgumentsJson);
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                break;
            default:
                w.WriteString("role", "tool");
                w.WriteString("tool_call_id", m.ToolCallId);
                w.WriteString("content", (m.IsError ? "ERRORE: " : "") + m.Text);
                break;
        }
        w.WriteEndObject();
    }

    private static string Trim(string s) => s.Length <= 500 ? s : s[..500];
}
