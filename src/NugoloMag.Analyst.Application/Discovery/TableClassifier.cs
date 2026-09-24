using NugoloMag.Analyst.Domain.Discovery;

namespace NugoloMag.Analyst.Application.Discovery;

/// <summary>Assegna alle tabelle un ruolo (movimenti, saldi, anagrafica articoli) con punteggio e motivazioni.</summary>
public static class TableClassifier
{
    public const int MinScore = 40;

    private static readonly string[] MovementNames = ["mov", "transaz", "transaction", "trans", "ledger", "giornale", "storico", "history"];
    private static readonly string[] SnapshotNames = ["saldi", "saldo", "giacenz", "esistenz", "snapshot", "inventory", "inventario", "stock"];
    private static readonly string[] ItemNames = ["articol", "anagraf", "item", "product", "prodott", "sku"];

    public static IReadOnlyList<TableCandidate> Classify(DatabaseCatalog catalog, int topPerRole = 5)
    {
        var all = new List<TableCandidate>();
        foreach (var table in catalog.Tables)
        {
            var columns = ColumnClassifier.BestPerRole(ColumnClassifier.Classify(table));
            if (Movements(table, columns) is { } m) all.Add(m);
            if (Snapshot(table, columns) is { } s) all.Add(s);
            if (ItemMaster(table, columns) is { } i) all.Add(i);
        }

        return all
            .Where(c => c.Score >= MinScore)
            .GroupBy(c => c.Role)
            .SelectMany(g => g.OrderByDescending(c => c.Score).Take(topPerRole))
            .ToList();
    }

    private static TableCandidate? Movements(CatalogTable table, IReadOnlyList<ColumnAssignment> cols)
    {
        bool Has(ColumnRole r) => cols.Any(c => c.Role == r);
        var hasQuantity = Has(ColumnRole.Quantity) || (Has(ColumnRole.InboundQuantity) && Has(ColumnRole.OutboundQuantity));
        if (!Has(ColumnRole.Date) || !Has(ColumnRole.Item) || !hasQuantity) return null;

        var reasons = new List<string> { "ha data, articolo e quantità" };
        var score = 35;
        score += NameBonus(table, MovementNames, 25, reasons, "il nome richiama i movimenti");
        if (Has(ColumnRole.Warehouse)) { score += 10; reasons.Add("ha il codice magazzino"); }
        if (Has(ColumnRole.MovementType)) { score += 10; reasons.Add("ha causale/tipo movimento"); }
        if (Has(ColumnRole.InboundQuantity)) { score += 10; reasons.Add("ha colonne distinte di carico e scarico"); }
        score += SizeBonus(table, reasons);
        if (IsKeyedByItem(table, cols)) { score -= 30; reasons.Add("l'articolo è chiave primaria: sembra un'anagrafica"); }

        return new TableCandidate(table.Name, TableRole.Movements, Math.Clamp(score, 0, 100), cols, reasons);
    }

    private static TableCandidate? Snapshot(CatalogTable table, IReadOnlyList<ColumnAssignment> cols)
    {
        bool Has(ColumnRole r) => cols.Any(c => c.Role == r);
        if (!Has(ColumnRole.Date) || !Has(ColumnRole.Item) || !Has(ColumnRole.OnHand)) return null;

        var reasons = new List<string> { "ha data, articolo e giacenza" };
        var score = 40;
        score += NameBonus(table, SnapshotNames, 25, reasons, "il nome richiama saldi o giacenze");
        if (Has(ColumnRole.Warehouse)) { score += 10; reasons.Add("ha il codice magazzino"); }
        score += SizeBonus(table, reasons);

        return new TableCandidate(table.Name, TableRole.StockSnapshot, Math.Clamp(score, 0, 100), cols, reasons);
    }

    private static TableCandidate? ItemMaster(CatalogTable table, IReadOnlyList<ColumnAssignment> cols)
    {
        bool Has(ColumnRole r) => cols.Any(c => c.Role == r);
        if (!Has(ColumnRole.Item)) return null;

        var reasons = new List<string>();
        var score = 0;
        if (IsKeyedByItem(table, cols)) { score += 35; reasons.Add("il codice articolo è chiave primaria"); }
        score += NameBonus(table, ItemNames, 25, reasons, "il nome richiama l'anagrafica articoli");
        if (Has(ColumnRole.Category)) { score += 20; reasons.Add("ha la categoria/famiglia"); }
        if (Has(ColumnRole.UnitCost)) { score += 15; reasons.Add("ha il costo"); }
        if (Has(ColumnRole.Description)) { score += 10; reasons.Add("ha la descrizione"); }
        if (Has(ColumnRole.Date) && (Has(ColumnRole.Quantity) || Has(ColumnRole.OnHand))) { score -= 30; reasons.Add("ha date e quantità: sembra una tabella di fatti"); }

        return new TableCandidate(table.Name, TableRole.ItemMaster, Math.Clamp(score, 0, 100), cols, reasons);
    }

    private static bool IsKeyedByItem(CatalogTable table, IReadOnlyList<ColumnAssignment> cols)
    {
        var item = cols.FirstOrDefault(c => c.Role == ColumnRole.Item)?.Column;
        var pk = table.Columns.Where(c => c.IsPrimaryKey).ToList();
        return item is not null && pk.Count == 1 && string.Equals(pk[0].Name, item, StringComparison.OrdinalIgnoreCase);
    }

    private static int NameBonus(CatalogTable table, string[] tokens, int bonus, List<string> reasons, string reason)
    {
        var name = ColumnClassifier.Normalize(table.Name.Name);
        if (!tokens.Any(name.Contains)) return 0;
        reasons.Add(reason);
        return bonus;
    }

    private static int SizeBonus(CatalogTable table, List<string> reasons)
    {
        if (table.RowCount is not { } rows || rows <= 0) return 0;
        var bonus = (int)Math.Min(20, Math.Log10(rows) * 4);
        reasons.Add($"{rows:N0} righe");
        return bonus;
    }
}
