using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Domain.Agentic;

namespace NugoloMag.Analyst.Application.Agentic;

/// <summary>Parametri economici e soglie condivisi dagli agenti.</summary>
public sealed record AgenticSettings
{
    /// <summary>Costo orario del lavoro di magazzino (€/h) per tradurre minuti persi in euro.</summary>
    public double LaborCostPerHour { get; init; } = 28;
    /// <summary>Scostamento minimo della produttività dall'atteso contestuale per considerarlo rilevante.</summary>
    public double MinProductivityGap { get; init; } = 0.08;
    public double MinProductivityZ { get; init; } = 3;
    /// <summary>Giorni di copertura sotto i quali un esaurimento accelerato diventa rischio di rottura.</summary>
    public double StockoutCoverDays { get; init; } = 7;
    public double OverstockCoverDays { get; init; } = 120;
    public double MinDepletionRatio { get; init; } = 1.8;
    /// <summary>Esposizione economica (€) oltre la quale l'impatto è considerato pienamente materiale.</summary>
    public double MaterialAmount { get; init; } = 5000;
    /// <summary>Giorni per cui si proietta l'impatto se non si interviene.</summary>
    public int ImpactHorizonDays { get; init; } = 5;
    public SeverityLevel EscalateToSupervisorFrom { get; init; } = SeverityLevel.High;
    public SeverityLevel EscalateToManagementFrom { get; init; } = SeverityLevel.Critical;
    /// <summary>Dopo quante segnalazioni "irrilevante" la stessa condizione viene declassata.</summary>
    public int DismissalsBeforeDemotion { get; init; } = 2;
}

/// <summary>
/// Tutto ciò che gli agenti possono osservare in un ciclo: dati di inventario e missioni sulla finestra,
/// incidenti aperti e (facoltativo) il database sorgente per le query dell'LLM.
/// </summary>
public sealed class AgentWorkspace(
    string sourceName,
    AnalysisWindow window,
    InventoryDataset inventory,
    TaskDataset tasks,
    DateTimeOffset now,
    IReadOnlyList<Incident> openIncidents,
    ISourceDatabase? source = null)
{
    public string SourceName => sourceName;
    public AnalysisWindow Window => window;
    public InventoryDataset Inventory => inventory;
    public TaskDataset Tasks => tasks;
    public DateTimeOffset Now => now;
    public IReadOnlyList<Incident> OpenIncidents => openIncidents;
    public ISourceDatabase? Source => source;

    public IReadOnlyList<string> Warehouses =>
        inventory.Warehouses.Select(w => w.Value).Concat(tasks.Warehouses.Select(w => w.Value)).Distinct().OrderBy(w => w).ToList();
}

/// <summary>Richiesta mirata di un agente a un altro (es. l'indagine chiede all'inventario la disponibilità di certi articoli).</summary>
public sealed record AgentQuery(string Warehouse, string Aspect, IReadOnlyList<string> Skus, string? Zone = null, string? Activity = null);

/// <summary>
/// Agente specialista: possiede un dominio, osserva in modo proattivo (senza domande) e risponde a richieste mirate
/// sempre con il contratto standard <see cref="AgentFinding"/>.
/// </summary>
public interface ISpecialistAgent
{
    string Name { get; }
    OperationalDomain Domain { get; }
    string Mission { get; }
    /// <summary>Aspetti che l'agente sa esaminare su richiesta, con descrizione (usati anche come strumenti per l'LLM).</summary>
    IReadOnlyDictionary<string, string> Aspects { get; }
    IReadOnlyList<AgentFinding> Observe(AgentWorkspace workspace);
    AgentFinding? Examine(AgentWorkspace workspace, AgentQuery query);
}

internal static class Evidence
{
    public static EvidenceItem Fact(string agent, string statement, params (string Key, string Value)[] data) =>
        new(ClaimKind.Fact, statement, agent, data.ToDictionary(d => d.Key, d => d.Value));

    public static EvidenceItem Signal(string agent, string statement, params (string Key, string Value)[] data) =>
        new(ClaimKind.Signal, statement, agent, data.ToDictionary(d => d.Key, d => d.Value));

    public static string Pct(double ratio) => (ratio >= 0 ? "+" : "") + ratio.ToString("P0", System.Globalization.CultureInfo.GetCultureInfo("it-IT"));
    public static string Num(double value) => value.ToString("#,0.#", System.Globalization.CultureInfo.GetCultureInfo("it-IT"));
}
