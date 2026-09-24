using System.Globalization;
using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Infrastructure.Data;

/// <summary>
/// Sorgente CSV (export da gestionale). Intestazione attesa:
/// date,warehouse,sku,category,on_hand,inbound,outbound,adjustment,unit_cost — separatore "," o ";", decimali con punto.
/// </summary>
public sealed class CsvInventoryRepository(string path) : IInventoryRepository
{
    public static readonly string Header = "date,warehouse,sku,category,on_hand,inbound,outbound,adjustment,unit_cost";

    public async Task<IReadOnlyList<StockDay>> LoadAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var rows = new List<StockDay>();
        var lineNumber = 0;
        char separator = ',';

        foreach (var line in await File.ReadAllLinesAsync(path, ct))
        {
            lineNumber++;
            if (lineNumber == 1)
            {
                separator = line.Contains(';') ? ';' : ',';
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) continue;

            var c = line.Split(separator);
            if (c.Length < 9) throw new FormatException($"{path}:{lineNumber}: expected 9 columns, found {c.Length}.");

            var date = DateOnly.ParseExact(c[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (date < from || date > to) continue;

            rows.Add(new StockDay(date, new WarehouseCode(c[1]), new Sku(c[2]), c[3].Trim(),
                Dec(c[4]), Dec(c[5]), Dec(c[6]), Dec(c[7]), Dec(c[8])));
        }
        return rows;
    }

    private static decimal Dec(string s) =>
        string.IsNullOrWhiteSpace(s) ? 0m : decimal.Parse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture);

    public static async Task WriteAsync(string path, IEnumerable<StockDay> rows, CancellationToken ct = default)
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = rows.Select(r => string.Join(',',
            r.Date.ToString("yyyy-MM-dd", inv), r.Warehouse, r.Sku, r.Category,
            r.OnHand.ToString(inv), r.Inbound.ToString(inv), r.Outbound.ToString(inv), r.Adjustment.ToString(inv), r.UnitCost.ToString(inv)));
        await File.WriteAllLinesAsync(path, lines.Prepend(Header), ct);
    }
}
