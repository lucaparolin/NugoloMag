using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using NugoloMag.Analyst.Application.Llm;

namespace NugoloMag.Analyst.Infrastructure.Llm;

/// <summary>
/// Fornitore Anthropic tramite SDK ufficiale. I blocchi di ragionamento firmati del turno con strumenti
/// viaggiano in <see cref="ChatMessage.ProviderPayload"/> e vengono rispediti identici, come richiesto dall'API.
/// </summary>
public sealed class AnthropicChatModel(AnthropicClient client, LlmOptions options) : IChatModel
{
    public const string DefaultModel = "claude-opus-5";

    public ChatModelInfo Info { get; } = new("anthropic", options.Model.Length > 0 ? options.Model : DefaultModel, true);

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = Info.Model,
            MaxTokens = Math.Max(request.MaxTokens, 4000),
            System = request.JsonOnly ? request.System + "\n\nRispondi solo con un oggetto JSON valido." : request.System,
            Tools = request.Tools.Select(ToTool).ToList(),
            Messages = ToMessages(request.Messages),
            // In caso di rifiuto dei classificatori di sicurezza il server ripiega su un altro modello.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default()
        }, ct);

        if (response.StopReason == "refusal") return new ChatResponse("", [], ChatStop.Refusal);

        var payload = new List<BetaContentBlockParam>();
        var calls = new List<ToolCall>();
        var text = new System.Text.StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var t))
            {
                payload.Add(new BetaTextBlockParam { Text = t.Text });
                text.Append(t.Text);
            }
            else if (block.TryPickThinking(out var thinking))
            {
                payload.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            }
            else if (block.TryPickRedactedThinking(out var redacted))
            {
                payload.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
            }
            else if (block.TryPickToolUse(out var use))
            {
                payload.Add(new BetaToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                calls.Add(new ToolCall(use.ID, use.Name, Json.Write(w =>
                {
                    w.WriteStartObject();
                    foreach (var (k, v) in use.Input) { w.WritePropertyName(k); v.WriteTo(w); }
                    w.WriteEndObject();
                })));
            }
        }

        var stop = response.StopReason == "max_tokens" ? ChatStop.MaxTokens : calls.Count > 0 ? ChatStop.ToolUse : ChatStop.EndTurn;
        return new ChatResponse(text.ToString(), calls, stop, payload);
    }

    private static List<BetaMessageParam> ToMessages(IReadOnlyList<ChatMessage> messages)
    {
        var result = new List<BetaMessageParam>();
        var pendingResults = new List<BetaContentBlockParam>();

        void FlushResults()
        {
            if (pendingResults.Count == 0) return;
            result.Add(new BetaMessageParam { Role = Role.User, Content = pendingResults.ToList() });
            pendingResults.Clear();
        }

        foreach (var m in messages)
        {
            if (m.Role == ChatRole.Tool)
            {
                pendingResults.Add(new BetaToolResultBlockParam { ToolUseID = m.ToolCallId!, Content = m.Text ?? "", IsError = m.IsError });
                continue;
            }
            FlushResults();

            if (m.Role == ChatRole.User)
            {
                result.Add(new BetaMessageParam { Role = Role.User, Content = m.Text ?? "" });
            }
            else if (m.ProviderPayload is List<BetaContentBlockParam> blocks)
            {
                result.Add(new BetaMessageParam { Role = Role.Assistant, Content = blocks });
            }
            else if (m.ToolCalls.Count == 0)
            {
                result.Add(new BetaMessageParam { Role = Role.Assistant, Content = m.Text ?? "" });
            }
            else
            {
                // Messaggio con strumenti prodotto da un altro fornitore: si ricostruisce senza ragionamento.
                var blocksFromCalls = new List<BetaContentBlockParam>();
                if (m.Text is { Length: > 0 } text) blocksFromCalls.Add(new BetaTextBlockParam { Text = text });
                foreach (var c in m.ToolCalls)
                    blocksFromCalls.Add(new BetaToolUseBlockParam { ID = c.Id, Name = c.Name, Input = Arguments(c.ArgumentsJson) });
                result.Add(new BetaMessageParam { Role = Role.Assistant, Content = blocksFromCalls });
            }
        }
        FlushResults();
        return result;
    }

    private static Dictionary<string, JsonElement> Arguments(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static BetaToolUnion ToTool(ToolSpec spec)
    {
        using var doc = JsonDocument.Parse(spec.InputSchemaJson);
        var root = doc.RootElement;
        var properties = new Dictionary<string, JsonElement>();
        if (root.TryGetProperty("properties", out var props))
            foreach (var p in props.EnumerateObject()) properties[p.Name] = p.Value.Clone();
        var required = root.TryGetProperty("required", out var req) ? req.EnumerateArray().Select(r => r.GetString()!).ToList() : [];
        return new BetaTool { Name = spec.Name, Description = spec.Description, InputSchema = new() { Properties = properties, Required = required } };
    }
}
