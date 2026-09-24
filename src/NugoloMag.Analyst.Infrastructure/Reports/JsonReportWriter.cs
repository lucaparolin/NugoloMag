using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Infrastructure.Serialization;

namespace NugoloMag.Analyst.Infrastructure.Reports;

public sealed class JsonReportWriter : IReportWriter
{
    public string FileExtension => ".json";

    public string Render(AnalysisReport report) => NugoloJson.Serialize(ReportDocument.From(report));
}
