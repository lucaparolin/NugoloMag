using System.Text.Json;
using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Infrastructure.Reports;

public sealed class JsonReportWriter : IReportWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string FileExtension => ".json";

    public string Render(AnalysisReport report) => JsonSerializer.Serialize(ReportDto.From(report), Options);

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}
