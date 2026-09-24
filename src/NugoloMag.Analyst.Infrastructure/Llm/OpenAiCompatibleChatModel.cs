using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NugoloMag.Analyst.Application.Llm;

namespace NugoloMag.Analyst.Infrastructure.Llm;

/// <summary>
/// Client per l'API "chat completions" in stile OpenAI: OpenAI, Azure OpenAI, LM Studio, vLLM, llama.cpp server,
/// e anche Ollama (endpoint /v1). HTTP e JSON scritti a mano.
/// </summary>
public sealed class OpenAiCompatibleChatModel(HttpClient http, LlmOptions options) : IChatModel
{
    public ChatModelInfo Info { get; } = new("openai-compatible", options.Model, options.SupportsTools);

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var body = Json.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("model", options.Model);
            w.WriteNumber("max_tokens", Math.Min(request.MaxTokens, options.MaxTokens));
            w.WriteNumber("temperature", options.Temperature);
            if (request.JsonOnly)
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

        var baseUrl = (options.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (options.ApiKey is { Length: > 0 } key) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

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
