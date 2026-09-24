using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace NugoloMag.Web.Forms;

/// <summary>Lettura esplicita e tipizzata dei campi del form (al posto del model binding basato su reflection).</summary>
public sealed class FormReader(IFormCollection form)
{
    public string? Text(string key)
    {
        var value = form[key].ToString().Trim();
        return value.Length == 0 ? null : value;
    }

    public string Required(string key, string label) =>
        Text(key) ?? throw new FormException($"{label}: campo obbligatorio.");

    public bool Flag(string key) => form[key].Any(v => v is "on" or "true");

    public int Int(string key, string label, int min, int max)
    {
        if (!int.TryParse(Text(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new FormException($"{label}: inserire un numero tra {min} e {max}.");
        return value;
    }

    public TimeOnly Time(string key, string label) =>
        TimeOnly.TryParseExact(Text(key), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : throw new FormException($"{label}: formato HH:mm.");

    public IEnumerable<string> Keys => form.Keys;
}

/// <summary>Errore di compilazione del form, da mostrare all'utente.</summary>
public sealed class FormException(string message) : Exception(message);
