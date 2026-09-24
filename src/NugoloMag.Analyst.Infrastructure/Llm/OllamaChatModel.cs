using System.Text;
using System.Text.Json;
using NugoloMag.Analyst.Application.Llm;

namespace NugoloMag.Analyst.Infrastructure.Llm;

/// <summary>
/// Client nativo per Ollama (/api/chat). Supporta il tool calling dei modelli che lo prevedono (qwen2.5, llama3.1+, mistral-nemo…);
/// per gli altri impostare SupportsTools=false e gli strumenti vengono emulati via JSON (format=json).
/// Imposta num_ctx: il default di Ollama è piccolo e troncherebbe prompt e risultati delle query.
/// </summary>
public sealed class OllamaChatModel(HttpClient http, LlmOptions options) : IChatModel
{
    public ChatModelInfo Info { get; } = new("ollama", options.Model, options.SupportsTools);

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var body = Json.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("model", options.Model);
            w.WriteBoolean("stream", false);
            if (request.JsonOnly) w.WriteString("format", "json");

            w.WriteStartObject("options");
            w.WriteNumber("temperature", options.Temperature);
            w.WriteNumber("num_ctx", options.ContextWindow);
            w.WriteNumber("num_predict", Math.Min(request.MaxTokens, options.MaxTokens));
            w.WriteEndObject();

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

        var baseUrl = (options.BaseUrl ?? "http://localhost:11434").TrimEnd('/');
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync($"{baseUrl}/api/chat", content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new LlmException($"Ollama HTTP {(int)response.StatusCode}: {(text.Length > 500 ? text[..500] : text)}");

        using var doc = JsonDocument.Parse(text);
        var msg = doc.RootElement.GetProperty("message");
        var calls = new List<ToolCall>();
        if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var call in tc.EnumerateArray())
            {
                var fn = call.GetProperty("function");
                var args = fn.TryGetProperty("arguments", out var a) ? (a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetRawText()) : "{}";
                // Ollama non restituisce id: se ne crea uno stabile per abbinare i risultati.
                calls.Add(new ToolCall(Json.String(call, "id") ?? $"ollama_{Guid.NewGuid():N}"[..20], Json.String(fn, "name") ?? "", args));
                i++;
            }
        }

        var stop = Json.String(doc.RootElement, "done_reason") == "length" ? ChatStop.MaxTokens
            : calls.Count > 0 ? ChatStop.ToolUse
            : ChatStop.EndTurn;
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
                w.WriteString("content", m.Text ?? "");
                if (m.ToolCalls.Count > 0)
                {
                    w.WriteStartArray("tool_calls");
                    foreach (var c in m.ToolCalls)
                    {
                        w.WriteStartObject();
                        w.WriteStartObject("function");
                        w.WriteString("name", c.Name);
                        Json.WriteRaw(w, "arguments", c.ArgumentsJson);
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                break;
            default:
                w.WriteString("role", "tool");
                w.WriteString("tool_name", m.ToolName);
                w.WriteString("content", (m.IsError ? "ERRORE: " : "") + m.Text);
                break;
        }
        w.WriteEndObject();
    }
}
