using System.Text;
using NugoloMag.Analyst.Domain.Discovery;

namespace NugoloMag.Analyst.Application.Discovery;

/// <summary>
/// Riconosce il significato delle colonne dal nome (italiano e inglese, gergo da gestionale) e dal tipo SQL.
/// Come un magazziniere esperto che legge "CodArt", "QtaCar", "Causale" e capisce subito di cosa si tratta.
/// </summary>
public static class ColumnClassifier
{
    private sealed record Rule(ColumnRole Role, string[] Exact, string[] Contains, Func<CatalogColumn, bool> TypeOk);

    // L'ordine conta: i ruoli più specifici (carico/scarico/giacenza) vengono prima della quantità generica.
    private static readonly Rule[] Rules =
    [
        new(ColumnRole.InboundQuantity, ["qtacar", "qtacarico", "qtaent", "qtain", "inqty", "qtyin"],
            ["carico", "carichi", "caricat", "entrat", "inbound", "ricevut", "received"], c => c.IsNumeric),
        new(ColumnRole.OutboundQuantity, ["qtasca", "qtascarico", "qtausc", "qtaout", "outqty", "qtyout"],
            ["scaric", "uscit", "outbound", "spedit", "shipped", "vendut", "sold"], c => c.IsNumeric),
        new(ColumnRole.OnHand, ["giacenza", "giac", "esistenza", "saldo", "onhand", "stock", "disponibile", "balance"],
            ["giacenz", "esistenz", "saldo", "onhand", "disponib", "stocklevel", "qtystock"], c => c.IsNumeric),
        new(ColumnRole.UnitCost, ["costo", "cost", "cmp", "costostd", "costostandard", "unitcost", "standardcost", "costounitario"],
            ["costo", "cost", "prezzoacq", "valoreunit"], c => c.IsNumeric),
        new(ColumnRole.Quantity, ["qta", "qty", "quantita", "quantity", "qnt", "qt", "pezzi", "colli", "quant"],
            ["quantit", "qta", "qty"], c => c.IsNumeric),
        new(ColumnRole.Date, ["data", "date", "dt", "giorno", "day"],
            ["data", "date", "dtmov", "giorno"], c => c.IsDate),
        new(ColumnRole.MovementType, ["causale", "cau", "codcausale", "tipo", "tipomov", "tipomovimento", "movtype", "type", "segno", "transactiontype", "trantype"],
            ["causal", "tipomov", "movtype", "transtype"], c => c.IsKeyLike),
        new(ColumnRole.Warehouse, ["mag", "magazzino", "codmag", "idmag", "idmagazzino", "codicemagazzino", "warehouse", "warehouseid", "warehousecode", "wh", "whs", "deposito", "coddep", "store", "storeid"],
            ["magazzin", "warehouse", "deposit", "codmag"], c => c.IsKeyLike),
        new(ColumnRole.Item, ["art", "articolo", "codart", "codarticolo", "codicearticolo", "idart", "idarticolo", "item", "itemid", "itemcode", "sku", "prodotto", "codprod", "product", "productid", "productcode"],
            ["articol", "codart", "sku", "itemcode", "itemid", "prodott", "product"], c => c.IsKeyLike),
        new(ColumnRole.Category, ["famiglia", "categoria", "category", "gruppo", "classe", "reparto", "linea", "family"],
            ["famigl", "categor", "gruppo", "merceolog", "reparto", "classe"], c => c.IsKeyLike),
        new(ColumnRole.Description, ["descrizione", "descr", "des", "description", "nome", "name"],
            ["descri", "description"], c => c.IsText)
    ];

    /// <summary>Per ogni colonna il ruolo più probabile (al massimo uno), con punteggio 0-100.</summary>
    public static IReadOnlyList<ColumnAssignment> Classify(CatalogTable table)
    {
        var result = new List<ColumnAssignment>();
        foreach (var column in table.Columns)
        {
            var name = Normalize(column.Name);
            foreach (var rule in Rules)
            {
                if (!rule.TypeOk(column)) continue;
                var score = rule.Exact.Contains(name) ? 100 : rule.Contains.Any(name.Contains) ? 70 : 0;
                if (score == 0) continue;
                result.Add(new ColumnAssignment(rule.Role, column.Name, score));
                break;
            }
        }

        // Una data senza nome riconoscibile è comunque una data: la teniamo con punteggio basso.
        if (result.All(a => a.Role != ColumnRole.Date) && table.Columns.FirstOrDefault(c => c.IsDate) is { } anyDate)
            result.Add(new ColumnAssignment(ColumnRole.Date, anyDate.Name, 30));

        return result;
    }

    /// <summary>La colonna migliore per ogni ruolo.</summary>
    public static IReadOnlyList<ColumnAssignment> BestPerRole(IEnumerable<ColumnAssignment> assignments) =>
        assignments.GroupBy(a => a.Role).Select(g => g.OrderByDescending(a => a.Score).First()).ToList();

    public static string Normalize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }
}
