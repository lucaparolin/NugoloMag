using NugoloMag.Analyst.Domain.Discovery;

namespace NugoloMag.Analyst.Infrastructure.SqlServer;

/// <summary>Query T-SQL delle missioni di magazzino (colonne attese da <c>DbTaskRepository</c>; parametri @from, @to).</summary>
public static class TaskQueryBuilder
{
    public static string? Build(SourceMapping mapping)
    {
        if (mapping.Tasks is not { } t) return null;

        string Code(string? column, string fallback) =>
            column is null ? Literal(fallback) : $"LTRIM(RTRIM(CAST(k.{Id(column)} AS nvarchar(50))))";
        string Nullable(string? column) =>
            column is null ? "CAST(NULL AS nvarchar(50))" : $"LTRIM(RTRIM(CAST(k.{Id(column)} AS nvarchar(50))))";

        var warehouse = Code(t.WarehouseColumn, mapping.DefaultWarehouse);
        return $"""
            SELECT CAST(ROW_NUMBER() OVER (ORDER BY k.{Id(t.StartColumn)}) AS nvarchar(50)) AS TaskId,
                   {warehouse} AS WarehouseCode,
                   {Code(t.ActivityColumn, "PICK")} AS Activity,
                   {Code(t.ZoneColumn, "-")} AS Zone,
                   {Nullable(t.OrderColumn)} AS OrderRef,
                   {Nullable(t.ItemColumn)} AS Sku,
                   {Nullable(t.LocationColumn)} AS Location,
                   CAST({(t.QuantityColumn is { } q ? $"ISNULL(k.{Id(q)}, 0)" : "1")} AS decimal(18,3)) AS Quantity,
                   CAST(k.{Id(t.StartColumn)} AS datetime2(0)) AS StartedAt,
                   CAST(k.{Id(t.EndColumn)} AS datetime2(0)) AS EndedAt
            FROM {t.Table.Quoted} AS k
            WHERE k.{Id(t.StartColumn)} >= CAST(@from AS date)
              AND k.{Id(t.StartColumn)} < DATEADD(day, 1, CAST(@to AS date))
              AND k.{Id(t.EndColumn)} IS NOT NULL;
            """;
    }

    private static string Id(string identifier) => TableName.QuoteIdentifier(identifier);
    private static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";
}
