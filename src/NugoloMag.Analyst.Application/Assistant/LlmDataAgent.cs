using System.Text;
using System.Text.Json;
using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Domain.Assistant;

namespace NugoloMag.Analyst.Application.Assistant;

/// <summary>
/// Assistente dati conversazionale, indipendente dal fornitore: qualsiasi <see cref="IChatModel"/>
/// (Claude, modelli OpenAI-compatibili, Ollama locale) guida il ciclo di strumenti in <see cref="ToolLoop"/>.
/// Le istruzioni sono in agents/data-assistant/SKILL.md.
/// </summary>
public sealed class LlmDataAgent(AgentLlm llm) : IDataAgent
{
    public bool IsAvailable => llm.IsEnabled(SkillIds.DataAssistant);

    public async Task<IReadOnlyList<ConversationEntry>> ReplyAsync(
        AgentContext context, IReadOnlyList<ConversationEntry> history, string message, IToolbox tools, CancellationToken ct = default)
    {
        var messages = Rebuild(history);
        messages.Add(ChatMessage.User(message));

        var agent = llm.Resolve(SkillIds.DataAssistant) ?? throw new LlmException("All'assistente dati non è assegnato un modello linguistico.");
        var result = await agent.RunToolsAsync(System(agent, context), messages, tools, ct: ct);

        var entries = new List<ConversationEntry>();
        foreach (var step in result.Steps)
        {
            if (step.Call is { } call)
                entries.Add(new ConversationEntry(0, EntryKind.ToolCall, null, call.Name, Normalize(call.ArgumentsJson), step.Outcome!.Content, step.Outcome.IsError, DateTimeOffset.UtcNow));
            else
                entries.Add(new ConversationEntry(0, EntryKind.Assistant, step.Text, null, null, null, false, DateTimeOffset.UtcNow));
        }
        entries.Add(new ConversationEntry(0, EntryKind.Assistant, result.FinalText, null, null, null, !result.Completed, DateTimeOffset.UtcNow));
        return entries;
    }

    private static string System(AgentBinding agent, AgentContext context) => agent.System(new Dictionary<string, string?>
    {
        ["source"] = context.SourceName,
        ["database"] = context.Database,
        ["today"] = context.Today.ToString("yyyy-MM-dd"),
        ["discovery_brief"] = context.DiscoveryBrief is { } brief ? $"<analisi_database>\n{brief}\n</analisi_database>" : null
    });

    /// <summary>
    /// I turni precedenti diventano testo: domanda dell'utente, poi risposta dell'assistente preceduta da un promemoria
    /// delle query eseguite. Funziona con ogni fornitore e riparte sempre dallo stesso prefisso.
    /// </summary>
    public static List<ChatMessage> Rebuild(IReadOnlyList<ConversationEntry> history)
    {
        var messages = new List<ChatMessage>();
        var user = new StringBuilder();
        var assistant = new StringBuilder();

        void Flush()
        {
            if (user.Length == 0) { assistant.Clear(); return; }
            messages.Add(ChatMessage.User(user.ToString().Trim()));
            messages.Add(ChatMessage.Assistant(assistant.Length == 0 ? "(risposta non disponibile)" : assistant.ToString().Trim()));
            user.Clear();
            assistant.Clear();
        }

        foreach (var e in history)
        {
            switch (e.Kind)
            {
                case EntryKind.User:
                    if (assistant.Length > 0) Flush();
                    if (user.Length > 0) user.AppendLine();
                    user.Append(e.Text);
                    break;
                case EntryKind.ToolCall:
                    assistant.AppendLine($"[{e.ToolName} {Summarize(e)}]");
                    break;
                default:
                    assistant.AppendLine(e.Text);
                    break;
            }
        }
        Flush();
        return messages;
    }

    private static string Summarize(ConversationEntry e)
    {
        var detail = "";
        if (e.ToolInput is { } json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("sql", out var s)) detail = s.GetString() ?? "";
                else if (doc.RootElement.TryGetProperty("table", out var t)) detail = t.GetString() ?? "";
            }
            catch (JsonException) { }
        }
        var lastLine = e.ToolResult?.Split('\n').LastOrDefault() ?? "";
        return $"{detail.ReplaceLineEndings(" ")} → {(e.IsError ? "ERRORE " : "")}{lastLine}".Trim();
    }

    private static string Normalize(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
