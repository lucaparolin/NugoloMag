using System.Data;
using Microsoft.Data.SqlClient;

namespace NugoloMag.Analyst.Infrastructure.Store;

/// <summary>Crea lo schema nugolo se manca. Idempotente: si esegue a ogni avvio.</summary>
public sealed class StoreSchemaInstaller(SqlConnectionFactory connections)
{
    private const string Ddl = """
        IF SCHEMA_ID(N'nugolo') IS NULL EXEC(N'CREATE SCHEMA nugolo');

        IF OBJECT_ID(N'nugolo.Discovery', N'U') IS NULL
        CREATE TABLE nugolo.Discovery (
            DiscoveryId  bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Discovery PRIMARY KEY,
            SourceName   nvarchar(100)     NOT NULL,
            DatabaseName nvarchar(128)     NOT NULL,
            CreatedAt    datetimeoffset(0) NOT NULL,
            Readiness    varchar(20)       NOT NULL,
            ReportJson   nvarchar(max)     NOT NULL);

        IF OBJECT_ID(N'nugolo.Monitor', N'U') IS NULL
        CREATE TABLE nugolo.Monitor (
            MonitorId    bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Monitor PRIMARY KEY,
            Name         nvarchar(100)     NOT NULL,
            SourceName   nvarchar(100)     NOT NULL,
            DiscoveryId  bigint            NOT NULL CONSTRAINT FK_Monitor_Discovery REFERENCES nugolo.Discovery(DiscoveryId),
            MappingJson  nvarchar(max)     NOT NULL,
            SourceQuery  nvarchar(max)     NOT NULL,
            RunAt        time(0)           NOT NULL,
            BaselineDays int               NOT NULL,
            RecentDays   int               NOT NULL,
            IsActive     bit               NOT NULL,
            CreatedAt    datetimeoffset(0) NOT NULL,
            NextRunAt    datetimeoffset(0) NULL);

        IF OBJECT_ID(N'nugolo.MonitorRun', N'U') IS NULL
        CREATE TABLE nugolo.MonitorRun (
            RunId         bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MonitorRun PRIMARY KEY,
            MonitorId     bigint            NOT NULL CONSTRAINT FK_MonitorRun_Monitor REFERENCES nugolo.Monitor(MonitorId),
            AsOf          date              NOT NULL,
            StartedAt     datetimeoffset(0) NOT NULL,
            CompletedAt   datetimeoffset(0) NULL,
            Status        varchar(20)       NOT NULL,
            RowsAnalyzed  int               NOT NULL CONSTRAINT DF_MonitorRun_Rows DEFAULT 0,
            FindingsCount int               NOT NULL CONSTRAINT DF_MonitorRun_Findings DEFAULT 0,
            HighCount     int               NOT NULL CONSTRAINT DF_MonitorRun_High DEFAULT 0,
            Narrative     nvarchar(max)     NULL,
            ReportJson    nvarchar(max)     NULL,
            Error         nvarchar(max)     NULL);

        IF COL_LENGTH(N'nugolo.Monitor', N'TaskQuery') IS NULL
            ALTER TABLE nugolo.Monitor ADD TaskQuery nvarchar(max) NULL;

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MonitorRun_Monitor' AND object_id = OBJECT_ID(N'nugolo.MonitorRun'))
        CREATE INDEX IX_MonitorRun_Monitor ON nugolo.MonitorRun (MonitorId, StartedAt DESC);

        IF OBJECT_ID(N'nugolo.Finding', N'U') IS NULL
        CREATE TABLE nugolo.Finding (
            RunId     bigint        NOT NULL CONSTRAINT FK_Finding_Run REFERENCES nugolo.MonitorRun(RunId),
            [Rank]    int           NOT NULL,
            Kind      varchar(30)   NOT NULL,
            Severity  varchar(10)   NOT NULL,
            Magnitude float         NOT NULL,
            Subject   nvarchar(120) NOT NULL,
            Metric    varchar(20)   NULL,
            Headline  nvarchar(500) NOT NULL,
            CONSTRAINT PK_Finding PRIMARY KEY (RunId, [Rank]));

        IF OBJECT_ID(N'nugolo.Conversation', N'U') IS NULL
        CREATE TABLE nugolo.Conversation (
            ConversationId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Conversation PRIMARY KEY,
            SourceName     nvarchar(100)     NOT NULL,
            Title          nvarchar(200)     NOT NULL,
            CreatedAt      datetimeoffset(0) NOT NULL,
            UpdatedAt      datetimeoffset(0) NOT NULL);

        IF OBJECT_ID(N'nugolo.ConversationEntry', N'U') IS NULL
        CREATE TABLE nugolo.ConversationEntry (
            ConversationId bigint            NOT NULL CONSTRAINT FK_Entry_Conversation REFERENCES nugolo.Conversation(ConversationId),
            Seq            int               NOT NULL,
            Kind           varchar(20)       NOT NULL,
            Text           nvarchar(max)     NULL,
            ToolName       varchar(50)       NULL,
            ToolInput      nvarchar(max)     NULL,
            ToolResult     nvarchar(max)     NULL,
            IsError        bit               NOT NULL,
            CreatedAt      datetimeoffset(0) NOT NULL,
            CONSTRAINT PK_ConversationEntry PRIMARY KEY (ConversationId, Seq));

        IF OBJECT_ID(N'nugolo.SavedQuery', N'U') IS NULL
        CREATE TABLE nugolo.SavedQuery (
            QueryId        bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SavedQuery PRIMARY KEY,
            SourceName     nvarchar(100)     NOT NULL,
            Name           nvarchar(200)     NOT NULL,
            Description    nvarchar(1000)    NOT NULL,
            SqlText        nvarchar(max)     NOT NULL,
            CreatedBy      varchar(20)       NOT NULL,
            ConversationId bigint            NULL,
            CreatedAt      datetimeoffset(0) NOT NULL);

        IF OBJECT_ID(N'nugolo.Incident', N'U') IS NULL
        CREATE TABLE nugolo.Incident (
            IncidentId      bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Incident PRIMARY KEY,
            SourceName      nvarchar(100)     NOT NULL,
            Warehouse       nvarchar(20)      NOT NULL,
            Domain          varchar(30)       NOT NULL,
            Signature       nvarchar(300)     NOT NULL,
            Title           nvarchar(500)     NOT NULL,
            Status          varchar(20)       NOT NULL,
            Severity        varchar(20)       NOT NULL,
            SeverityScore   float             NOT NULL,
            Confidence      varchar(10)       NOT NULL,
            EscalationLevel int               NOT NULL,
            Verification    varchar(20)       NOT NULL,
            DetectedAt      datetimeoffset(0) NOT NULL,
            UpdatedAt       datetimeoffset(0) NOT NULL,
            CaseJson        nvarchar(max)     NOT NULL);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Incident_Source_Status' AND object_id = OBJECT_ID(N'nugolo.Incident'))
        CREATE INDEX IX_Incident_Source_Status ON nugolo.Incident (SourceName, Status) INCLUDE (Signature, SeverityScore);

        IF OBJECT_ID(N'nugolo.IncidentFeedback', N'U') IS NULL
        CREATE TABLE nugolo.IncidentFeedback (
            FeedbackId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_IncidentFeedback PRIMARY KEY,
            IncidentId bigint            NOT NULL CONSTRAINT FK_Feedback_Incident REFERENCES nugolo.Incident(IncidentId),
            At         datetimeoffset(0) NOT NULL,
            Verdict    varchar(20)       NOT NULL,
            Note       nvarchar(1000)    NULL,
            UserName   nvarchar(100)     NULL);

        IF OBJECT_ID(N'nugolo.Briefing', N'U') IS NULL
        CREATE TABLE nugolo.Briefing (
            BriefingId   bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Briefing PRIMARY KEY,
            SourceName   nvarchar(100)     NOT NULL,
            AsOf         date              NOT NULL,
            CreatedAt    datetimeoffset(0) NOT NULL,
            BriefingJson nvarchar(max)     NOT NULL);

        IF OBJECT_ID(N'nugolo.Investigation', N'U') IS NULL
        CREATE TABLE nugolo.Investigation (
            InvestigationId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Investigation PRIMARY KEY,
            SourceName      nvarchar(100)     NOT NULL,
            Question        nvarchar(2000)    NOT NULL,
            CreatedAt       datetimeoffset(0) NOT NULL,
            CaseJson        nvarchar(max)     NOT NULL);
        """;

    public async Task InstallAsync(CancellationToken ct = default)
    {
        await EnsureDatabaseAsync(ct);
        await using var connection = await connections.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Ddl;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Crea il database di NugoloMag se non esiste (serve il permesso CREATE DATABASE; altrimenti crearlo a mano).</summary>
    private async Task EnsureDatabaseAsync(CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(connections.ConnectionString);
        var database = builder.InitialCatalog;
        if (string.IsNullOrEmpty(database)) return;

        builder.InitialCatalog = "master";
        await using var master = new SqlConnection(builder.ConnectionString);
        await master.OpenAsync(ct);
        await using var cmd = master.CreateCommand();
        cmd.CommandText = """
            IF DB_ID(@db) IS NULL
            BEGIN
                DECLARE @sql nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@db);
                EXEC (@sql);
            END
            """;
        cmd.Parameters.Add(SqlConnectionFactory.Param("@db", SqlDbType.NVarChar, database));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
