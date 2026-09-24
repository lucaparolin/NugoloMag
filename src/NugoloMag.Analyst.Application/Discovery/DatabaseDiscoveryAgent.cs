using NugoloMag.Analyst.Domain.Discovery;

namespace NugoloMag.Analyst.Application.Discovery;

/// <summary>
/// L'agente che studia il database prima di attivare il monitoraggio. Procede per passi verificabili:
/// schema → classificazione → profilazione → proposta di mapping → prova della query → (revisione LLM).
/// Ogni passo lascia una traccia leggibile, così l'utente vede perché l'agente ha deciso cosa.
/// </summary>
public sealed class DatabaseDiscoveryAgent(SourceValidator validator, ISchemaAdvisor advisor, TimeProvider clock)
{
    private const int ProfiledMovementTables = 3;
    private const int ProfiledSnapshotTables = 2;
    private const int MinHistoricSnapshotDays = 20;

    public async Task<DiscoveryReport> AnalyzeAsync(ISourceDatabase source, CancellationToken ct = default)
    {
        var trace = new Trace(clock);
        var today = Today();

        // 1. Schema
        DatabaseCatalog catalog;
        try
        {
            catalog = await trace.Run("Lettura dello schema", () => source.ReadCatalogAsync(ct), c =>
            [
                $"Database {c.Database}: {c.Tables.Count(t => !t.IsView)} tabelle, {c.Tables.Count(t => t.IsView)} viste, {c.ColumnCount:N0} colonne, {c.ForeignKeys.Count} relazioni."
            ]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            trace.Fail("Lettura dello schema", ex.Message);
            return Blocked(source.Name, "?", 0, 0, trace, [], []);
        }

        // 2. Classificazione
        var candidates = TableClassifier.Classify(catalog).ToList();
        trace.Add("Classificazione delle tabelle",
            candidates.Any(c => c.Role == TableRole.Movements) ? StepStatus.Ok : StepStatus.Failed,
            Describe(candidates));

        if (candidates.All(c => c.Role != TableRole.Movements))
            return Blocked(source.Name, catalog.Database, catalog.Tables.Count, catalog.ColumnCount, trace, candidates, TablesOf(catalog, candidates, null));

        // 3. Profilazione dei candidati principali
        var profiles = new Dictionary<TableName, TableProfile>();
        var profileNotes = new List<string>();
        var toProfile = candidates.Where(c => c.Role == TableRole.Movements).OrderByDescending(c => c.Score).Take(ProfiledMovementTables)
            .Concat(candidates.Where(c => c.Role == TableRole.StockSnapshot).OrderByDescending(c => c.Score).Take(ProfiledSnapshotTables))
            .ToList();

        await trace.Run("Profilazione dei dati", async () =>
        {
            foreach (var candidate in toProfile)
            {
                var profile = await source.ProfileAsync(candidate, today, ct);
                profiles[candidate.Table] = profile;
                var adjusted = Adjust(candidate, profile);
                candidates[candidates.IndexOf(candidate)] = adjusted;
                profileNotes.Add($"{candidate.Table}: {profile.Rows:N0} righe, date {profile.MinDate:dd/MM/yyyy}–{profile.MaxDate:dd/MM/yyyy}, " +
                                 $"{profile.DistinctItems:N0} articoli, {profile.DistinctWarehouses} magazzini, {profile.RowsLast30Days:N0} righe negli ultimi 30 giorni " +
                                 $"→ punteggio {candidate.Score} → {adjusted.Score}");
            }
            return profileNotes;
        }, notes => notes);

        // 4. Causali e proposta di mapping
        var movement = candidates.Where(c => c.Role == TableRole.Movements).OrderByDescending(c => c.Score).First();
        IReadOnlyList<CodeFrequency> codes = [];
        if (movement.ColumnFor(ColumnRole.MovementType) is { } typeColumn && movement.ColumnFor(ColumnRole.InboundQuantity) is null)
            codes = await source.TopValuesAsync(movement.Table, typeColumn, movement.ColumnFor(ColumnRole.Quantity), ct);

        var proposal = MappingProposer.Propose(catalog, candidates, profiles.GetValueOrDefault(movement.Table), codes);
        trace.Add("Proposta di mapping", proposal.Mapping is null ? StepStatus.Failed : StepStatus.Ok, proposal.Notes);

        var tables = TablesOf(catalog, candidates, proposal.Mapping);
        if (proposal.Mapping is null)
            return Blocked(source.Name, catalog.Database, catalog.Tables.Count, catalog.ColumnCount, trace, candidates, tables) with { MovementCodes = codes };

        var draft = new DiscoveryReport
        {
            SourceName = source.Name,
            Database = catalog.Database,
            CreatedAt = clock.GetUtcNow(),
            TableCount = catalog.Tables.Count,
            ColumnCount = catalog.ColumnCount,
            Steps = trace.Steps,
            Candidates = candidates,
            CandidateTables = tables,
            MovementCodes = codes,
            Readiness = Readiness.NeedsReview
        };

        // 5-6. Query, prova sui dati, revisione
        return await ValidateAndReviewAsync(source, draft, proposal.Mapping, trace, proposal.Codes, ct);
    }

    /// <summary>Riprova dopo le correzioni dell'utente. Il mapping è ammesso solo su tabelle e colonne del catalogo analizzato.</summary>
    public async Task<DiscoveryReport> RevalidateAsync(ISourceDatabase source, DiscoveryReport previous, SourceMapping mapping, CancellationToken ct = default)
    {
        var trace = new Trace(clock, previous.Steps.Where(s => !s.Title.StartsWith("Prova", StringComparison.Ordinal) && !s.Title.StartsWith("Revisione", StringComparison.Ordinal)));

        var errors = mapping.Validate(previous.ToCandidateCatalog());
        if (errors.Count > 0)
        {
            trace.Add("Revisione dell'utente", StepStatus.Failed, errors);
            return previous with { Steps = trace.Steps, Readiness = Readiness.Blocked, Mapping = mapping, Validation = null, SourceQuery = null };
        }

        var codes = previous.MovementCodes;
        if (mapping.Movements.TypeColumn is { } type &&
            !string.Equals(type, previous.Mapping?.Movements.TypeColumn, StringComparison.OrdinalIgnoreCase))
            codes = await source.TopValuesAsync(mapping.Movements.Table, type, mapping.Movements.QuantityColumn, ct);

        trace.Add("Revisione dell'utente", StepStatus.Ok, ["Mapping modificato manualmente e verificato sullo schema."]);
        var classification = mapping.Movements.Direction == MovementDirection.TypeCode && codes.Count > 0 ? MovementCodeClassifier.Classify(codes) : null;
        return await ValidateAndReviewAsync(source, previous with { MovementCodes = codes }, mapping, trace, classification, ct);
    }

    private async Task<DiscoveryReport> ValidateAndReviewAsync(
        ISourceDatabase source, DiscoveryReport draft, SourceMapping mapping, Trace trace, CodeClassification? codes, CancellationToken ct)
    {
        var query = source.BuildInventoryQuery(mapping);
        var validation = await trace.Run("Prova della query sui dati reali",
            () => validator.ValidateAsync(source, query, Today(), ct),
            v => v.Checks.Select(c => $"{Icon(c.Status)} {c.Name}: {c.Detail}").ToList(),
            v => v.HasFailures ? StepStatus.Failed : v.Checks.Any(c => c.Status == StepStatus.Warning) ? StepStatus.Warning : StepStatus.Ok);

        var readiness =
            validation.HasFailures ? Readiness.Blocked :
            validation.Checks.Any(c => c.Status == StepStatus.Warning) || codes is { ClassifiedShare: < 0.95 } ? Readiness.NeedsReview :
            Readiness.Ready;

        var report = draft with
        {
            Steps = trace.Steps,
            Mapping = mapping,
            SourceQuery = query,
            Validation = validation,
            Readiness = readiness,
            AdvisorNotes = null
        };

        try
        {
            var notes = await advisor.ReviewAsync(report, ct);
            if (notes is not null)
            {
                trace.Add("Revisione di Claude", StepStatus.Ok, ["Commento disponibile sotto la proposta."]);
                report = report with { Steps = trace.Steps, AdvisorNotes = notes };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            trace.Add("Revisione di Claude", StepStatus.Skipped, [$"Non disponibile: {ex.Message}"]);
            report = report with { Steps = trace.Steps };
        }

        return report;
    }

    private static TableCandidate Adjust(TableCandidate c, TableProfile p)
    {
        var reasons = c.Reasons.ToList();
        var score = c.Score;
        if (c.Role == TableRole.Movements)
        {
            if (p.RowsLast30Days > 0) { score += 15; reasons.Add($"attiva: {p.RowsLast30Days:N0} righe negli ultimi 30 giorni"); }
            else { score -= 30; reasons.Add("nessuna riga negli ultimi 30 giorni"); }
        }
        else if (c.Role == TableRole.StockSnapshot)
        {
            if (p.DistinctDatesLast30Days >= MinHistoricSnapshotDays) { score += 15; reasons.Add($"storico giornaliero ({p.DistinctDatesLast30Days} giorni su 30)"); }
            else { score -= 50; reasons.Add($"solo {p.DistinctDatesLast30Days} date negli ultimi 30 giorni: saldo corrente, non storico"); }
        }
        return c with { Score = Math.Clamp(score, 0, 100), Reasons = reasons };
    }

    private static IReadOnlyList<string> Describe(IReadOnlyList<TableCandidate> candidates)
    {
        var lines = new List<string>();
        foreach (var (role, label) in new[] { (TableRole.Movements, "Movimenti"), (TableRole.StockSnapshot, "Saldi"), (TableRole.ItemMaster, "Anagrafica articoli") })
        {
            var top = candidates.Where(c => c.Role == role).OrderByDescending(c => c.Score).Take(3).ToList();
            lines.Add(top.Count == 0
                ? $"{label}: nessun candidato"
                : $"{label}: " + string.Join("; ", top.Select(c => $"{c.Table} ({c.Score}: {string.Join(", ", c.Reasons.Take(3))})")));
        }
        return lines;
    }

    private static IReadOnlyList<CatalogTable> TablesOf(DatabaseCatalog catalog, IEnumerable<TableCandidate> candidates, SourceMapping? mapping)
    {
        var names = candidates.Select(c => c.Table).ToHashSet();
        if (mapping?.Items is { } items) names.Add(items.Table);
        if (mapping?.Snapshot is { } snap) names.Add(snap.Table);
        return names.Select(catalog.Find).OfType<CatalogTable>().ToList();
    }

    private DiscoveryReport Blocked(string source, string database, int tables, int columns, Trace trace,
        IReadOnlyList<TableCandidate> candidates, IReadOnlyList<CatalogTable> candidateTables) => new()
    {
        SourceName = source,
        Database = database,
        CreatedAt = clock.GetUtcNow(),
        TableCount = tables,
        ColumnCount = columns,
        Steps = trace.Steps,
        Candidates = candidates,
        CandidateTables = candidateTables,
        Readiness = Readiness.Blocked
    };

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private static string Icon(StepStatus s) => s switch { StepStatus.Ok => "✓", StepStatus.Warning => "!", StepStatus.Failed => "✗", _ => "·" };

    /// <summary>Registra i passi con la loro durata.</summary>
    private sealed class Trace(TimeProvider clock, IEnumerable<AgentStep>? initial = null)
    {
        private readonly List<AgentStep> _steps = initial?.ToList() ?? [];
        public IReadOnlyList<AgentStep> Steps => _steps.ToList();

        public void Add(string title, StepStatus status, IReadOnlyList<string> details, double seconds = 0) =>
            _steps.Add(new AgentStep(title, status, details, Math.Round(seconds, 2)));

        public void Fail(string title, string error) => Add(title, StepStatus.Failed, [error]);

        public async Task<T> Run<T>(string title, Func<Task<T>> action, Func<T, IReadOnlyList<string>> describe, Func<T, StepStatus>? status = null)
        {
            var start = clock.GetTimestamp();
            var result = await action();
            Add(title, status?.Invoke(result) ?? StepStatus.Ok, describe(result), clock.GetElapsedTime(start).TotalSeconds);
            return result;
        }
    }
}
