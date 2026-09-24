using System.Data;
using NugoloMag.Analyst.Application.Assistant;
using NugoloMag.Analyst.Domain.Assistant;
using static NugoloMag.Analyst.Infrastructure.Store.SqlConnectionFactory;

namespace NugoloMag.Analyst.Infrastructure.Store;

public sealed class SqlConversationStore(SqlConnectionFactory connections) : IConversationStore
{
    public async Task<long> CreateAsync(string sourceName, string title, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nugolo.Conversation (SourceName, Title, CreatedAt, UpdatedAt)
            OUTPUT INSERTED.ConversationId VALUES (@source, @title, @now, @now);
            """;
        cmd.Parameters.Add(Param("@source", SqlDbType.NVarChar, sourceName));
        cmd.Parameters.Add(Param("@title", SqlDbType.NVarChar, title));
        cmd.Parameters.Add(Param("@now", SqlDbType.DateTimeOffset, now));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<Conversation?> GetAsync(long id, CancellationToken ct = default) =>
        (await Query("SELECT ConversationId, SourceName, Title, CreatedAt, UpdatedAt FROM nugolo.Conversation WHERE ConversationId = @id;", id, ct)).SingleOrDefault();

    public async Task<IReadOnlyList<Conversation>> ListAsync(int take, CancellationToken ct = default) =>
        await Query($"SELECT TOP ({Math.Clamp(take, 1, 500)}) ConversationId, SourceName, Title, CreatedAt, UpdatedAt FROM nugolo.Conversation ORDER BY UpdatedAt DESC;", null, ct);

    public async Task<IReadOnlyList<ConversationEntry>> EntriesAsync(long conversationId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Seq, Kind, Text, ToolName, ToolInput, ToolResult, IsError, CreatedAt
            FROM nugolo.ConversationEntry WHERE ConversationId = @id ORDER BY Seq;
            """;
        cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, conversationId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<ConversationEntry>();
        while (await r.ReadAsync(ct))
            result.Add(new ConversationEntry(r.GetInt32(0), ParseKind(r.GetString(1)),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.GetBoolean(6), r.GetDateTimeOffset(7)));
        return result;
    }

    public async Task AppendAsync(long conversationId, IReadOnlyList<ConversationEntry> entries, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        int next;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT ISNULL(MAX(Seq), 0) + 1 FROM nugolo.ConversationEntry WITH (UPDLOCK) WHERE ConversationId = @id;";
            cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, conversationId));
            next = (int)(await cmd.ExecuteScalarAsync(ct))!;
        }

        foreach (var e in entries)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO nugolo.ConversationEntry (ConversationId, Seq, Kind, Text, ToolName, ToolInput, ToolResult, IsError, CreatedAt)
                VALUES (@id, @seq, @kind, @text, @tool, @input, @result, @error, @created);
                """;
            cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, conversationId));
            cmd.Parameters.Add(Param("@seq", SqlDbType.Int, next++));
            cmd.Parameters.Add(Param("@kind", SqlDbType.VarChar, Code(e.Kind)));
            cmd.Parameters.Add(Param("@text", SqlDbType.NVarChar, e.Text));
            cmd.Parameters.Add(Param("@tool", SqlDbType.VarChar, e.ToolName));
            cmd.Parameters.Add(Param("@input", SqlDbType.NVarChar, e.ToolInput));
            cmd.Parameters.Add(Param("@result", SqlDbType.NVarChar, e.ToolResult));
            cmd.Parameters.Add(Param("@error", SqlDbType.Bit, e.IsError));
            cmd.Parameters.Add(Param("@created", SqlDbType.DateTimeOffset, e.CreatedAt));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE nugolo.Conversation SET UpdatedAt = @now WHERE ConversationId = @id;";
            cmd.Parameters.Add(Param("@now", SqlDbType.DateTimeOffset, now));
            cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, conversationId));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task RenameAsync(long conversationId, string title, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE nugolo.Conversation SET Title = @title WHERE ConversationId = @id;";
        cmd.Parameters.Add(Param("@title", SqlDbType.NVarChar, title));
        cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, conversationId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<Conversation>> Query(string sql, long? id, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (id is { } value) cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, value));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<Conversation>();
        while (await r.ReadAsync(ct))
            result.Add(new Conversation(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDateTimeOffset(3), r.GetDateTimeOffset(4)));
        return result;
    }

    private static string Code(EntryKind kind) => kind switch
    {
        EntryKind.User => "user",
        EntryKind.Assistant => "assistant",
        EntryKind.ToolCall => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static EntryKind ParseKind(string code) => code switch
    {
        "user" => EntryKind.User,
        "assistant" => EntryKind.Assistant,
        "tool" => EntryKind.ToolCall,
        _ => throw new FormatException($"Tipo voce sconosciuto: {code}")
    };
}

public sealed class SqlSavedQueryStore(SqlConnectionFactory connections) : ISavedQueryStore
{
    private const string Columns = "QueryId, SourceName, Name, Description, SqlText, CreatedBy, ConversationId, CreatedAt";

    public async Task<long> SaveAsync(SavedQuery q, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nugolo.SavedQuery (SourceName, Name, Description, SqlText, CreatedBy, ConversationId, CreatedAt)
            OUTPUT INSERTED.QueryId VALUES (@source, @name, @description, @sql, @by, @conversation, @created);
            """;
        cmd.Parameters.Add(Param("@source", SqlDbType.NVarChar, q.SourceName));
        cmd.Parameters.Add(Param("@name", SqlDbType.NVarChar, q.Name));
        cmd.Parameters.Add(Param("@description", SqlDbType.NVarChar, q.Description));
        cmd.Parameters.Add(Param("@sql", SqlDbType.NVarChar, q.Sql));
        cmd.Parameters.Add(Param("@by", SqlDbType.VarChar, q.CreatedBy));
        cmd.Parameters.Add(Param("@conversation", SqlDbType.BigInt, q.ConversationId));
        cmd.Parameters.Add(Param("@created", SqlDbType.DateTimeOffset, q.CreatedAt));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<SavedQuery?> GetAsync(long id, CancellationToken ct = default) =>
        (await Query($"SELECT {Columns} FROM nugolo.SavedQuery WHERE QueryId = @p;", SqlDbType.BigInt, id, ct)).SingleOrDefault();

    public Task<IReadOnlyList<SavedQuery>> ListAsync(string? sourceName, CancellationToken ct = default) =>
        sourceName is null
            ? Query($"SELECT {Columns} FROM nugolo.SavedQuery ORDER BY QueryId DESC;", SqlDbType.Int, null, ct)
            : Query($"SELECT {Columns} FROM nugolo.SavedQuery WHERE SourceName = @p ORDER BY QueryId DESC;", SqlDbType.NVarChar, sourceName, ct);

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM nugolo.SavedQuery WHERE QueryId = @id;";
        cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, id));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<SavedQuery>> Query(string sql, SqlDbType type, object? value, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (value is not null) cmd.Parameters.Add(Param("@p", type, value));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<SavedQuery>();
        while (await r.ReadAsync(ct))
            result.Add(new SavedQuery(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetInt64(6), r.GetDateTimeOffset(7)));
        return result;
    }
}
