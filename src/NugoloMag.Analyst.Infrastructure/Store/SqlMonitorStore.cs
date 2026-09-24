using System.Data;
using Microsoft.Data.SqlClient;
using NugoloMag.Analyst.Application.Monitoring;
using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Domain.Monitoring;
using NugoloMag.Analyst.Infrastructure.Reports;
using NugoloMag.Analyst.Infrastructure.Serialization;
using static NugoloMag.Analyst.Infrastructure.Store.SqlConnectionFactory;

namespace NugoloMag.Analyst.Infrastructure.Store;

/// <summary>Lettura del report salvato di un'esecuzione, per le viste.</summary>
public interface IRunReportQuery
{
    Task<ReportDocument?> GetReportAsync(long runId, CancellationToken ct = default);
}

public sealed class SqlMonitorStore(SqlConnectionFactory connections) : IMonitorStore, IRunReportQuery
{
    private const string MonitorColumns =
        "MonitorId, Name, SourceName, DiscoveryId, MappingJson, SourceQuery, RunAt, BaselineDays, RecentDays, IsActive, CreatedAt, NextRunAt";
    private const string RunColumns =
        "RunId, MonitorId, AsOf, StartedAt, CompletedAt, Status, RowsAnalyzed, FindingsCount, HighCount, Narrative, Error";

    public async Task<long> CreateAsync(MonitorDefinition m, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nugolo.Monitor (Name, SourceName, DiscoveryId, MappingJson, SourceQuery, RunAt, BaselineDays, RecentDays, IsActive, CreatedAt, NextRunAt)
            OUTPUT INSERTED.MonitorId
            VALUES (@name, @source, @discovery, @mapping, @query, @runAt, @baseline, @recent, @active, @created, @next);
            """;
        cmd.Parameters.Add(Param("@name", SqlDbType.NVarChar, m.Name));
        cmd.Parameters.Add(Param("@source", SqlDbType.NVarChar, m.SourceName));
        cmd.Parameters.Add(Param("@discovery", SqlDbType.BigInt, m.DiscoveryId));
        cmd.Parameters.Add(Param("@mapping", SqlDbType.NVarChar, NugoloJson.Serialize(m.Mapping)));
        cmd.Parameters.Add(Param("@query", SqlDbType.NVarChar, m.SourceQuery));
        cmd.Parameters.Add(Param("@runAt", SqlDbType.Time, m.RunAt.ToTimeSpan()));
        cmd.Parameters.Add(Param("@baseline", SqlDbType.Int, m.BaselineDays));
        cmd.Parameters.Add(Param("@recent", SqlDbType.Int, m.RecentDays));
        cmd.Parameters.Add(Param("@active", SqlDbType.Bit, m.IsActive));
        cmd.Parameters.Add(Param("@created", SqlDbType.DateTimeOffset, m.CreatedAt));
        cmd.Parameters.Add(Param("@next", SqlDbType.DateTimeOffset, m.NextRunAt));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<MonitorDefinition?> GetAsync(long id, CancellationToken ct = default) =>
        (await QueryMonitors($"SELECT {MonitorColumns} FROM nugolo.Monitor WHERE MonitorId = @id;",
            c => c.Parameters.Add(Param("@id", SqlDbType.BigInt, id)), ct)).SingleOrDefault();

    public Task<IReadOnlyList<MonitorDefinition>> ListAsync(CancellationToken ct = default) =>
        QueryMonitors($"SELECT {MonitorColumns} FROM nugolo.Monitor ORDER BY Name;", _ => { }, ct);

    public Task<IReadOnlyList<MonitorDefinition>> ListDueAsync(DateTimeOffset now, CancellationToken ct = default) =>
        QueryMonitors($"SELECT {MonitorColumns} FROM nugolo.Monitor WHERE IsActive = 1 AND NextRunAt <= @now ORDER BY NextRunAt;",
            c => c.Parameters.Add(Param("@now", SqlDbType.DateTimeOffset, now)), ct);

    public async Task SetScheduleAsync(long id, bool isActive, DateTimeOffset? nextRunAt, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE nugolo.Monitor SET IsActive = @active, NextRunAt = @next WHERE MonitorId = @id;";
        cmd.Parameters.Add(Param("@active", SqlDbType.Bit, isActive));
        cmd.Parameters.Add(Param("@next", SqlDbType.DateTimeOffset, nextRunAt));
        cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, id));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> StartRunAsync(MonitorRun run, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nugolo.MonitorRun (MonitorId, AsOf, StartedAt, Status)
            OUTPUT INSERTED.RunId
            VALUES (@monitor, @asOf, @started, @status);
            """;
        cmd.Parameters.Add(Param("@monitor", SqlDbType.BigInt, run.MonitorId));
        cmd.Parameters.Add(Param("@asOf", SqlDbType.Date, run.AsOf.ToDateTime(TimeOnly.MinValue)));
        cmd.Parameters.Add(Param("@started", SqlDbType.DateTimeOffset, run.StartedAt));
        cmd.Parameters.Add(Param("@status", SqlDbType.VarChar, StoreCodes.Code(run.Status)));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task CompleteRunAsync(long runId, AnalysisReport report, DateTimeOffset completedAt, CancellationToken ct = default)
    {
        var document = ReportDocument.From(report);
        await using var connection = await connections.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE nugolo.MonitorRun
                SET CompletedAt = @completed, Status = @status, RowsAnalyzed = @rows, FindingsCount = @findings,
                    HighCount = @high, Narrative = @narrative, ReportJson = @json
                WHERE RunId = @id;
                """;
            cmd.Parameters.Add(Param("@completed", SqlDbType.DateTimeOffset, completedAt));
            cmd.Parameters.Add(Param("@status", SqlDbType.VarChar, StoreCodes.Code(RunStatus.Succeeded)));
            cmd.Parameters.Add(Param("@rows", SqlDbType.Int, report.RowsAnalyzed));
            cmd.Parameters.Add(Param("@findings", SqlDbType.Int, report.Findings.Count));
            cmd.Parameters.Add(Param("@high", SqlDbType.Int, report.Findings.Count(f => f.Severity == Severity.High)));
            cmd.Parameters.Add(Param("@narrative", SqlDbType.NVarChar, report.Narrative));
            cmd.Parameters.Add(Param("@json", SqlDbType.NVarChar, NugoloJson.Serialize(document)));
            cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, runId));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var f in document.Findings)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO nugolo.Finding (RunId, [Rank], Kind, Severity, Magnitude, Subject, Metric, Headline)
                VALUES (@run, @rank, @kind, @severity, @magnitude, @subject, @metric, @headline);
                """;
            cmd.Parameters.Add(Param("@run", SqlDbType.BigInt, runId));
            cmd.Parameters.Add(Param("@rank", SqlDbType.Int, f.Rank));
            cmd.Parameters.Add(Param("@kind", SqlDbType.VarChar, f.Kind));
            cmd.Parameters.Add(Param("@severity", SqlDbType.VarChar, f.Severity));
            cmd.Parameters.Add(Param("@magnitude", SqlDbType.Float, f.Magnitude));
            cmd.Parameters.Add(Param("@subject", SqlDbType.NVarChar, Truncate(f.Subject, 120)));
            cmd.Parameters.Add(Param("@metric", SqlDbType.VarChar, f.Metric));
            cmd.Parameters.Add(Param("@headline", SqlDbType.NVarChar, Truncate(f.Headline, 500)));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task FailRunAsync(long runId, string error, DateTimeOffset completedAt, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE nugolo.MonitorRun SET CompletedAt = @completed, Status = @status, Error = @error WHERE RunId = @id;";
        cmd.Parameters.Add(Param("@completed", SqlDbType.DateTimeOffset, completedAt));
        cmd.Parameters.Add(Param("@status", SqlDbType.VarChar, StoreCodes.Code(RunStatus.Failed)));
        cmd.Parameters.Add(Param("@error", SqlDbType.NVarChar, error));
        cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, runId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<MonitorRun>> ListRunsAsync(long monitorId, int take, CancellationToken ct = default) =>
        QueryRuns($"SELECT TOP (@take) {RunColumns} FROM nugolo.MonitorRun WHERE MonitorId = @monitor ORDER BY RunId DESC;", c =>
        {
            c.Parameters.Add(Param("@take", SqlDbType.Int, take));
            c.Parameters.Add(Param("@monitor", SqlDbType.BigInt, monitorId));
        }, ct);

    public async Task<MonitorRun?> GetRunAsync(long runId, CancellationToken ct = default) =>
        (await QueryRuns($"SELECT {RunColumns} FROM nugolo.MonitorRun WHERE RunId = @id;",
            c => c.Parameters.Add(Param("@id", SqlDbType.BigInt, runId)), ct)).SingleOrDefault();

    public async Task<ReportDocument?> GetReportAsync(long runId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ReportJson FROM nugolo.MonitorRun WHERE RunId = @id;";
        cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, runId));
        return await cmd.ExecuteScalarAsync(ct) is string json ? NugoloJson.ReadReport(json) : null;
    }

    private async Task<IReadOnlyList<MonitorDefinition>> QueryMonitors(string sql, Action<SqlCommand> parameters, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        parameters(cmd);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<MonitorDefinition>();
        while (await r.ReadAsync(ct))
        {
            result.Add(new MonitorDefinition
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                SourceName = r.GetString(2),
                DiscoveryId = r.GetInt64(3),
                Mapping = NugoloJson.ReadMapping(r.GetString(4)),
                SourceQuery = r.GetString(5),
                RunAt = TimeOnly.FromTimeSpan(r.GetTimeSpan(6)),
                BaselineDays = r.GetInt32(7),
                RecentDays = r.GetInt32(8),
                IsActive = r.GetBoolean(9),
                CreatedAt = r.GetDateTimeOffset(10),
                NextRunAt = r.IsDBNull(11) ? null : r.GetDateTimeOffset(11)
            });
        }
        return result;
    }

    private async Task<IReadOnlyList<MonitorRun>> QueryRuns(string sql, Action<SqlCommand> parameters, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        parameters(cmd);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<MonitorRun>();
        while (await r.ReadAsync(ct))
        {
            result.Add(new MonitorRun
            {
                Id = r.GetInt64(0),
                MonitorId = r.GetInt64(1),
                AsOf = DateOnly.FromDateTime(r.GetDateTime(2)),
                StartedAt = r.GetDateTimeOffset(3),
                CompletedAt = r.IsDBNull(4) ? null : r.GetDateTimeOffset(4),
                Status = StoreCodes.ParseRunStatus(r.GetString(5)),
                RowsAnalyzed = r.GetInt32(6),
                FindingsCount = r.GetInt32(7),
                HighCount = r.GetInt32(8),
                Narrative = r.IsDBNull(9) ? null : r.GetString(9),
                Error = r.IsDBNull(10) ? null : r.GetString(10)
            });
        }
        return result;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
