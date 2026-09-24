using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Application;

/// <summary>
/// Evita di raccontare due volte la stessa storia: un finding di articolo già spiegato da un finding
/// di magazzino (stessa metrica e direzione, articolo o categoria tra le root cause) viene assorbito dal padre;
/// un crollo delle uscite di un articolo già segnalato come fermo o in rottura viene assorbito da quest'ultimo.
/// </summary>
public sealed class FindingConsolidator
{
    public IReadOnlyList<Finding> Consolidate(IReadOnlyList<Finding> findings, InventoryDataset data)
    {
        var absorbed = new Dictionary<Finding, List<Finding>>(ReferenceEqualityComparer.Instance);
        var result = new List<Finding>();

        foreach (var f in findings)
        {
            var parent = f.Subject.IsWarehouseLevel ? null : FindParent(f, findings, data);
            if (parent is null) { result.Add(f); continue; }

            if (!absorbed.TryGetValue(parent, out var children)) absorbed[parent] = children = [];
            children.Add(f);
        }

        return result.Select(f => absorbed.TryGetValue(f, out var children) && children.Any(c => c.Subject != f.Subject)
                ? f with
                {
                    Evidence = new Dictionary<string, string>(f.Evidence)
                    {
                        ["articoli coinvolti"] = string.Join(", ", children.Where(c => c.Subject != f.Subject).Select(c => c.Subject.Sku!.Value.Value).Distinct())
                    }
                }
                : f)
            .ToList();
    }

    private static Finding? FindParent(Finding child, IReadOnlyList<Finding> all, InventoryDataset data)
    {
        var sku = child.Subject.Sku!.Value;

        if (child.Kind == FindingKind.Drop && child.Metric == Metric.Outbound)
        {
            var lifecycle = all.FirstOrDefault(p => p.Subject == child.Subject && p.Kind is FindingKind.Stockout or FindingKind.StalledItem);
            if (lifecycle is not null) return lifecycle;
        }

        var category = data.CategoryOf(child.Subject.Warehouse, sku);
        return all.FirstOrDefault(p =>
            p.Subject == new Subject(child.Subject.Warehouse) &&
            p.Metric == child.Metric &&
            p.Kind == child.Kind &&
            p.RootCauses.Any(c =>
                (c.Dimension == "Categoria" && c.Member == category) ||
                (c.Dimension == "Articolo" && c.Member.StartsWith(sku.Value + " ", StringComparison.Ordinal))));
    }
}
