using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Application;

public static class MetricLabels
{
    public static string Italian(Metric metric) => metric switch
    {
        Metric.OnHand => "Giacenza",
        Metric.Inbound => "Entrate",
        Metric.Outbound => "Uscite",
        Metric.Adjustment => "Rettifiche inventariali",
        Metric.StockValue => "Valore giacenza",
        _ => metric.ToString()
    };

    public static string Italian(FindingKind kind) => kind switch
    {
        FindingKind.Spike => "Picco",
        FindingKind.Drop => "Crollo",
        FindingKind.TrendReversal => "Inversione di trend",
        FindingKind.NewItem => "Nuovi articoli",
        FindingKind.StalledItem => "Articolo fermo",
        FindingKind.Stockout => "Rottura di stock",
        FindingKind.MixDrift => "Cambio di mix",
        FindingKind.DataFreshness => "Qualità del caricamento",
        FindingKind.Integrity => "Integrità giacenze",
        _ => kind.ToString()
    };

    public static string Number(double value) => value.ToString("#,0.##", System.Globalization.CultureInfo.GetCultureInfo("it-IT"));

    public static string Percent(double ratio) => (ratio >= 0 ? "+" : "") + ratio.ToString("P0", System.Globalization.CultureInfo.GetCultureInfo("it-IT"));
}
