using System.Data;
using Microsoft.Data.SqlClient;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Infrastructure.Demo;

/// <summary>
/// Crea un database "GestionaleDemo" con lo schema tipico di un gestionale italiano: anagrafiche, movimenti
/// con causali, saldi correnti e tabelle "di disturbo" (clienti, ordini, listini) per mettere alla prova l'agente.
/// I movimenti derivano dal generatore sintetico, quindi contengono le stesse anomalie iniettate.
/// </summary>
public sealed class DemoErpSeeder(string masterConnectionString, string database = "GestionaleDemo")
{
    public async Task SeedAsync(DateOnly asOf, CancellationToken ct = default)
    {
        await ExecuteAsync(masterConnectionString, $"""
            IF DB_ID(N'{database}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{database}];
            END
            CREATE DATABASE [{database}];
            """, ct);

        var cs = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = database }.ConnectionString;
        await ExecuteAsync(cs, Schema, ct);

        var rows = new SyntheticInventoryGenerator().Generate(asOf, days: 75);
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync(ct);

        await BulkAsync(connection, "dbo.Magazzini", Magazzini(rows), ct);
        await BulkAsync(connection, "dbo.Articoli", Articoli(rows), ct);
        await BulkAsync(connection, "dbo.MovMag", Movimenti(rows), ct);
        await BulkAsync(connection, "dbo.SaldiMagazzino", Saldi(rows, asOf), ct);
        await BulkAsync(connection, "dbo.MissioniMagazzino", Missioni(new SyntheticTaskGenerator().Generate(rows, asOf)), ct);
        await ExecuteAsync(cs, Noise, ct);
    }

    private const string Schema = """
        CREATE TABLE dbo.Magazzini (CodMag nvarchar(10) NOT NULL PRIMARY KEY, Descrizione nvarchar(60) NOT NULL);
        CREATE TABLE dbo.Articoli (
            CodArt nvarchar(20) NOT NULL PRIMARY KEY, Descrizione nvarchar(100) NOT NULL,
            Famiglia nvarchar(40) NOT NULL, CostoStd decimal(12,4) NOT NULL, Attivo bit NOT NULL);
        CREATE TABLE dbo.Causali (Codice char(3) NOT NULL PRIMARY KEY, Descrizione nvarchar(60) NOT NULL);
        CREATE TABLE dbo.MovMag (
            IdMov int IDENTITY(1,1) NOT NULL PRIMARY KEY,
            DataMov datetime2(0) NOT NULL,
            CodMag nvarchar(10) NOT NULL REFERENCES dbo.Magazzini(CodMag),
            CodArt nvarchar(20) NOT NULL REFERENCES dbo.Articoli(CodArt),
            Causale char(3) NOT NULL REFERENCES dbo.Causali(Codice),
            Quantita decimal(12,3) NOT NULL,
            NumDoc nvarchar(20) NULL);
        CREATE INDEX IX_MovMag_Data ON dbo.MovMag (DataMov) INCLUDE (CodMag, CodArt, Causale, Quantita);
        CREATE TABLE dbo.SaldiMagazzino (
            CodMag nvarchar(10) NOT NULL, CodArt nvarchar(20) NOT NULL, Giacenza decimal(12,3) NOT NULL,
            DataAggiornamento date NOT NULL, PRIMARY KEY (CodMag, CodArt));
        CREATE TABLE dbo.MissioniMagazzino (
            IdMissione int IDENTITY(1,1) NOT NULL PRIMARY KEY,
            CodMag nvarchar(10) NOT NULL REFERENCES dbo.Magazzini(CodMag),
            Attivita nvarchar(10) NOT NULL,
            Zona nvarchar(5) NOT NULL,
            NumOrdine nvarchar(20) NULL,
            CodArt nvarchar(20) NULL,
            Ubicazione nvarchar(20) NULL,
            Quantita decimal(12,3) NOT NULL,
            Operatore nvarchar(20) NOT NULL,
            Inizio datetime2(0) NOT NULL,
            Fine datetime2(0) NULL);
        CREATE INDEX IX_Missioni_Inizio ON dbo.MissioniMagazzino (Inizio);
        INSERT INTO dbo.Causali VALUES ('CAR', N'Carico da fornitore'), ('VEN', N'Vendita'), ('INV', N'Rettifica inventariale'), ('INI', N'Saldo iniziale');
        """;

    private const string Noise = """
        CREATE TABLE dbo.Clienti (IdCliente int IDENTITY PRIMARY KEY, RagioneSociale nvarchar(100), Citta nvarchar(50), DataInserimento datetime2);
        CREATE TABLE dbo.OrdiniTestata (IdOrdine int IDENTITY PRIMARY KEY, IdCliente int REFERENCES dbo.Clienti(IdCliente), DataOrdine date, Totale decimal(12,2));
        CREATE TABLE dbo.OrdiniRighe (IdOrdine int REFERENCES dbo.OrdiniTestata(IdOrdine), Riga int, CodArt nvarchar(20), Quantita decimal(12,3), Prezzo decimal(12,2), PRIMARY KEY (IdOrdine, Riga));
        CREATE TABLE dbo.ListiniPrezzi (CodArt nvarchar(20), DataValidita date, Prezzo decimal(12,2), PRIMARY KEY (CodArt, DataValidita));
        CREATE TABLE dbo.Utenti (Login nvarchar(50) PRIMARY KEY, Nome nvarchar(100), UltimoAccesso datetime2);
        INSERT INTO dbo.Clienti (RagioneSociale, Citta, DataInserimento) VALUES (N'Rossi Srl', N'Milano', '2024-01-10'), (N'Bianchi Spa', N'Roma', '2024-03-02');
        INSERT INTO dbo.OrdiniTestata (IdCliente, DataOrdine, Totale) VALUES (1, '2026-09-01', 1200), (2, '2026-09-10', 830);
        INSERT INTO dbo.OrdiniRighe VALUES (1, 1, N'MI-001', 10, 12.5), (1, 2, N'MI-002', 4, 30), (2, 1, N'RM-003', 20, 8);
        INSERT INTO dbo.ListiniPrezzi SELECT CodArt, '2026-01-01', CostoStd * 1.4 FROM dbo.Articoli;
        """;

    private static DataTable Magazzini(IReadOnlyList<StockDay> rows)
    {
        var t = Table(("CodMag", typeof(string)), ("Descrizione", typeof(string)));
        foreach (var w in rows.Select(r => r.Warehouse.Value).Distinct())
            t.Rows.Add(w, w switch { "MI01" => "Milano", "RM02" => "Roma", "NA03" => "Napoli", _ => w });
        return t;
    }

    private static DataTable Articoli(IReadOnlyList<StockDay> rows)
    {
        var t = Table(("CodArt", typeof(string)), ("Descrizione", typeof(string)), ("Famiglia", typeof(string)), ("CostoStd", typeof(decimal)), ("Attivo", typeof(bool)));
        foreach (var a in rows.GroupBy(r => r.Sku).Select(g => g.First()))
            t.Rows.Add(a.Sku.Value, $"Articolo {a.Sku.Value}", a.Category, a.UnitCost, true);
        return t;
    }

    private static DataTable Movimenti(IReadOnlyList<StockDay> rows)
    {
        var t = Table(("IdMov", typeof(int)), ("DataMov", typeof(DateTime)), ("CodMag", typeof(string)), ("CodArt", typeof(string)),
            ("Causale", typeof(string)), ("Quantita", typeof(decimal)), ("NumDoc", typeof(string)));
        var rnd = new Random(7);

        foreach (var g in rows.GroupBy(r => (r.Warehouse, r.Sku)))
        {
            var ordered = g.OrderBy(r => r.Date).ToList();
            var first = ordered[0];
            var opening = first.OnHand - first.Inbound + first.Outbound - first.Adjustment;
            if (opening != 0) Add(first.Date.AddDays(-1), first, "INI", opening);

            decimal previous = opening;
            foreach (var r in ordered)
            {
                if (r.Inbound != 0) Add(r.Date, r, "CAR", r.Inbound);
                if (r.Outbound != 0) Add(r.Date, r, "VEN", r.Outbound);
                // Tutto ciò che non torna con carichi e vendite diventa rettifica: così la giacenza ricostruita coincide.
                var adjustment = r.OnHand - previous - r.Inbound + r.Outbound;
                if (adjustment != 0) Add(r.Date, r, "INV", adjustment);
                previous = r.OnHand;
            }
        }
        return t;

        void Add(DateOnly date, StockDay r, string causale, decimal qty) =>
            t.Rows.Add(DBNull.Value, date.ToDateTime(new TimeOnly(8 + rnd.Next(0, 10), rnd.Next(0, 60))), r.Warehouse.Value, r.Sku.Value, causale, qty, $"D{rnd.Next(10000, 99999)}");
    }

    private static DataTable Saldi(IReadOnlyList<StockDay> rows, DateOnly asOf)
    {
        var t = Table(("CodMag", typeof(string)), ("CodArt", typeof(string)), ("Giacenza", typeof(decimal)), ("DataAggiornamento", typeof(DateTime)));
        foreach (var r in rows.Where(r => r.Date == asOf))
            t.Rows.Add(r.Warehouse.Value, r.Sku.Value, r.OnHand, asOf.ToDateTime(TimeOnly.MinValue));
        return t;
    }

    private static DataTable Missioni(IReadOnlyList<DemoTask> tasks)
    {
        var t = Table(("IdMissione", typeof(int)), ("CodMag", typeof(string)), ("Attivita", typeof(string)), ("Zona", typeof(string)),
            ("NumOrdine", typeof(string)), ("CodArt", typeof(string)), ("Ubicazione", typeof(string)), ("Quantita", typeof(decimal)),
            ("Operatore", typeof(string)), ("Inizio", typeof(DateTime)), ("Fine", typeof(DateTime)));
        foreach (var (task, op) in tasks)
            t.Rows.Add(DBNull.Value, task.Warehouse.Value, task.Activity, task.Zone, task.OrderRef, (object?)task.Sku?.Value ?? DBNull.Value,
                (object?)task.Location ?? DBNull.Value, task.Quantity, op, task.StartedAt, task.EndedAt);
        return t;
    }

    private static DataTable Table(params (string Name, Type Type)[] columns)
    {
        var t = new DataTable();
        foreach (var (name, type) in columns) t.Columns.Add(name, type);
        return t;
    }

    private static async Task BulkAsync(SqlConnection connection, string table, DataTable data, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.KeepNulls, null) { DestinationTableName = table, BulkCopyTimeout = 300 };
        foreach (DataColumn c in data.Columns)
            if (c.ColumnName is not ("IdMov" or "IdMissione")) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        await bulk.WriteToServerAsync(data, ct);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
