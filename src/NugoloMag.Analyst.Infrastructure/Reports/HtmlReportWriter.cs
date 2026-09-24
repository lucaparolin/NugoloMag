using System.Globalization;
using System.Net;
using System.Text;
using NugoloMag.Analyst.Application;
using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Infrastructure.Reports;

/// <summary>Report HTML autocontenuto (nessuna dipendenza esterna), con sparkline SVG e tema chiaro/scuro.</summary>
public sealed class HtmlReportWriter : IReportWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string Css = """
            :root{--bg:#f7f7f5;--card:#fff;--ink:#1d1d1f;--muted:#6b6b70;--line:#e3e3e0;--accent:#2f5bd3;--hi:#c2410c;--med:#b7791f;--lo:#5b7083;--recent:rgba(47,91,211,.08)}
            @media (prefers-color-scheme:dark){:root{--bg:#141416;--card:#1d1d20;--ink:#ececef;--muted:#9a9aa2;--line:#2e2e33;--accent:#7c9cff;--hi:#fb923c;--med:#facc15;--lo:#94a3b8;--recent:rgba(124,156,255,.12)}}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif}
            main{max-width:960px;margin:0 auto;padding:24px 16px 64px}
            h1{font-size:24px;margin:0 0 4px}.meta{color:var(--muted);font-size:13px}
            .kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:12px;margin:20px 0}
            .kpi{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px}.kpi b{display:block;font-size:22px}.kpi span{color:var(--muted);font-size:12px}
            .summary{background:var(--card);border:1px solid var(--line);border-left:3px solid var(--accent);border-radius:10px;padding:14px 18px;white-space:pre-wrap}
            h2{font-size:17px;margin:28px 0 10px}
            .f{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:14px 16px;margin:10px 0}
            .f h3{font-size:15px;margin:0 0 6px}.tags{display:flex;flex-wrap:wrap;gap:6px;font-size:12px;color:var(--muted);margin-bottom:8px}
            .tag{border:1px solid var(--line);border-radius:999px;padding:1px 8px}.sev-high{color:var(--hi);border-color:var(--hi)}.sev-medium{color:var(--med);border-color:var(--med)}.sev-low{color:var(--lo)}
            .grid{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:14px}@media(max-width:640px){.grid{grid-template-columns:1fr}}
            svg{width:100%;height:auto;display:block}ul{margin:0;padding-left:18px;font-size:13px}
            table{width:100%;border-collapse:collapse;font-size:12.5px;margin-top:8px}th,td{padding:4px 6px;border-bottom:1px solid var(--line);text-align:right}th:first-child,td:first-child,th:nth-child(2),td:nth-child(2){text-align:left}
            .bar{height:6px;background:var(--accent);border-radius:3px;display:inline-block;vertical-align:middle}
            .wrap{overflow-x:auto}
        """;

    public string FileExtension => ".html";

    public string Render(AnalysisReport report)
    {
        var sb = new StringBuilder();
        var high = report.Findings.Count(f => f.Severity == Severity.High);
        sb.Append($$"""
            <!doctype html>
            <html lang="it"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Analisi magazzini</title>
            <style>
            {{Css}}
            </style></head><body><main>
            <h1>Analisi magazzini al {{report.Window.AsOf:dd/MM/yyyy}}</h1>
            <div class="meta">Baseline {{report.Window.BaselineStart:dd/MM}}–{{report.Window.BaselineEnd:dd/MM}} · periodo recente {{report.Window.RecentStart:dd/MM}}–{{report.Window.AsOf:dd/MM}} · generato {{report.GeneratedAt.ToLocalTime():dd/MM/yyyy HH:mm}}</div>
            <div class="kpis">
              <div class="kpi"><b>{{report.Warehouses.Count}}</b><span>magazzini</span></div>
              <div class="kpi"><b>{{report.RowsAnalyzed.ToString("N0", CultureInfo.GetCultureInfo("it-IT"))}}</b><span>righe analizzate</span></div>
              <div class="kpi"><b>{{report.Findings.Count}}</b><span>cambiamenti rilevanti</span></div>
              <div class="kpi"><b style="color:var(--hi)">{{high}}</b><span>alta priorità</span></div>
            </div>
            <h2>Sintesi dell'analista</h2>
            <div class="summary">{{Enc(report.Narrative.Trim())}}</div>
            <h2>Cambiamenti rilevati, per importanza</h2>
            """);

        foreach (var (f, i) in report.Findings.Select((f, i) => (f, i + 1)))
        {
            sb.Append($"""<section class="f"><h3>{i}. {Enc(f.Headline)}</h3><div class="tags">""");
            sb.Append($"""<span class="tag sev-{f.Severity.Code()}">{MetricLabels.Italian(f.Severity)}</span><span class="tag">{Enc(MetricLabels.Italian(f.Kind))}</span>""");
            sb.Append($"""<span class="tag">magnitudo {f.Magnitude:0}/100</span><span class="tag">{Enc(f.Subject.ToString())}</span></div><div class="grid">""");

            sb.Append("<div>");
            if (f.History.Count > 1) sb.Append(Sparkline(f.History, report.Window));
            if (f.Evidence.Count > 0)
                sb.Append("<ul>").AppendJoin("", f.Evidence.Select(e => $"<li>{Enc(e.Key)}: <b>{Enc(e.Value)}</b></li>")).Append("</ul>");
            sb.Append("</div><div>");

            if (f.RootCauses.Count > 0)
            {
                sb.Append("""<div class="wrap"><table><tr><th>Driver</th><th>Segmento</th><th>Prima</th><th>Ora</th><th>Quota</th></tr>""");
                foreach (var c in f.RootCauses)
                {
                    var width = (int)Math.Round(Math.Clamp(c.Share, 0, 1) * 60);
                    sb.Append($"<tr><td>{Enc(c.Dimension)}</td><td>{Enc(c.Member)}</td><td>{Num(c.Baseline, f.Kind)}</td><td>{Num(c.Current, f.Kind)}</td>" +
                              $"""<td><span class="bar" style="width:{width}px"></span> {c.Share.ToString("P0", Inv)}</td></tr>""");
                }
                sb.Append("</table></div>");
            }
            sb.Append("</div></div></section>");
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static string Sparkline(IReadOnlyList<SeriesPoint> points, AnalysisWindow window)
    {
        const double w = 420, h = 90, pad = 4;
        var min = Math.Min(0, points.Min(p => p.Value));
        var max = points.Max(p => p.Value);
        var range = max - min == 0 ? 1 : max - min;
        double X(int i) => pad + i * (w - 2 * pad) / (points.Count - 1);
        double Y(double v) => h - pad - (v - min) / range * (h - 2 * pad);

        var path = string.Join(" ", points.Select((p, i) => $"{(i == 0 ? 'M' : 'L')}{X(i).ToString("0.#", Inv)},{Y(p.Value).ToString("0.#", Inv)}"));
        var recentIndex = points.ToList().FindIndex(p => p.Date >= window.RecentStart);
        var rx = X(Math.Max(recentIndex, 0));
        var last = points[^1];

        return $"""
            <svg viewBox="0 0 {w} {h}" role="img" aria-label="Andamento">
            <rect x="{rx.ToString("0.#", Inv)}" y="0" width="{(w - rx).ToString("0.#", Inv)}" height="{h}" fill="var(--recent)"/>
            <path d="{path}" fill="none" stroke="var(--accent)" stroke-width="1.8" stroke-linejoin="round"/>
            <circle cx="{X(points.Count - 1).ToString("0.#", Inv)}" cy="{Y(last.Value).ToString("0.#", Inv)}" r="3.5" fill="var(--hi)"/>
            </svg>
            """;
    }

    private static string Num(double v, FindingKind kind) =>
        kind == FindingKind.MixDrift ? v.ToString("P1", Inv) : MetricLabels.Number(v);

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
