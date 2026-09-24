using System.Data;
using System.Data.Common;
using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Infrastructure.Data;

/// <summary>Missioni di magazzino via ADO.NET (provider-agnostico). Colonne: TaskId, WarehouseCode, Activity, Zone, OrderRef, Sku, Location, Quantity, StartedAt, EndedAt.</summary>
public sealed class DbTaskRepository(DbProviderFactory factory, string connectionString, string query) : ITaskRepository
{
    public async Task<IReadOnlyList<WarehouseTask>> LoadAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var connection = factory.CreateConnection() ?? throw new InvalidOperationException("Provider without connection support.");
        connection.ConnectionString = connectionString;
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = 300;
        foreach (var (name, value) in new[] { ("@from", from), ("@to", to) })
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.DbType = DbType.Date;
            p.Value = value.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add(p);
        }

        var tasks = new List<WarehouseTask>();
        await using var r = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await r.ReadAsync(ct))
        {
            // Con SequentialAccess ogni colonna si legge una sola volta e in ordine.
            var taskId = r.GetString(0);
            var warehouse = new WarehouseCode(r.GetString(1));
            var activity = r.GetString(2).ToUpperInvariant();
            var zone = r.GetString(3);
            var orderRef = r.IsDBNull(4) ? null : r.GetString(4);
            var sku = r.IsDBNull(5) ? null : r.GetString(5);
            var location = r.IsDBNull(6) ? null : r.GetString(6);
            tasks.Add(new WarehouseTask(
                TaskId: taskId,
                Warehouse: warehouse,
                Activity: activity,
                Zone: zone,
                OrderRef: orderRef,
                Sku: string.IsNullOrEmpty(sku) ? null : new Sku(sku),
                Location: location,
                Quantity: r.GetDecimal(7),
                StartedAt: r.GetDateTime(8),
                EndedAt: r.GetDateTime(9)));
        }
        return tasks;
    }
}
