using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NugoloMag.Analyst.Domain.Discovery;
using NugoloMag.Analyst.Infrastructure.Reports;

namespace NugoloMag.Analyst.Infrastructure.Serialization;

/// <summary>
/// Contesto JSON generato a compile-time: nessuna reflection a runtime per serializzare/deserializzare.
/// Ogni tipo persistito o scambiato va dichiarato qui; gli enum sono salvati come testo.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ReportDocument))]
[JsonSerializable(typeof(DiscoveryReport))]
[JsonSerializable(typeof(SourceMapping))]
[JsonSerializable(typeof(NugoloMag.Analyst.Domain.Agentic.Incident))]
[JsonSerializable(typeof(NugoloMag.Analyst.Domain.Agentic.ExecutiveBriefing))]
[JsonSerializable(typeof(NugoloMag.Analyst.Domain.Agentic.InvestigationCase))]
public sealed partial class NugoloJsonContext : JsonSerializerContext;

public static class NugoloJson
{
    /// <summary>Stesse impostazioni del contesto, ma con le lettere accentate leggibili nel JSON salvato.</summary>
    public static readonly NugoloJsonContext Context = new(new JsonSerializerOptions(NugoloJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });

    public static string Serialize(ReportDocument value) => JsonSerializer.Serialize(value, Context.ReportDocument);
    public static string Serialize(DiscoveryReport value) => JsonSerializer.Serialize(value, Context.DiscoveryReport);
    public static string Serialize(SourceMapping value) => JsonSerializer.Serialize(value, Context.SourceMapping);

    public static string Serialize(NugoloMag.Analyst.Domain.Agentic.Incident value) => JsonSerializer.Serialize(value, Context.Incident);
    public static string Serialize(NugoloMag.Analyst.Domain.Agentic.ExecutiveBriefing value) => JsonSerializer.Serialize(value, Context.ExecutiveBriefing);
    public static string Serialize(NugoloMag.Analyst.Domain.Agentic.InvestigationCase value) => JsonSerializer.Serialize(value, Context.InvestigationCase);
    public static NugoloMag.Analyst.Domain.Agentic.Incident ReadIncident(string json) => JsonSerializer.Deserialize(json, Context.Incident) ?? throw new JsonException("JSON vuoto.");
    public static NugoloMag.Analyst.Domain.Agentic.ExecutiveBriefing ReadBriefing(string json) => JsonSerializer.Deserialize(json, Context.ExecutiveBriefing) ?? throw new JsonException("JSON vuoto.");
    public static NugoloMag.Analyst.Domain.Agentic.InvestigationCase ReadInvestigation(string json) => JsonSerializer.Deserialize(json, Context.InvestigationCase) ?? throw new JsonException("JSON vuoto.");

    public static ReportDocument ReadReport(string json) => JsonSerializer.Deserialize(json, Context.ReportDocument) ?? throw new JsonException("JSON vuoto.");
    public static DiscoveryReport ReadDiscovery(string json) => JsonSerializer.Deserialize(json, Context.DiscoveryReport) ?? throw new JsonException("JSON vuoto.");
    public static SourceMapping ReadMapping(string json) => JsonSerializer.Deserialize(json, Context.SourceMapping) ?? throw new JsonException("JSON vuoto.");
}
