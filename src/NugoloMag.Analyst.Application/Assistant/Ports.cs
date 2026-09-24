using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Domain.Assistant;

namespace NugoloMag.Analyst.Application.Assistant;

public interface IConversationStore
{
    Task<long> CreateAsync(string sourceName, string title, DateTimeOffset now, CancellationToken ct = default);
    Task<Conversation?> GetAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<Conversation>> ListAsync(int take, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationEntry>> EntriesAsync(long conversationId, CancellationToken ct = default);
    Task AppendAsync(long conversationId, IReadOnlyList<ConversationEntry> entries, DateTimeOffset now, CancellationToken ct = default);
    Task RenameAsync(long conversationId, string title, CancellationToken ct = default);
}

public interface ISavedQueryStore
{
    Task<long> SaveAsync(SavedQuery query, CancellationToken ct = default);
    Task<SavedQuery?> GetAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<SavedQuery>> ListAsync(string? sourceName, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
}

public sealed record AgentContext(string SourceName, string Database, string? DiscoveryBrief, DateOnly Today);

/// <summary>
/// L'agente conversazionale: riceve la storia e il nuovo messaggio, usa gli strumenti in autonomia
/// e restituisce le nuove voci (usi di strumenti + risposta finale, che può essere una domanda all'utente).
/// </summary>
public interface IDataAgent
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<ConversationEntry>> ReplyAsync(
        AgentContext context, IReadOnlyList<ConversationEntry> history, string message, IToolbox tools, CancellationToken ct = default);
}

public sealed class UnavailableDataAgent : IDataAgent
{
    public bool IsAvailable => false;

    public Task<IReadOnlyList<ConversationEntry>> ReplyAsync(
        AgentContext context, IReadOnlyList<ConversationEntry> history, string message, IToolbox tools, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>(
        [
            new ConversationEntry(0, EntryKind.Assistant,
                "L'assistente conversazionale richiede un modello linguistico: configurare la sezione Llm di appsettings (Ollama, Anthropic o compatibile OpenAI) e riavviare l'applicazione.",
                null, null, null, true, DateTimeOffset.UtcNow)
        ]);
}
