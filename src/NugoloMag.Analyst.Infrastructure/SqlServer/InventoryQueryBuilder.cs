using System.Text;
using NugoloMag.Analyst.Domain.Discovery;

namespace NugoloMag.Analyst.Infrastructure.SqlServer;

/// <summary>
/// Genera la query T-SQL che trasforma i movimenti del gestionale nelle posizioni giornaliere attese dall'analista.
/// Gli identificatori vengono solo da un <see cref="SourceMapping"/> già validato sul catalogo e sono sempre quotati;
/// le causali sono letterali con escape. Parametri: @from, @to.
///
/// Logica: movimenti → saldo di apertura (tutto ciò che precede @from) + movimenti giornalieri →
/// griglia densa giorni × (magazzino, articolo) → giacenza come somma progressiva (o dai saldi storici, se presenti).
/// </summary>
public static class InventoryQueryBuilder
{
    public static string Build(SourceMapping mapping)
    {
        var m = mapping.Movements;
        var q = m.QuantityColumn is { } qc ? $"CAST(ISNULL(m.{Id(qc)}, 0) AS decimal(18,3))" : "0";
        var (inExpr, outExpr, adjExpr) = m.Direction switch
        {
            MovementDirection.SeparateColumns => (
                $"CAST(ISNULL(m.{Id(m.InboundColumn!)}, 0) AS decimal(18,3))",
                $"CAST(ISNULL(m.{Id(m.OutboundColumn!)}, 0) AS decimal(18,3))",
                "CAST(0 AS decimal(18,3))"),
            MovementDirection.TypeCode => (
                $"CASE WHEN {InList(m.TypeColumn!, m.InboundCodes)} THEN ABS({q}) ELSE 0 END",
                $"CASE WHEN {InList(m.TypeColumn!, m.OutboundCodes)} THEN ABS({q}) ELSE 0 END",
                $"CASE WHEN {InList(m.TypeColumn!, m.AdjustmentCodes)} THEN {q} ELSE 0 END"),
            _ => (
                $"CASE WHEN {q} > 0 THEN {q} ELSE 0 END",
                $"CASE WHEN {q} < 0 THEN -{q} ELSE 0 END",
                "CAST(0 AS decimal(18,3))")
        };

        var warehouse = m.WarehouseColumn is { } wc ? Code($"m.{Id(wc)}") : Literal(mapping.DefaultWarehouse);
        var sb = new StringBuilder();
        sb.AppendLine($"""
            WITH mov AS (
                SELECT CAST(m.{Id(m.DateColumn)} AS date) AS D,
                       {warehouse} AS W,
                       {Code($"m.{Id(m.ItemColumn)}")} AS S,
                       {inExpr} AS I,
                       {outExpr} AS O,
                       {adjExpr} AS A
                FROM {m.Table.Quoted} AS m
                WHERE m.{Id(m.DateColumn)} < DATEADD(day, 1, CAST(@to AS date))
            ),
            opening AS (
                SELECT W, S, SUM(I - O + A) AS Q FROM mov WHERE D < CAST(@from AS date) GROUP BY W, S
            ),
            daily AS (
                SELECT D, W, S, SUM(I) AS I, SUM(O) AS O, SUM(A) AS A FROM mov WHERE D >= CAST(@from AS date) GROUP BY D, W, S
            ),
            days AS (
                SELECT CAST(@from AS date) AS D
                UNION ALL
                SELECT DATEADD(day, 1, D) FROM days WHERE D < CAST(@to AS date)
            ),
            pairs AS (
                SELECT W, S FROM opening WHERE Q <> 0
                UNION
                SELECT W, S FROM daily
            ),
            grid AS (
                SELECT d.D, p.W, p.S,
                       ISNULL(x.I, 0) AS I, ISNULL(x.O, 0) AS O, ISNULL(x.A, 0) AS A,
                       ISNULL(o.Q, 0) + SUM(ISNULL(x.I, 0) - ISNULL(x.O, 0) + ISNULL(x.A, 0))
                           OVER (PARTITION BY p.W, p.S ORDER BY d.D ROWS UNBOUNDED PRECEDING) AS Q
                FROM days AS d
                CROSS JOIN pairs AS p
                LEFT JOIN daily AS x ON x.D = d.D AND x.W = p.W AND x.S = p.S
                LEFT JOIN opening AS o ON o.W = p.W AND o.S = p.S
            )
            SELECT g.D AS StockDate,
                   g.W AS WarehouseCode,
                   g.S AS Sku,
                   {Category(mapping)} AS Category,
                   CAST({OnHand(mapping)} AS decimal(18,3)) AS OnHand,
                   CAST(g.I AS decimal(18,3)) AS Inbound,
                   CAST(g.O AS decimal(18,3)) AS Outbound,
                   CAST(g.A AS decimal(18,3)) AS Adjustment,
                   CAST({Cost(mapping)} AS decimal(18,4)) AS UnitCost
            FROM grid AS g
            """);

        if (mapping.Items is { } items)
        {
            var selected = new List<string> { "1 AS Found" };
            if (items.CategoryColumn is { } cat) selected.Add($"it.{Id(cat)} AS C");
            if (items.CostColumn is { } cost) selected.Add($"it.{Id(cost)} AS K");
            sb.AppendLine($"""
                OUTER APPLY (
                    SELECT TOP (1) {string.Join(", ", selected)}
                    FROM {items.Table.Quoted} AS it
                    WHERE {Code($"it.{Id(items.KeyColumn)}")} = g.S
                ) AS item
                """);
        }

        if (mapping.Snapshot is { } snap)
        {
            var whFilter = snap.WarehouseColumn is { } sw ? $" AND {Code($"sn.{Id(sw)}")} = g.W" : "";
            sb.AppendLine($"""
                OUTER APPLY (
                    SELECT TOP (1) sn.{Id(snap.OnHandColumn)} AS Q
                    FROM {snap.Table.Quoted} AS sn
                    WHERE {Code($"sn.{Id(snap.ItemColumn)}")} = g.S{whFilter}
                      AND sn.{Id(snap.DateColumn)} < DATEADD(day, 1, g.D)
                    ORDER BY sn.{Id(snap.DateColumn)} DESC
                ) AS snap
                """);
        }

        sb.Append("OPTION (MAXRECURSION 0);");
        return sb.ToString();
    }

    private static string Category(SourceMapping mapping) =>
        mapping.Items?.CategoryColumn is not null ? "ISNULL(NULLIF(LTRIM(RTRIM(CAST(item.C AS nvarchar(100)))), N''), N'N/D')" : "N'N/D'";

    private static string Cost(SourceMapping mapping) =>
        mapping.Items?.CostColumn is not null ? "ISNULL(item.K, 0)" : "0";

    private static string OnHand(SourceMapping mapping) =>
        mapping.Snapshot is not null ? "COALESCE(snap.Q, g.Q)" : "g.Q";

    /// <summary>Codici come testo normalizzato: stessi valori da tabelle diverse (int, char con spazi) si confrontano correttamente.</summary>
    private static string Code(string expression) => $"LTRIM(RTRIM(CAST({expression} AS nvarchar(50))))";

    private static string Id(string identifier) => TableName.QuoteIdentifier(identifier);

    private static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";

    private static string InList(string typeColumn, IReadOnlyList<string> codes) =>
        codes.Count == 0 ? "1 = 0" : $"{Code($"m.{Id(typeColumn)}")} IN ({string.Join(", ", codes.Select(Literal))})";
}
