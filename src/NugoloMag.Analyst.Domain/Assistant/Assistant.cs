namespace NugoloMag.Analyst.Domain.Assistant;

/// <summary>Una conversazione tra utente e assistente dati su una sorgente.</summary>
public sealed record Conversation(long Id, string SourceName, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public enum EntryKind { User, Assistant, ToolCall }

/// <summary>
/// Una voce della trascrizione: messaggio dell'utente, risposta dell'assistente o uso di uno strumento
/// (con input e risultato), così l'utente vede esattamente quali query sono state eseguite.
/// </summary>
public sealed record ConversationEntry(
    int Seq,
    EntryKind Kind,
    string? Text,
    string? ToolName,
    string? ToolInput,
    string? ToolResult,
    bool IsError,
    DateTimeOffset CreatedAt);

/// <summary>Una query di analisi salvata (dall'assistente o dall'utente), rieseguibile.</summary>
public sealed record SavedQuery(
    long Id,
    string SourceName,
    string Name,
    string Description,
    string Sql,
    string CreatedBy,
    long? ConversationId,
    DateTimeOffset CreatedAt);

/// <summary>Risultato tabellare di una query, già convertito in testo.</summary>
public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated, double Seconds);
