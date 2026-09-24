using System.Data;
using Microsoft.Data.SqlClient;
using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Domain.Discovery;
using NugoloMag.Analyst.Infrastructure.Data;

namespace NugoloMag.Analyst.Infrastructure.SqlServer;

/// <summary>
/// Database del gestionale su SQL Server, letto via ADO.NET. Solo SELECT: si consiglia un login con db_datareader.
/// Tutti gli identificatori arrivano dal catalogo e sono quotati; i valori sono parametri.
/// </summary>
public sealed class SqlServerSourceDatabase(string name, string connectionString, int commandTimeoutSeconds = 300) : ISourceDatabase
{
    public string Name => name;

    public async Task<DatabaseCatalog> ReadCatalogAsync(CancellationToken ct = default)
    {
        const string columnsSql = """
            SELECT s.name, o.name, CAST(CASE o.type WHEN 'V' THEN 1 ELSE 0 END AS bit),
                   (SELECT SUM(p.rows) FROM sys.partitions AS p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)),
                   c.name, TYPE_NAME(c.system_type_id), c.is_nullable,
                   CAST(CASE WHEN EXISTS (
                        SELECT 1 FROM sys.indexes AS i
                        JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                        WHERE i.object_id = o.object_id AND i.is_primary_key = 1 AND ic.column_id = c.column_id) THEN 1 ELSE 0 END AS bit)
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            JOIN sys.columns AS c ON c.object_id = o.object_id
            WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 AND s.name NOT IN (N'nugolo', N'sys', N'INFORMATION_SCHEMA')
            ORDER BY s.name, o.name, c.column_id;
            """;
        const string foreignKeysSql = """
            SELECT ps.name, pt.name, pc.name, rs.name, rt.name, rc.name
            FROM sys.foreign_key_columns AS f
            JOIN sys.objects AS pt ON pt.object_id = f.parent_object_id
            JOIN sys.schemas AS ps ON ps.schema_id = pt.schema_id
            JOIN sys.columns AS pc ON pc.object_id = f.parent_object_id AND pc.column_id = f.parent_column_id
            JOIN sys.objects AS rt ON rt.object_id = f.referenced_object_id
            JOIN sys.schemas AS rs ON rs.schema_id = rt.schema_id
            JOIN sys.columns AS rc ON rc.object_id = f.referenced_object_id AND rc.column_id = f.referenced_column_id;
            """;

        await using var connection = await OpenAsync(ct);

        string database;
        await using (var cmd = Command(connection, "SELECT DB_NAME();"))
            database = (string)(await cmd.ExecuteScalarAsync(ct))!;

        var tables = new List<CatalogTable>();
        await using (var cmd = Command(connection, columnsSql))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            TableName? current = null;
            bool isView = false;
            long? rows = null;
            var columns = new List<CatalogColumn>();

            while (await reader.ReadAsync(ct))
            {
                var table = new TableName(reader.GetString(0), reader.GetString(1));
                if (current != table)
                {
                    if (current is { } done) tables.Add(new CatalogTable(done, isView, rows, columns));
                    current = table;
                    isView = reader.GetBoolean(2);
                    rows = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                    columns = [];
                }
                columns.Add(new CatalogColumn(reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.GetBoolean(7)));
            }
            if (current is { } last) tables.Add(new CatalogTable(last, isView, rows, columns));
        }

        var foreignKeys = new List<ForeignKey>();
        await using (var cmd = Command(connection, foreignKeysSql))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                foreignKeys.Add(new ForeignKey(
                    new TableName(reader.GetString(0), reader.GetString(1)), reader.GetString(2),
                    new TableName(reader.GetString(3), reader.GetString(4)), reader.GetString(5)));
        }

        return new DatabaseCatalog(database, tables, foreignKeys);
    }

    public async Task<TableProfile> ProfileAsync(TableCandidate candidate, DateOnly today, CancellationToken ct = default)
    {
        var date = Id(candidate.ColumnFor(ColumnRole.Date)!);
        var item = Id(candidate.ColumnFor(ColumnRole.Item)!);
        var warehouse = candidate.ColumnFor(ColumnRole.Warehouse) is { } w ? $"COUNT_BIG(DISTINCT t.{Id(w)})" : "CAST(1 AS bigint)";
        var quantity = candidate.ColumnFor(ColumnRole.Quantity) ?? candidate.ColumnFor(ColumnRole.OnHand);
        var negative = quantity is { } q ? $"AVG(CASE WHEN t.{Id(q)} < 0 THEN 1.0 ELSE 0.0 END)" : "0.0";

        var sql = $"""
            SELECT COUNT_BIG(*),
                   MIN(CAST(t.{date} AS date)),
                   MAX(CAST(t.{date} AS date)),
                   COUNT_BIG(DISTINCT t.{item}),
                   {warehouse},
                   COUNT_BIG(DISTINCT CASE WHEN t.{date} >= @since THEN CAST(t.{date} AS date) END),
                   CAST(SUM(CASE WHEN t.{date} >= @since THEN 1 ELSE 0 END) AS bigint),
                   CAST({negative} AS float)
            FROM {candidate.Table.Quoted} AS t;
            """;

        await using var connection = await OpenAsync(ct);
        await using var cmd = Command(connection, sql);
        cmd.Parameters.Add(new SqlParameter("@since", SqlDbType.Date) { Value = today.AddDays(-30).ToDateTime(TimeOnly.MinValue) });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new TableProfile(
            Rows: reader.GetInt64(0),
            MinDate: reader.IsDBNull(1) ? null : DateOnly.FromDateTime(reader.GetDateTime(1)),
            MaxDate: reader.IsDBNull(2) ? null : DateOnly.FromDateTime(reader.GetDateTime(2)),
            DistinctItems: reader.GetInt64(3),
            DistinctWarehouses: reader.GetInt64(4),
            DistinctDatesLast30Days: reader.GetInt64(5),
            RowsLast30Days: reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
            NegativeQuantityShare: reader.IsDBNull(7) ? 0 : reader.GetDouble(7));
    }

    public async Task<IReadOnlyList<CodeFrequency>> TopValuesAsync(TableName table, string column, string? quantityColumn, CancellationToken ct = default)
    {
        var code = $"LTRIM(RTRIM(CAST(t.{Id(column)} AS nvarchar(50))))";
        var net = quantityColumn is { } q ? $"SUM(CAST(t.{Id(q)} AS float))" : "CAST(0 AS float)";
        var sql = $"""
            SELECT TOP (30) {code}, COUNT_BIG(*) AS Cnt, {net}
            FROM {table.Quoted} AS t
            WHERE t.{Id(column)} IS NOT NULL
            GROUP BY {code}
            ORDER BY Cnt DESC;
            """;

        await using var connection = await OpenAsync(ct);
        await using var cmd = Command(connection, sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<CodeFrequency>();
        while (await reader.ReadAsync(ct))
            result.Add(new CodeFrequency(reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? 0 : reader.GetDouble(2)));
        return result;
    }

    public string BuildInventoryQuery(SourceMapping mapping) => InventoryQueryBuilder.Build(mapping);

    public IInventoryRepository Inventory(string query) => new DbInventoryRepository(SqlClientFactory.Instance, connectionString, query);

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private SqlCommand Command(SqlConnection connection, string sql) =>
        new(sql, connection) { CommandTimeout = commandTimeoutSeconds };

    private static string Id(string identifier) => TableName.QuoteIdentifier(identifier);
}

public sealed class SqlServerSourceRegistry(IReadOnlyDictionary<string, string> connectionStrings) : ISourceRegistry
{
    public IReadOnlyList<string> Names => connectionStrings.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    public ISourceDatabase Get(string name) =>
        connectionStrings.TryGetValue(name, out var cs)
            ? new SqlServerSourceDatabase(name, cs)
            : throw new KeyNotFoundException($"Sorgente '{name}' non configurata.");
}
