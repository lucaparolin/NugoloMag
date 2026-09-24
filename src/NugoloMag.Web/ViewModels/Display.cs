using System.Globalization;
using System.Text;
using NugoloMag.Analyst.Domain.Discovery;
using NugoloMag.Analyst.Domain.Monitoring;
using NugoloMag.Analyst.Infrastructure.Reports;

namespace NugoloMag.Web.ViewModels;

/// <summary>Etichette e formattazioni per le viste (mapping esplicito, niente Enum.ToString).</summary>
public static class Display
{
    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    public static (string Css, string Text) Readiness(Readiness r) => r switch
    {
        Analyst.Domain.Discovery.Readiness.Ready => ("ok", "Pronto"),
        Analyst.Domain.Discovery.Readiness.NeedsReview => ("warn", "Da rivedere"),
        _ => ("fail", "Bloccato")
    };

    public static (string Css, string Icon) Step(StepStatus s) => s switch
    {
        StepStatus.Ok => ("ok", "✓"),
        StepStatus.Warning => ("warn", "!"),
        StepStatus.Failed => ("fail", "✗"),
        _ => ("muted", "·")
    };

    public static (string Css, string Text) Run(RunStatus s) => s switch
    {
        RunStatus.Succeeded => ("ok", "Completata"),
        RunStatus.Failed => ("fail", "Fallita"),
        _ => ("muted", "In corso")
    };

    public static string Severity(string code) => code switch { "high" => "fail", "medium" => "warn", _ => "muted" };

    public static string Time(DateTimeOffset? value, TimeZoneInfo zone) =>
        value is { } v ? TimeZoneInfo.ConvertTime(v, zone).ToString("dd/MM/yyyy HH:mm", It) : "—";

    public static string Number(double value) => value.ToString("#,0.##", It);
    public static string Number(decimal value) => value.ToString("#,0.##", It);
    public static string Number(long value) => value.ToString("#,0", It);
    public static string Percent(double ratio) => ratio.ToString("P0", It);

    /// <summary>Sparkline SVG della serie, con il periodo recente evidenziato. Contiene solo numeri: sicura da emettere raw.</summary>
    public static string Sparkline(IReadOnlyList<PointDocument> points, string recentFrom)
    {
        if (points.Count < 2) return "";
        const double w = 420, h = 90, pad = 4;
        var inv = CultureInfo.InvariantCulture;
        var min = Math.Min(0, points.Min(p => p.Value));
        var range = Math.Max(points.Max(p => p.Value) - min, 1e-9);
        double X(int i) => pad + i * (w - 2 * pad) / (points.Count - 1);
        double Y(double v) => h - pad - (v - min) / range * (h - 2 * pad);

        var path = new StringBuilder();
        for (var i = 0; i < points.Count; i++)
            path.Append(i == 0 ? 'M' : 'L').Append(X(i).ToString("0.#", inv)).Append(',').Append(Y(points[i].Value).ToString("0.#", inv)).Append(' ');

        var recentIndex = Math.Max(0, points.ToList().FindIndex(p => string.CompareOrdinal(p.Date, recentFrom) >= 0));
        var rx = X(recentIndex);
        return $"""<svg class="spark" viewBox="0 0 {w} {h}" role="img" aria-label="Andamento"><rect x="{rx.ToString("0.#", inv)}" y="0" width="{(w - rx).ToString("0.#", inv)}" height="{h}" class="spark-recent"/><path d="{path}" class="spark-line"/><circle cx="{X(points.Count - 1).ToString("0.#", inv)}" cy="{Y(points[^1].Value).ToString("0.#", inv)}" r="3.5" class="spark-dot"/></svg>""";
    }
}
