using System.Globalization;
using System.Text;
using System.Text.Json;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Domain.Assistant;
using NugoloMag.Analyst.Domain.Discovery;

namespace NugoloMag.Analyst.Application.Assistant;

/// <summary>
/// Strumenti dell'assistente dati: esplorare lo schema, guardare esempi, eseguire query in sola lettura,
/// salvare le query utili. L'input arriva come JSON e si legge con JsonElement (nessuna deserializzazione via reflection).
/// </summary>
public sealed class DataAgentToolbox(
    ISourceDatabase source,
    DatabaseCatalog catalog,
    ISavedQueryStore savedQueries,
    long conversationId,
    TimeProvider clock) : IDataAgentToolbox
{
    public const int MaxRowsForModel = 200;
    private const int MaxCellLength = 80;
    private const int MaxResultChars = 24_000;

    public IReadOnlyList<ToolSpec> Specs { get; } =
    [
        new("list_tables",
            "Elenca tabelle e viste del database con numero di righe. Usa 'filter' per cercare per nome (es. 'mov', 'art').",
            """{"type":"object","properties":{"filter":{"type":"string","description":"Parte del nome da cercare (facoltativo)"}},"additionalProperties":false}"""),
        new("describe_table",
            "Colonne (nome, tipo, chiave primaria) e relazioni (foreign key) di una tabella. Nome nel formato schema.tabella.",
            """{"type":"object","properties":{"table":{"type":"string","description":"es. dbo.MovMag"}},"required":["table"],"additionalProperties":false}"""),
        new("sample_rows",
            "Mostra alcune righe di esempio di una tabella per capire i valori reali (codici, formati, date).",
            """{"type":"object","properties":{"table":{"type":"string"},"rows":{"type":"integer","minimum":1,"maximum":20}},"required":["table"],"additionalProperties":false}"""),
        new("run_query",
            "Esegue una query T-SQL in sola lettura (una sola SELECT o WITH…SELECT; niente INTO, EXEC, SET, DECLARE, scritture). " +
            "Restituisce al massimo 200 righe: aggrega in SQL (GROUP BY, TOP, SUM) invece di scaricare dati grezzi. " +
            "Quota gli identificatori con [] se sono parole riservate. Indica sempre lo scopo.",
            """{"type":"object","properties":{"sql":{"type":"string"},"purpose":{"type":"string","description":"Cosa si vuole scoprire con questa query"}},"required":["sql","purpose"],"additionalProperties":false}"""),
        new("save_query",
            "Salva una query di analisi utile e verificata, così l'utente può rieseguirla dalla pagina 'Query salvate'. " +
            "Salvala solo dopo averla eseguita con successo e, se non è ovvio, dopo che l'utente ha confermato che risponde alla sua domanda.",
            """{"type":"object","properties":{"name":{"type":"string","maxLength":200},"description":{"type":"string","maxLength":1000},"sql":{"type":"string"}},"required":["name","description","sql"],"additionalProperties":false}"""),
        new("list_saved_queries",
            "Elenca le query già salvate per questa sorgente (per riusarle o non duplicarle).",
            """{"type":"object","properties":{},"additionalProperties":false}""")
    ];

    public async Task<ToolOutcome> ExecuteAsync(string name, JsonElement input, CancellationToken ct = default)
    {
        try
        {
            return name switch
            {
                "list_tables" => ListTables(OptionalString(input, "filter")),
                "describe_table" => DescribeTable(RequiredString(input, "table")),
                "sample_rows" => await SampleAsync(RequiredString(input, "table"), OptionalInt(input, "rows") ?? 10, ct),
                "run_query" => await RunQueryAsync(RequiredString(input, "sql"), ct),
                "save_query" => await SaveAsync(RequiredString(input, "name"), RequiredString(input, "description"), RequiredString(input, "sql"), ct),
                "list_saved_queries" => await ListSavedAsync(ct),
                _ => new ToolOutcome($"Strumento sconosciuto: {name}", true)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // L'errore torna all'LLM, che può correggere la query e riprovare.
            return new ToolOutcome($"Errore: {ex.Message}", true);
        }
    }

    private ToolOutcome ListTables(string? filter)
    {
        var tables = catalog.Tables
            .Where(t => filter is null || t.Name.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.RowCount ?? 0)
            .Take(200)
            .Select(t => $"{t.Name}{(t.IsView ? " (vista)" : "")}\t{(t.RowCount is { } r ? r.ToString("N0", CultureInfo.InvariantCulture) + " righe" : "")}\t{t.Columns.Count} colonne");
        var lines = tables.ToList();
        return new ToolOutcome(lines.Count == 0 ? "Nessuna tabella trovata." : string.Join('\n', lines), false);
    }

    private ToolOutcome DescribeTable(string table)
    {
        var t = Find(table);
        var sb = new StringBuilder($"{t.Name}{(t.IsView ? " (vista)" : "")}, {t.RowCount?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"} righe\n");
        foreach (var c in t.Columns)
            sb.AppendLine($"- {c.Name} {c.SqlType}{(c.IsPrimaryKey ? " PK" : "")}{(c.IsNullable ? " null" : "")}");
        foreach (var fk in catalog.ForeignKeys.Where(f => f.From == t.Name))
            sb.AppendLine($"FK {fk.FromColumn} → {fk.To}.{fk.ToColumn}");
        foreach (var fk in catalog.ForeignKeys.Where(f => f.To == t.Name))
            sb.AppendLine($"Referenziata da {fk.From}.{fk.FromColumn}");
        return new ToolOutcome(sb.ToString(), false);
    }

    private async Task<ToolOutcome> SampleAsync(string table, int rows, CancellationToken ct)
    {
        var t = Find(table);
        var result = await source.QueryAsync($"SELECT TOP ({Math.Clamp(rows, 1, 20)}) * FROM {t.Name.Quoted}", 20, ct);
        return new ToolOutcome(Format(result), false);
    }

    private async Task<ToolOutcome> RunQueryAsync(string sql, CancellationToken ct)
    {
        var guard = ReadOnlySqlGuard.Check(sql);
        if (!guard.IsAllowed) return new ToolOutcome($"Query rifiutata: {guard.Reason}", true);
        var result = await source.QueryAsync(sql, MaxRowsForModel, ct);
        return new ToolOutcome(Format(result), false);
    }

    private async Task<ToolOutcome> SaveAsync(string name, string description, string sql, CancellationToken ct)
    {
        var guard = ReadOnlySqlGuard.Check(sql);
        if (!guard.IsAllowed) return new ToolOutcome($"Query non salvata: {guard.Reason}", true);
        if (name.Length > 200 || description.Length > 1000) return new ToolOutcome("Nome o descrizione troppo lunghi.", true);

        var test = await source.QueryAsync(sql, 1, ct); // deve almeno funzionare
        var id = await savedQueries.SaveAsync(new SavedQuery(0, source.Name, name.Trim(), description.Trim(), sql.Trim(), "assistente", conversationId, clock.GetUtcNow()), ct);
        return new ToolOutcome($"Query salvata con id {id} ({test.Columns.Count} colonne). L'utente la trova in 'Query salvate'.", false);
    }

    private async Task<ToolOutcome> ListSavedAsync(CancellationToken ct)
    {
        var saved = await savedQueries.ListAsync(source.Name, ct);
        return new ToolOutcome(saved.Count == 0
            ? "Nessuna query salvata."
            : string.Join('\n', saved.Select(q => $"#{q.Id} {q.Name}: {q.Description}")), false);
    }

    private CatalogTable Find(string table) =>
        catalog.Find(TableName.Parse(table.Trim().Replace("[", "").Replace("]", "")))
        ?? throw new InvalidOperationException($"Tabella {table} non trovata. Usa list_tables per i nomi esatti.");

    /// <summary>Tabella in testo compatto (TSV) per l'LLM, con celle e dimensione totale limitate.</summary>
    public static string Format(QueryResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join('\t', result.Columns));
        foreach (var row in result.Rows)
        {
            sb.AppendLine(string.Join('\t', row.Select(v => v.Length > MaxCellLength ? v[..MaxCellLength] + "…" : v)));
            if (sb.Length > MaxResultChars) { sb.AppendLine("… (output troncato)"); break; }
        }
        sb.Append($"{result.Rows.Count} righe{(result.Truncated ? $" (limite {result.Rows.Count} raggiunto: aggregare o filtrare)" : "")}, {result.Seconds:0.00}s");
        return sb.ToString();
    }

    private static string RequiredString(JsonElement input, string property) =>
        OptionalString(input, property) ?? throw new ArgumentException($"Parametro '{property}' obbligatorio.");

    private static string? OptionalString(JsonElement input, string property) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static int? OptionalInt(JsonElement input, string property) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
