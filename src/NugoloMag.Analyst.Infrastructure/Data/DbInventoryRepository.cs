using System.Data;
using System.Data.Common;
using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Infrastructure.Data;

/// <summary>
/// Repository ADO.NET indipendente dal provider (SQL Server, SQLite, ...): riceve la DbProviderFactory.
/// La query deve restituire le colonne di <see cref="DefaultQuery"/> e usare i parametri @from e @to;
/// per adattarla al proprio ERP basta passare una query o una vista diversa.
/// </summary>
public sealed class DbInventoryRepository(DbProviderFactory factory, string connectionString, string? query = null) : IInventoryRepository
{
    public const string DefaultQuery = """
        SELECT StockDate, WarehouseCode, Sku, Category, OnHand, Inbound, Outbound, Adjustment, UnitCost
        FROM StockDaily
        WHERE StockDate BETWEEN @from AND @to
        ORDER BY WarehouseCode, Sku, StockDate
        """;

    public async Task<IReadOnlyList<StockDay>> LoadAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var connection = factory.CreateConnection() ?? throw new InvalidOperationException("Provider without connection support.");
        connection.ConnectionString = connectionString;
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = query ?? DefaultQuery;
        command.CommandTimeout = 300;
        AddParameter(command, "@from", from.ToDateTime(TimeOnly.MinValue));
        AddParameter(command, "@to", to.ToDateTime(TimeOnly.MinValue));

        var rows = new List<StockDay>();
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new StockDay(
                Date: DateOnly.FromDateTime(reader.GetDateTime(0)),
                Warehouse: new WarehouseCode(reader.GetString(1)),
                Sku: new Sku(reader.GetString(2)),
                Category: reader.IsDBNull(3) ? "N/D" : reader.GetString(3),
                OnHand: reader.GetDecimal(4),
                Inbound: reader.GetDecimal(5),
                Outbound: reader.GetDecimal(6),
                Adjustment: reader.GetDecimal(7),
                UnitCost: reader.IsDBNull(8) ? 0m : reader.GetDecimal(8)));
        }
        return rows;
    }

    private static void AddParameter(DbCommand command, string name, DateTime value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.DbType = DbType.Date;
        p.Value = value;
        command.Parameters.Add(p);
    }
}
