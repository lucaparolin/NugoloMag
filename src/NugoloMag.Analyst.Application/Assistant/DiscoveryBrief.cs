using System.Text;
using NugoloMag.Analyst.Domain.Discovery;

namespace NugoloMag.Analyst.Application.Assistant;

/// <summary>
/// Riassunto dell'analisi del database già fatta dall'agente di discovery: l'assistente parte da ciò che si sa
/// (tabelle giuste, causali, query delle posizioni giornaliere) invece di riscoprirlo a ogni conversazione.
/// </summary>
public static class DiscoveryBrief
{
    public static string From(DiscoveryReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Analisi del database #{report.Id} del {report.CreatedAt:yyyy-MM-dd} (esito: {report.Readiness}).");
        foreach (var c in report.Candidates.OrderBy(c => c.Role).ThenByDescending(c => c.Score).Take(9))
            sb.AppendLine($"- candidata {Role(c.Role)}: {c.Table} (punteggio {c.Score}; colonne: {string.Join(", ", c.Columns.Select(a => $"{a.Column}={a.Role}"))})");

        if (report.Mapping is { } m)
        {
            var mov = m.Movements;
            sb.AppendLine($"Mapping confermato: movimenti in {mov.Table} (data {mov.DateColumn}, articolo {mov.ItemColumn}, magazzino {mov.WarehouseColumn ?? "unico"}).");
            if (mov.Direction == MovementDirection.TypeCode)
                sb.AppendLine($"Causale {mov.TypeColumn}: carichi [{string.Join(",", mov.InboundCodes)}], scarichi [{string.Join(",", mov.OutboundCodes)}], rettifiche [{string.Join(",", mov.AdjustmentCodes)}].");
            else
                sb.AppendLine($"Verso del movimento: {(mov.Direction == MovementDirection.SeparateColumns ? $"colonne {mov.InboundColumn}/{mov.OutboundColumn}" : $"segno di {mov.QuantityColumn}")}.");
            if (m.Items is { } items) sb.AppendLine($"Anagrafica articoli: {items.Table} (chiave {items.KeyColumn}, categoria {items.CategoryColumn ?? "-"}, costo {items.CostColumn ?? "-"}).");
            if (m.Snapshot is { } snap) sb.AppendLine($"Saldi storici: {snap.Table}.{snap.OnHandColumn}.");
        }

        if (report.MovementCodes.Count > 0)
            sb.AppendLine("Causali più frequenti: " + string.Join(", ", report.MovementCodes.Take(15).Select(c => $"{c.Code} ({c.Rows})")));
        return sb.ToString();
    }

    private static string Role(TableRole role) => role switch
    {
        TableRole.Movements => "movimenti",
        TableRole.StockSnapshot => "saldi",
        _ => "anagrafica articoli"
    };
}
