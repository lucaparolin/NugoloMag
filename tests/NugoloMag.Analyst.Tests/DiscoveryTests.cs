using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Domain.Discovery;
using NugoloMag.Analyst.Infrastructure.SqlServer;

namespace NugoloMag.Analyst.Tests;

public class DiscoveryTests
{
    private static CatalogColumn Col(string name, string type, bool pk = false) => new(name, type, !pk, pk);

    private static readonly DatabaseCatalog Erp = new("Erp",
    [
        new(new TableName("dbo", "MovMag"), false, 250_000,
            [Col("IdMov", "int", true), Col("DataMov", "datetime2"), Col("CodMag", "nvarchar"), Col("CodArt", "nvarchar"), Col("Causale", "char"), Col("Quantita", "decimal")]),
        new(new TableName("dbo", "Articoli"), false, 5_000,
            [Col("CodArt", "nvarchar", true), Col("Descrizione", "nvarchar"), Col("Famiglia", "nvarchar"), Col("CostoStd", "decimal")]),
        new(new TableName("dbo", "OrdiniRighe"), false, 90_000,
            [Col("IdOrdine", "int"), Col("Riga", "int"), Col("CodArt", "nvarchar"), Col("Quantita", "decimal"), Col("Prezzo", "decimal")]),
        new(new TableName("dbo", "Clienti"), false, 800,
            [Col("IdCliente", "int", true), Col("RagioneSociale", "nvarchar"), Col("DataInserimento", "datetime2")])
    ],
    [new ForeignKey(new TableName("dbo", "MovMag"), "CodArt", new TableName("dbo", "Articoli"), "CodArt")]);

    [Theory]
    [InlineData("QtaCarico", "decimal", ColumnRole.InboundQuantity)]
    [InlineData("Quantità", "decimal", ColumnRole.Quantity)]
    [InlineData("DATA_MOV", "datetime", ColumnRole.Date)]
    [InlineData("cod_art", "varchar", ColumnRole.Item)]
    [InlineData("Esistenza", "numeric", ColumnRole.OnHand)]
    [InlineData("TipoMov", "char", ColumnRole.MovementType)]
    [InlineData("WarehouseCode", "nvarchar", ColumnRole.Warehouse)]
    public void Columns_are_recognised_from_italian_and_english_names(string name, string type, ColumnRole expected)
    {
        var table = new CatalogTable(new TableName("dbo", "T"), false, null, [Col(name, type)]);
        Assert.Equal(expected, ColumnClassifier.Classify(table).Single().Role);
    }

    [Fact]
    public void Name_match_requires_a_compatible_type()
    {
        var table = new CatalogTable(new TableName("dbo", "T"), false, null, [Col("Data", "nvarchar")]);
        Assert.DoesNotContain(ColumnClassifier.Classify(table), a => a.Role == ColumnRole.Date);
    }

    [Fact]
    public void Movements_table_wins_over_order_lines_and_item_master_is_recognised()
    {
        var candidates = TableClassifier.Classify(Erp);

        var movements = candidates.Where(c => c.Role == TableRole.Movements).OrderByDescending(c => c.Score).ToList();
        Assert.Equal("MovMag", movements[0].Table.Name);
        Assert.DoesNotContain(movements, c => c.Table.Name == "OrdiniRighe"); // niente data: non sono movimenti
        Assert.Equal("Articoli", candidates.Where(c => c.Role == TableRole.ItemMaster).OrderByDescending(c => c.Score).First().Table.Name);
    }

    [Fact]
    public void Movement_codes_are_classified_by_name()
    {
        var result = MovementCodeClassifier.Classify(
        [
            new("VEN", 7000, 7000), new("CAR", 900, 900), new("INV", 150, -20), new("XYZ", 50, 10)
        ]);

        Assert.Equal(["CAR"], result.Inbound);
        Assert.Equal(["VEN"], result.Outbound);
        Assert.Equal(["INV"], result.Adjustment);
        Assert.Equal(["XYZ"], result.Unclassified);
        Assert.InRange(result.ClassifiedShare, 0.99, 1);
    }

    [Fact]
    public void Proposal_uses_type_codes_and_follows_the_foreign_key_to_the_item_master()
    {
        var proposal = MappingProposer.Propose(Erp, TableClassifier.Classify(Erp), null,
            [new("VEN", 7000, 7000), new("CAR", 900, 900), new("INV", 150, -20)]);

        var mapping = Assert.IsType<SourceMapping>(proposal.Mapping);
        Assert.Equal(MovementDirection.TypeCode, mapping.Movements.Direction);
        Assert.Equal("Causale", mapping.Movements.TypeColumn);
        Assert.Equal("Articoli", mapping.Items!.Table.Name);
        Assert.Equal("Famiglia", mapping.Items.CategoryColumn);
        Assert.Equal("CostoStd", mapping.Items.CostColumn);
        Assert.Empty(mapping.Validate(Erp));
    }

    [Fact]
    public void Mapping_referencing_unknown_columns_is_rejected()
    {
        var bad = new SourceMapping(
            new MovementSource(new TableName("dbo", "MovMag"), "DataMov", null, "CodArt; DROP TABLE x", MovementDirection.SignedQuantity,
                "Quantita", null, null, null, [], [], []),
            null, null);

        Assert.Contains(bad.Validate(Erp), e => e.Contains("non trovata"));
    }

    [Fact]
    public void Generated_query_quotes_identifiers_and_escapes_codes()
    {
        var mapping = new SourceMapping(
            new MovementSource(new TableName("dbo", "Mov]Mag"), "DataMov", "CodMag", "CodArt", MovementDirection.TypeCode,
                "Quantita", null, null, "Causale", ["CAR"], ["V'EN"], []),
            null, new ItemMasterSource(new TableName("dbo", "Articoli"), "CodArt", "Famiglia", null));

        var sql = InventoryQueryBuilder.Build(mapping);

        Assert.Contains("[dbo].[Mov]]Mag]", sql);
        Assert.Contains("N'V''EN'", sql);
        Assert.Contains("@from", sql);
        Assert.Contains("@to", sql);
        Assert.Contains("1 = 0", sql); // nessuna causale di rettifica
    }

    [Fact]
    public void Next_run_is_scheduled_at_the_configured_local_time()
    {
        var monitor = new NugoloMag.Analyst.Domain.Monitoring.MonitorDefinition
        {
            Name = "m", SourceName = "s", DiscoveryId = 1, SourceQuery = "q", RunAt = new TimeOnly(6, 0),
            BaselineDays = 28, RecentDays = 7, IsActive = true, CreatedAt = DateTimeOffset.UnixEpoch,
            Mapping = new SourceMapping(new MovementSource(new TableName("dbo", "M"), "D", null, "S", MovementDirection.SignedQuantity, "Q", null, null, null, [], [], []), null, null)
        };
        var rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");

        var next = monitor.NextOccurrenceAfter(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero), rome);

        Assert.Equal(new DateTimeOffset(2026, 9, 25, 6, 0, 0, TimeSpan.FromHours(2)), next);
    }
}
