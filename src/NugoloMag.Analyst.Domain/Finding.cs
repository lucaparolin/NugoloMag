namespace NugoloMag.Analyst.Domain;

public enum FindingKind
{
    /// <summary>Valore del giorno molto sopra il normale.</summary>
    Spike,
    /// <summary>Valore del giorno molto sotto il normale.</summary>
    Drop,
    /// <summary>Il trend recente ha invertito direzione rispetto alla baseline.</summary>
    TrendReversal,
    /// <summary>Articolo mai visto nella baseline che inizia a muoversi.</summary>
    NewItem,
    /// <summary>Articolo regolarmente movimentato che ha smesso di uscire pur avendo giacenza.</summary>
    StalledItem,
    /// <summary>Articolo regolarmente movimentato ora a giacenza zero.</summary>
    Stockout,
    /// <summary>Il mix per categoria delle uscite è cambiato.</summary>
    MixDrift,
    /// <summary>Dati mancanti o caricamento parziale.</summary>
    DataFreshness,
    /// <summary>Giacenze negative o che non quadrano con i movimenti.</summary>
    Integrity
}

public enum Severity { Low, Medium, High }

/// <summary>Contributo di un segmento (categoria o articolo) alla variazione osservata.</summary>
/// <summary>Un punto della serie storica mostrata a corredo del finding.</summary>
public readonly record struct SeriesPoint(DateOnly Date, double Value);

public sealed record Contribution(string Dimension, string Member, double Baseline, double Current, double Delta, double Share);

/// <summary>
/// Un cambiamento rilevato nei dati. Il punteggio <see cref="Magnitude"/> (0-100) combina
/// quanto il cambiamento è statisticamente sorprendente e quanto pesa sul magazzino.
/// </summary>
public sealed record Finding
{
    public required FindingKind Kind { get; init; }
    public required Subject Subject { get; init; }
    public Metric? Metric { get; init; }
    public required DateOnly Date { get; init; }
    public required double Magnitude { get; init; }
    public required string Headline { get; init; }
    public double? Baseline { get; init; }
    public double? Observed { get; init; }
    public IReadOnlyDictionary<string, string> Evidence { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<Contribution> RootCauses { get; init; } = [];
    /// <summary>Andamento della metrica sul periodo analizzato, per i grafici del report.</summary>
    public IReadOnlyList<SeriesPoint> History { get; init; } = [];

    public Severity Severity => Magnitude switch
    {
        >= 70 => Severity.High,
        >= 40 => Severity.Medium,
        _ => Severity.Low
    };

    public double? RelativeChange => Baseline is { } b && Observed is { } o && b != 0 ? (o - b) / Math.Abs(b) : null;
}
