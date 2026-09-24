using System.Net;
using System.Text;
using Microsoft.AspNetCore.Html;
using NugoloMag.Analyst.Domain.Discovery;

namespace NugoloMag.Web.ViewModels;

/// <summary>Controlli del form di mapping. Ogni valore è codificato HTML.</summary>
public static class FormControls
{
    public static IHtmlContent ColumnSelect(string name, IReadOnlyList<CatalogColumn> columns, string? selected, bool optional, string emptyLabel = "(nessuna)")
    {
        var sb = new StringBuilder($"<select name=\"{Enc(name)}\">");
        if (optional || selected is null) sb.Append($"<option value=\"\">{Enc(emptyLabel)}</option>");
        foreach (var c in columns)
        {
            var isSelected = string.Equals(c.Name, selected, StringComparison.OrdinalIgnoreCase) ? " selected" : "";
            sb.Append($"<option value=\"{Enc(c.Name)}\"{isSelected}>{Enc(c.Name)} ({Enc(c.SqlType)})</option>");
        }
        return new HtmlString(sb.Append("</select>").ToString());
    }

    public static IHtmlContent TableSelect(string name, IReadOnlyList<string> tables, string? selected)
    {
        var sb = new StringBuilder($"<select name=\"{Enc(name)}\" data-refresh>");
        if (selected is null) sb.Append("<option value=\"\">(scegli)</option>");
        foreach (var t in tables)
        {
            var isSelected = string.Equals(t, selected, StringComparison.OrdinalIgnoreCase) ? " selected" : "";
            sb.Append($"<option value=\"{Enc(t)}\"{isSelected}>{Enc(t)}</option>");
        }
        return new HtmlString(sb.Append("</select>").ToString());
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
