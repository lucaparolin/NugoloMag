using System.Data;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Domain.Discovery;
using NugoloMag.Analyst.Infrastructure.Serialization;
using static NugoloMag.Analyst.Infrastructure.Store.SqlConnectionFactory;

namespace NugoloMag.Analyst.Infrastructure.Store;

public sealed class SqlDiscoveryStore(SqlConnectionFactory connections) : IDiscoveryStore
{
    public async Task<long> SaveAsync(DiscoveryReport report, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nugolo.Discovery (SourceName, DatabaseName, CreatedAt, Readiness, ReportJson)
            OUTPUT INSERTED.DiscoveryId
            VALUES (@source, @db, @created, @readiness, N'{}');
            """;
        cmd.Parameters.Add(Param("@source", SqlDbType.NVarChar, report.SourceName));
        cmd.Parameters.Add(Param("@db", SqlDbType.NVarChar, report.Database));
        cmd.Parameters.Add(Param("@created", SqlDbType.DateTimeOffset, report.CreatedAt));
        cmd.Parameters.Add(Param("@readiness", SqlDbType.VarChar, StoreCodes.Code(report.Readiness)));
        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;

        // L'Id fa parte del documento: si scrive dopo averlo ottenuto.
        await UpdateAsync(report with { Id = id }, ct);
        return id;
    }

    public async Task UpdateAsync(DiscoveryReport report, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE nugolo.Discovery SET Readiness = @readiness, ReportJson = @json WHERE DiscoveryId = @id;";
        cmd.Parameters.Add(Param("@readiness", SqlDbType.VarChar, StoreCodes.Code(report.Readiness)));
        cmd.Parameters.Add(Param("@json", SqlDbType.NVarChar, NugoloJson.Serialize(report)));
        cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, report.Id));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException($"Analisi {report.Id} non trovata.");
    }

    public async Task<DiscoveryReport?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ReportJson FROM nugolo.Discovery WHERE DiscoveryId = @id;";
        cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, id));
        return await cmd.ExecuteScalarAsync(ct) is string json ? NugoloJson.ReadDiscovery(json) with { Id = id } : null;
    }

    public async Task<IReadOnlyList<DiscoverySummary>> ListAsync(int take, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT TOP (@take) DiscoveryId, SourceName, DatabaseName, CreatedAt, Readiness FROM nugolo.Discovery ORDER BY DiscoveryId DESC;";
        cmd.Parameters.Add(Param("@take", SqlDbType.Int, take));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<DiscoverySummary>();
        while (await reader.ReadAsync(ct))
            result.Add(new DiscoverySummary(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetDateTimeOffset(3), StoreCodes.ParseReadiness(reader.GetString(4))));
        return result;
    }
}
