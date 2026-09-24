using System.Data;
using NugoloMag.Analyst.Application.Agentic;
using NugoloMag.Analyst.Domain.Agentic;
using NugoloMag.Analyst.Infrastructure.Serialization;
using static NugoloMag.Analyst.Infrastructure.Store.SqlConnectionFactory;

namespace NugoloMag.Analyst.Infrastructure.Store;

/// <summary>Codici testuali stabili per le colonne interrogabili (mappatura esplicita).</summary>
internal static class AgenticCodes
{
    public static string Code(IncidentStatus s) => s switch
    {
        IncidentStatus.Open => "open", IncidentStatus.Worsening => "worsening", IncidentStatus.Improving => "improving", _ => "resolved"
    };

    public static string Code(SeverityLevel s) => s switch
    {
        SeverityLevel.Informational => "informational", SeverityLevel.Low => "low", SeverityLevel.Medium => "medium", SeverityLevel.High => "high", _ => "critical"
    };

    public static string Code(ConfidenceLevel c) => c switch { ConfidenceLevel.High => "high", ConfidenceLevel.Medium => "medium", _ => "low" };

    public static string Code(VerificationStatus v) => v switch
    {
        VerificationStatus.Pending => "pending", VerificationStatus.Effective => "effective", VerificationStatus.Ineffective => "ineffective", _ => "n/a"
    };

    public static string Code(OperationalDomain d) => d switch
    {
        OperationalDomain.Inventory => "inventory", OperationalDomain.Inbound => "inbound", OperationalDomain.Outbound => "outbound",
        OperationalDomain.Productivity => "productivity", OperationalDomain.Capacity => "capacity", OperationalDomain.Quality => "quality",
        OperationalDomain.Flow => "flow", OperationalDomain.Forecast => "forecast", _ => "cross-functional"
    };

    public static string Code(FeedbackVerdict v) => v switch
    {
        FeedbackVerdict.Useful => "useful", FeedbackVerdict.Irrelevant => "irrelevant", FeedbackVerdict.Redundant => "redundant",
        FeedbackVerdict.TooEarly => "too-early", FeedbackVerdict.TooLate => "too-late", FeedbackVerdict.ActionTaken => "action-taken",
        FeedbackVerdict.CauseConfirmed => "cause-confirmed", _ => "cause-rejected"
    };

    public static FeedbackVerdict ParseVerdict(string code) => code switch
    {
        "useful" => FeedbackVerdict.Useful, "irrelevant" => FeedbackVerdict.Irrelevant, "redundant" => FeedbackVerdict.Redundant,
        "too-early" => FeedbackVerdict.TooEarly, "too-late" => FeedbackVerdict.TooLate, "action-taken" => FeedbackVerdict.ActionTaken,
        "cause-confirmed" => FeedbackVerdict.CauseConfirmed, "cause-rejected" => FeedbackVerdict.CauseRejected,
        _ => throw new FormatException($"Esito feedback sconosciuto: {code}")
    };
}

/// <summary>Incidenti: colonne per filtrare e ordinare, documento JSON completo per il caso.</summary>
public sealed class SqlIncidentStore(SqlConnectionFactory connections) : IIncidentStore
{
    public Task<IReadOnlyList<Incident>> ListOpenAsync(string sourceName, CancellationToken ct = default) =>
        Query("SELECT IncidentId, CaseJson FROM nugolo.Incident WHERE SourceName = @source AND Status <> 'resolved';",
            c => c.Parameters.Add(Param("@source", SqlDbType.NVarChar, sourceName)), ct);

    public Task<IReadOnlyList<Incident>> ListAsync(string? sourceName, bool includeResolved, int take, CancellationToken ct = default) =>
        Query($"""
            SELECT TOP ({Math.Clamp(take, 1, 1000)}) IncidentId, CaseJson FROM nugolo.Incident
            WHERE (@source IS NULL OR SourceName = @source) AND (@all = 1 OR Status <> 'resolved')
            ORDER BY CASE WHEN Status = 'resolved' THEN 1 ELSE 0 END, SeverityScore DESC, UpdatedAt DESC;
            """,
            c =>
            {
                c.Parameters.Add(Param("@source", SqlDbType.NVarChar, sourceName));
                c.Parameters.Add(Param("@all", SqlDbType.Bit, includeResolved));
            }, ct);

    public async Task<Incident?> GetAsync(long id, CancellationToken ct = default) =>
        (await Query("SELECT IncidentId, CaseJson FROM nugolo.Incident WHERE IncidentId = @id;",
            c => c.Parameters.Add(Param("@id", SqlDbType.BigInt, id)), ct)).SingleOrDefault();

    public async Task<long> SaveAsync(Incident incident, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = incident.Id == 0
            ? """
              INSERT INTO nugolo.Incident (SourceName, Warehouse, Domain, Signature, Title, Status, Severity, SeverityScore, Confidence,
                                           EscalationLevel, Verification, DetectedAt, UpdatedAt, CaseJson)
              OUTPUT INSERTED.IncidentId
              VALUES (@source, @warehouse, @domain, @signature, @title, @status, @severity, @score, @confidence, @escalation, @verification, @detected, @updated, N'{}');
              """
            : """
              UPDATE nugolo.Incident SET Title = @title, Status = @status, Severity = @severity, SeverityScore = @score, Confidence = @confidence,
                     EscalationLevel = @escalation, Verification = @verification, UpdatedAt = @updated
              OUTPUT INSERTED.IncidentId
              WHERE IncidentId = @id;
              """;
        cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, incident.Id));
        cmd.Parameters.Add(Param("@source", SqlDbType.NVarChar, incident.SourceName));
        cmd.Parameters.Add(Param("@warehouse", SqlDbType.NVarChar, incident.Warehouse));
        cmd.Parameters.Add(Param("@domain", SqlDbType.VarChar, AgenticCodes.Code(incident.Domain)));
        cmd.Parameters.Add(Param("@signature", SqlDbType.NVarChar, incident.Signature));
        cmd.Parameters.Add(Param("@title", SqlDbType.NVarChar, incident.Title.Length > 500 ? incident.Title[..500] : incident.Title));
        cmd.Parameters.Add(Param("@status", SqlDbType.VarChar, AgenticCodes.Code(incident.Status)));
        cmd.Parameters.Add(Param("@severity", SqlDbType.VarChar, AgenticCodes.Code(incident.Severity)));
        cmd.Parameters.Add(Param("@score", SqlDbType.Float, incident.SeverityScore));
        cmd.Parameters.Add(Param("@confidence", SqlDbType.VarChar, AgenticCodes.Code(incident.Confidence)));
        cmd.Parameters.Add(Param("@escalation", SqlDbType.Int, incident.EscalationLevel));
        cmd.Parameters.Add(Param("@verification", SqlDbType.VarChar, AgenticCodes.Code(incident.Verification)));
        cmd.Parameters.Add(Param("@detected", SqlDbType.DateTimeOffset, incident.DetectedAt));
        cmd.Parameters.Add(Param("@updated", SqlDbType.DateTimeOffset, incident.UpdatedAt));
        var id = (long)(await cmd.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException($"Incidente {incident.Id} non trovato."));

        // Il documento contiene l'Id: si scrive dopo averlo ottenuto.
        await using var json = connection.CreateCommand();
        json.CommandText = "UPDATE nugolo.Incident SET CaseJson = @json WHERE IncidentId = @id;";
        json.Parameters.Add(Param("@json", SqlDbType.NVarChar, NugoloJson.Serialize(incident with { Id = id })));
        json.Parameters.Add(Param("@id", SqlDbType.BigInt, id));
        await json.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task AddFeedbackAsync(IncidentFeedback feedback, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO nugolo.IncidentFeedback (IncidentId, At, Verdict, Note, UserName) VALUES (@id, @at, @verdict, @note, @user);";
        cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, feedback.IncidentId));
        cmd.Parameters.Add(Param("@at", SqlDbType.DateTimeOffset, feedback.At));
        cmd.Parameters.Add(Param("@verdict", SqlDbType.VarChar, AgenticCodes.Code(feedback.Verdict)));
        cmd.Parameters.Add(Param("@note", SqlDbType.NVarChar, feedback.Note));
        cmd.Parameters.Add(Param("@user", SqlDbType.NVarChar, feedback.User));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<IncidentFeedback>> FeedbackAsync(long incidentId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT At, Verdict, Note, UserName FROM nugolo.IncidentFeedback WHERE IncidentId = @id ORDER BY FeedbackId;";
        cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, incidentId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<IncidentFeedback>();
        while (await r.ReadAsync(ct))
            list.Add(new IncidentFeedback(incidentId, r.GetDateTimeOffset(0), AgenticCodes.ParseVerdict(r.GetString(1)),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    public async Task<int> DismissalsAsync(string sourceName, string signature, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM nugolo.IncidentFeedback AS f
            JOIN nugolo.Incident AS i ON i.IncidentId = f.IncidentId
            WHERE i.SourceName = @source AND i.Signature = @signature AND f.Verdict IN ('irrelevant', 'redundant');
            """;
        cmd.Parameters.Add(Param("@source", SqlDbType.NVarChar, sourceName));
        cmd.Parameters.Add(Param("@signature", SqlDbType.NVarChar, signature));
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<IReadOnlyList<Incident>> Query(string sql, Action<Microsoft.Data.SqlClient.SqlCommand> parameters, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        parameters(cmd);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<Incident>();
        while (await r.ReadAsync(ct)) list.Add(NugoloJson.ReadIncident(r.GetString(1)) with { Id = r.GetInt64(0) });
        return list;
    }
}

public sealed class SqlBriefingStore(SqlConnectionFactory connections) : IBriefingStore
{
    public async Task<long> SaveAsync(ExecutiveBriefing briefing, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nugolo.Briefing (SourceName, AsOf, CreatedAt, BriefingJson) OUTPUT INSERTED.BriefingId
            VALUES (@source, @asOf, @created, @json);
            """;
        cmd.Parameters.Add(Param("@source", SqlDbType.NVarChar, briefing.SourceName));
        cmd.Parameters.Add(Param("@asOf", SqlDbType.Date, briefing.AsOf.ToDateTime(TimeOnly.MinValue)));
        cmd.Parameters.Add(Param("@created", SqlDbType.DateTimeOffset, briefing.CreatedAt));
        cmd.Parameters.Add(Param("@json", SqlDbType.NVarChar, NugoloJson.Serialize(briefing)));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public Task<ExecutiveBriefing?> LatestAsync(string sourceName, CancellationToken ct = default) =>
        One("SELECT TOP (1) BriefingId, BriefingJson FROM nugolo.Briefing WHERE SourceName = @p ORDER BY BriefingId DESC;", SqlDbType.NVarChar, sourceName, ct);

    public Task<ExecutiveBriefing?> GetAsync(long id, CancellationToken ct = default) =>
        One("SELECT BriefingId, BriefingJson FROM nugolo.Briefing WHERE BriefingId = @p;", SqlDbType.BigInt, id, ct);

    public async Task<IReadOnlyList<(long Id, string SourceName, DateOnly AsOf, DateTimeOffset CreatedAt)>> ListAsync(int take, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT TOP ({Math.Clamp(take, 1, 500)}) BriefingId, SourceName, AsOf, CreatedAt FROM nugolo.Briefing ORDER BY BriefingId DESC;";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<(long, string, DateOnly, DateTimeOffset)>();
        while (await r.ReadAsync(ct)) list.Add((r.GetInt64(0), r.GetString(1), DateOnly.FromDateTime(r.GetDateTime(2)), r.GetDateTimeOffset(3)));
        return list;
    }

    private async Task<ExecutiveBriefing?> One(string sql, SqlDbType type, object value, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(Param("@p", type, value));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? NugoloJson.ReadBriefing(r.GetString(1)) with { Id = r.GetInt64(0) } : null;
    }
}

public sealed class SqlInvestigationStore(SqlConnectionFactory connections) : IInvestigationStore
{
    public async Task<long> SaveAsync(InvestigationCase investigation, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nugolo.Investigation (SourceName, Question, CreatedAt, CaseJson) OUTPUT INSERTED.InvestigationId
            VALUES (@source, @question, @created, @json);
            """;
        cmd.Parameters.Add(Param("@source", SqlDbType.NVarChar, investigation.SourceName));
        cmd.Parameters.Add(Param("@question", SqlDbType.NVarChar, investigation.Question.Length > 2000 ? investigation.Question[..2000] : investigation.Question));
        cmd.Parameters.Add(Param("@created", SqlDbType.DateTimeOffset, investigation.CreatedAt));
        cmd.Parameters.Add(Param("@json", SqlDbType.NVarChar, NugoloJson.Serialize(investigation)));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<InvestigationCase?> GetAsync(long id, CancellationToken ct = default) =>
        (await Query("SELECT InvestigationId, CaseJson FROM nugolo.Investigation WHERE InvestigationId = @id;", id, ct)).SingleOrDefault();

    public Task<IReadOnlyList<InvestigationCase>> ListAsync(int take, CancellationToken ct = default) =>
        Query($"SELECT TOP ({Math.Clamp(take, 1, 500)}) InvestigationId, CaseJson FROM nugolo.Investigation ORDER BY InvestigationId DESC;", null, ct);

    private async Task<IReadOnlyList<InvestigationCase>> Query(string sql, long? id, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (id is { } value) cmd.Parameters.Add(Param("@id", SqlDbType.BigInt, value));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<InvestigationCase>();
        while (await r.ReadAsync(ct)) list.Add(NugoloJson.ReadInvestigation(r.GetString(1)) with { Id = r.GetInt64(0) });
        return list;
    }
}
