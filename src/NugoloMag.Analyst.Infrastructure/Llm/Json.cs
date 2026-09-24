using System.Text;
using System.Text.Json;

namespace NugoloMag.Analyst.Infrastructure.Llm;

/// <summary>Scrittura JSON esplicita con Utf8JsonWriter: nessuna serializzazione via reflection.</summary>
internal static class Json
{
    public static string Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            write(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Scrive un JSON già valido (es. uno schema o degli argomenti) come valore.</summary>
    public static void WriteRaw(Utf8JsonWriter writer, string propertyName, string json)
    {
        writer.WritePropertyName(propertyName);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        doc.RootElement.WriteTo(writer);
    }

    public static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
