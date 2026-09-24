using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Application.Abstractions;

/// <summary>Sorgente dati delle posizioni di magazzino (SQL via ADO.NET, CSV, ...).</summary>
public interface IInventoryRepository
{
    Task<IReadOnlyList<StockDay>> LoadAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}

/// <summary>Sorgente delle missioni di magazzino (per la produttività).</summary>
public interface ITaskRepository
{
    Task<IReadOnlyList<WarehouseTask>> LoadAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}

/// <summary>Un singolo "agente" statistico che cerca un tipo di cambiamento. Nessun LLM qui.</summary>
public interface IChangeDetector
{
    string Name { get; }
    IEnumerable<Finding> Detect(InventoryDataset data, AnalysisWindow window);
}

/// <summary>Spiega una variazione scomponendola nei segmenti che la causano.</summary>
public interface IRootCauseAnalyzer
{
    IReadOnlyList<Contribution> Explain(Finding finding, InventoryDataset data, AnalysisWindow window);
}

/// <summary>Trasforma i findings in un testo da analista (template deterministico o LLM).</summary>
public interface IInsightNarrator
{
    Task<string> NarrateAsync(AnalysisReport report, CancellationToken ct = default);
    Task<string> AnswerAsync(AnalysisReport report, string question, CancellationToken ct = default);
}

public interface IReportWriter
{
    string FileExtension { get; }
    string Render(AnalysisReport report);
}
