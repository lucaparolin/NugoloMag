using NugoloMag.Analyst.Domain.Discovery;

namespace NugoloMag.Analyst.Application.Discovery;

public sealed record MappingProposal(SourceMapping? Mapping, IReadOnlyList<string> Notes, CodeClassification? Codes);

/// <summary>Dalle tabelle candidate (già profilate) costruisce il mapping più plausibile, spiegando le scelte.</summary>
public static class MappingProposer
{
    public static MappingProposal Propose(
        DatabaseCatalog catalog,
        IReadOnlyList<TableCandidate> candidates,
        TableProfile? movementProfile,
        IReadOnlyList<CodeFrequency> movementCodes)
    {
        var notes = new List<string>();
        var mov = Best(candidates, TableRole.Movements);
        if (mov is null) return new MappingProposal(null, ["Nessuna tabella di movimenti riconosciuta."], null);

        notes.Add($"Movimenti da {mov.Table} (punteggio {mov.Score}).");
        var warehouse = mov.ColumnFor(ColumnRole.Warehouse);
        if (warehouse is null) notes.Add("Nessuna colonna magazzino: tutti i movimenti saranno attribuiti a un magazzino unico.");

        CodeClassification? codes = null;
        MovementSource movements;
        var inCol = mov.ColumnFor(ColumnRole.InboundQuantity);
        var outCol = mov.ColumnFor(ColumnRole.OutboundQuantity);
        var qty = mov.ColumnFor(ColumnRole.Quantity);
        var type = mov.ColumnFor(ColumnRole.MovementType);
        var negativeShare = movementProfile?.NegativeQuantityShare ?? 0;

        if (inCol is not null && outCol is not null)
        {
            movements = Movement(mov, warehouse, MovementDirection.SeparateColumns, null, inCol, outCol, null, [], [], []);
            notes.Add($"Verso del movimento: colonne distinte {inCol} (carico) e {outCol} (scarico).");
        }
        else if (type is not null && qty is not null && movementCodes.Count > 0 &&
                 (codes = MovementCodeClassifier.Classify(movementCodes)).ClassifiedShare >= 0.5)
        {
            movements = Movement(mov, warehouse, MovementDirection.TypeCode, qty, null, null, type, codes.Inbound, codes.Outbound, codes.Adjustment);
            notes.Add($"Verso del movimento dalla causale {type}: carichi [{string.Join(", ", codes.Inbound)}], scarichi [{string.Join(", ", codes.Outbound)}], rettifiche [{string.Join(", ", codes.Adjustment)}].");
            if (codes.Unclassified.Count > 0)
                notes.Add($"Causali non riconosciute (escluse, {1 - codes.ClassifiedShare:P1} delle righe): {string.Join(", ", codes.Unclassified)}. Assegnarle a mano se sono movimenti fisici.");
        }
        else if (qty is not null)
        {
            movements = Movement(mov, warehouse, MovementDirection.SignedQuantity, qty, null, null, null, [], [], []);
            notes.Add(negativeShare > 0.05
                ? $"Verso del movimento dal segno di {qty} ({negativeShare:P0} delle righe negative = uscite)."
                : $"Attenzione: {qty} è quasi sempre positiva e non c'è una causale riconoscibile: il verso dei movimenti va indicato a mano.");
        }
        else
        {
            return new MappingProposal(null, [.. notes, "La tabella dei movimenti non ha una colonna quantità utilizzabile."], null);
        }

        var snapshot = Best(candidates, TableRole.StockSnapshot);
        SnapshotSource? snapshotSource = null;
        if (snapshot?.ColumnFor(ColumnRole.Date) is { } sd && snapshot.ColumnFor(ColumnRole.Item) is { } si && snapshot.ColumnFor(ColumnRole.OnHand) is { } so)
        {
            snapshotSource = new SnapshotSource(snapshot.Table, sd, snapshot.ColumnFor(ColumnRole.Warehouse), si, so);
            notes.Add($"Giacenza dai saldi storici di {snapshot.Table}.{so}.");
        }
        else
        {
            notes.Add("Giacenza ricostruita sommando i movimenti (nessuna tabella di saldi storici giornalieri).");
        }

        var items = ItemMaster(catalog, candidates, mov, notes);

        TaskSource? tasks = null;
        if (Best(candidates, TableRole.Tasks) is { } t && t.ColumnFor(ColumnRole.StartTime) is { } start && t.ColumnFor(ColumnRole.EndTime) is { } end)
        {
            tasks = new TaskSource(t.Table, t.ColumnFor(ColumnRole.Warehouse), start, end, t.ColumnFor(ColumnRole.Zone), t.ColumnFor(ColumnRole.Activity),
                t.ColumnFor(ColumnRole.OrderRef), t.ColumnFor(ColumnRole.Item), t.ColumnFor(ColumnRole.Location), t.ColumnFor(ColumnRole.Quantity));
            notes.Add($"Missioni di magazzino da {t.Table} (inizio {start}, fine {end}, zona {tasks.ZoneColumn ?? "-"}): abilitano l'analisi di produttività.");
        }
        else
        {
            notes.Add("Nessuna tabella di missioni con tempi: l'agente Produttività resterà inattivo.");
        }

        return new MappingProposal(new SourceMapping(movements, snapshotSource, items, Tasks: tasks), notes, codes);
    }

    private static MovementSource Movement(TableCandidate mov, string? warehouse, MovementDirection direction,
        string? qty, string? inCol, string? outCol, string? type,
        IReadOnlyList<string> inCodes, IReadOnlyList<string> outCodes, IReadOnlyList<string> adjCodes) =>
        new(mov.Table, mov.ColumnFor(ColumnRole.Date)!, warehouse, mov.ColumnFor(ColumnRole.Item)!, direction,
            qty, inCol, outCol, type, inCodes, outCodes, adjCodes);

    private static ItemMasterSource? ItemMaster(DatabaseCatalog catalog, IReadOnlyList<TableCandidate> candidates, TableCandidate mov, List<string> notes)
    {
        var itemColumn = mov.ColumnFor(ColumnRole.Item)!;

        // 1) relazione dichiarata (foreign key) dai movimenti all'anagrafica: la prova più forte.
        var fk = catalog.ForeignKeys.FirstOrDefault(f => f.From == mov.Table && string.Equals(f.FromColumn, itemColumn, StringComparison.OrdinalIgnoreCase));
        if (fk is not null && catalog.Find(fk.To) is { } target)
        {
            var cols = ColumnClassifier.BestPerRole(ColumnClassifier.Classify(target));
            notes.Add($"Anagrafica articoli {fk.To} collegata da foreign key su {itemColumn}.");
            return new ItemMasterSource(fk.To, fk.ToColumn, Col(cols, ColumnRole.Category), Col(cols, ColumnRole.UnitCost));
        }

        // 2) candidato con la stessa colonna chiave, altrimenti il migliore.
        var masters = candidates.Where(c => c.Role == TableRole.ItemMaster).OrderByDescending(c => c.Score).ToList();
        var normalized = ColumnClassifier.Normalize(itemColumn);
        var best = masters.FirstOrDefault(c => ColumnClassifier.Normalize(c.ColumnFor(ColumnRole.Item)!) == normalized) ?? masters.FirstOrDefault();
        if (best is null)
        {
            notes.Add("Nessuna anagrafica articoli: categoria e costo non disponibili.");
            return null;
        }

        notes.Add($"Anagrafica articoli {best.Table} (collegata per nome colonna {best.ColumnFor(ColumnRole.Item)}).");
        return new ItemMasterSource(best.Table, best.ColumnFor(ColumnRole.Item)!, best.ColumnFor(ColumnRole.Category), best.ColumnFor(ColumnRole.UnitCost));
    }

    private static string? Col(IReadOnlyList<ColumnAssignment> cols, ColumnRole role) => cols.FirstOrDefault(c => c.Role == role)?.Column;

    private static TableCandidate? Best(IReadOnlyList<TableCandidate> candidates, TableRole role) =>
        candidates.Where(c => c.Role == role && c.Score >= TableClassifier.MinScore).OrderByDescending(c => c.Score).FirstOrDefault();
}
