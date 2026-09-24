using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Domain.Assistant;
using NugoloMag.Analyst.Domain.Discovery;

namespace NugoloMag.Analyst.Application.Assistant;

/// <summary>Gestisce le conversazioni: prepara contesto e strumenti, chiama l'agente, salva la trascrizione.</summary>
public sealed class ConversationService(
    IConversationStore conversations,
    ISavedQueryStore savedQueries,
    IDiscoveryStore discoveries,
    ISourceRegistry sources,
    IDataAgent agent,
    TimeProvider clock)
{
    public const int MaxMessageLength = 4000;

    public bool IsAgentAvailable => agent.IsAvailable;

    public Task<long> StartAsync(string sourceName, CancellationToken ct = default)
    {
        if (!sources.Names.Contains(sourceName, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Sorgente '{sourceName}' non configurata.");
        return conversations.CreateAsync(sourceName, "Nuova conversazione", clock.GetUtcNow(), ct);
    }

    public async Task SendAsync(long conversationId, string message, CancellationToken ct = default)
    {
        message = message.Trim();
        if (message.Length == 0) throw new ArgumentException("Messaggio vuoto.");
        if (message.Length > MaxMessageLength) throw new ArgumentException($"Messaggio troppo lungo (max {MaxMessageLength} caratteri).");

        var conversation = await conversations.GetAsync(conversationId, ct) ?? throw new InvalidOperationException("Conversazione non trovata.");
        var history = await conversations.EntriesAsync(conversationId, ct);
        var now = clock.GetUtcNow();

        // Il messaggio dell'utente si salva subito: se l'agente fallisce non va perso.
        await conversations.AppendAsync(conversationId, [new ConversationEntry(0, EntryKind.User, message, null, null, null, false, now)], now, ct);
        if (history.Count == 0) await conversations.RenameAsync(conversationId, Title(message), ct);

        IReadOnlyList<ConversationEntry> reply;
        try
        {
            var source = sources.Get(conversation.SourceName);
            var catalog = await source.ReadCatalogAsync(ct);
            var toolbox = new DataAgentToolbox(source, catalog, savedQueries, conversationId, clock);
            var context = new AgentContext(conversation.SourceName, catalog.Database, await BriefAsync(conversation.SourceName, ct),
                DateOnly.FromDateTime(clock.GetLocalNow().DateTime));
            reply = await agent.ReplyAsync(context, history, message, toolbox, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            reply = [new ConversationEntry(0, EntryKind.Assistant, $"Non sono riuscito a completare la risposta: {ex.Message}", null, null, null, true, clock.GetUtcNow())];
        }

        await conversations.AppendAsync(conversationId, reply, clock.GetUtcNow(), ct);
    }

    /// <summary>L'ultima analisi utilizzabile del database della stessa sorgente, se esiste.</summary>
    private async Task<string?> BriefAsync(string sourceName, CancellationToken ct)
    {
        foreach (var summary in await discoveries.ListAsync(20, ct))
        {
            if (!string.Equals(summary.SourceName, sourceName, StringComparison.OrdinalIgnoreCase) || summary.Readiness == Readiness.Blocked) continue;
            return await discoveries.GetAsync(summary.Id, ct) is { } report ? DiscoveryBrief.From(report) : null;
        }
        return null;
    }

    private static string Title(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length <= 80 ? line : line[..77] + "…";
    }
}
