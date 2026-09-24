using NugoloMag.Analyst.Domain.Discovery;

namespace NugoloMag.Analyst.Application.Discovery;

public sealed record CodeClassification(
    IReadOnlyList<string> Inbound,
    IReadOnlyList<string> Outbound,
    IReadOnlyList<string> Adjustment,
    IReadOnlyList<string> Unclassified,
    double ClassifiedShare);

/// <summary>Interpreta le causali di magazzino (CAR, VEN, INV, ...) in carichi, scarichi e rettifiche.</summary>
public static class MovementCodeClassifier
{
    private static readonly string[] InboundCodes = ["C", "CA", "CAR", "CARICO", "IN", "E", "EN", "ENT", "ENTRATA", "ACQ", "ACQUISTO", "RES", "RESO", "RIC", "CF", "PO", "REC", "RECEIPT", "+"];
    private static readonly string[] OutboundCodes = ["S", "SC", "SCA", "SCARICO", "V", "VEN", "VENDITA", "OUT", "U", "USC", "USCITA", "SPE", "SPED", "DDT", "BOL", "SO", "SHIP", "SALE", "PRE", "PRELIEVO", "-"];
    private static readonly string[] AdjustmentCodes = ["INV", "INVENTARIO", "RET", "RETT", "RETTIFICA", "ADJ", "R", "RIN", "INI", "APE", "APERTURA", "DIF"];

    private static readonly string[] InboundPrefixes = ["CAR", "ENT", "ACQ", "RIC", "RES"];
    private static readonly string[] OutboundPrefixes = ["SCA", "VEN", "USC", "SPE", "PRE"];
    private static readonly string[] AdjustmentPrefixes = ["INV", "RET", "ADJ", "APE"];

    public static CodeClassification Classify(IReadOnlyList<CodeFrequency> codes)
    {
        List<string> inbound = [], outbound = [], adjustment = [], unknown = [];
        long classifiedRows = 0;

        foreach (var c in codes)
        {
            var code = c.Code.Trim().ToUpperInvariant();
            var target =
                AdjustmentCodes.Contains(code) || AdjustmentPrefixes.Any(code.StartsWith) ? adjustment :
                InboundCodes.Contains(code) || InboundPrefixes.Any(code.StartsWith) ? inbound :
                OutboundCodes.Contains(code) || OutboundPrefixes.Any(code.StartsWith) ? outbound :
                unknown;
            target.Add(c.Code.Trim());
            if (!ReferenceEquals(target, unknown)) classifiedRows += c.Rows;
        }

        var total = codes.Sum(c => c.Rows);
        return new CodeClassification(inbound, outbound, adjustment, unknown, total == 0 ? 0 : classifiedRows / (double)total);
    }
}
