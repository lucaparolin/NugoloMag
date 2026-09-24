namespace NugoloMag.Analyst.Application;

/// <summary>Soglie di rilevamento. I default sono prudenti: meglio pochi finding rilevanti che rumore.</summary>
public sealed record DetectionSettings
{
    /// <summary>|z| minimo per segnalare un picco o un crollo.</summary>
    public double ZThreshold { get; init; } = 4.0;
    /// <summary>Variazione relativa minima rispetto all'atteso (0.25 = 25%).</summary>
    public double MinRelativeChange { get; init; } = 0.25;
    /// <summary>Variazione assoluta minima in unità, per evitare allarmi su volumi minuscoli.</summary>
    public double MinAbsoluteDelta { get; init; } = 5;
    /// <summary>Uscita media giornaliera minima perché un articolo sia analizzato singolarmente.</summary>
    public double MinSkuDailyVolume { get; init; } = 2;
    /// <summary>Variazione minima di pendenza (% al giorno) per un'inversione di trend.</summary>
    public double MinTrendChangePerDay { get; init; } = 0.02;
    /// <summary>Population Stability Index minimo per segnalare un cambio di mix.</summary>
    public double MinPsi { get; init; } = 0.1;
    /// <summary>Giorni consecutivi senza uscite per dichiarare fermo un articolo abitualmente movimentato.</summary>
    public int StallDays { get; init; } = 3;
    /// <summary>Quota minima di giorni con uscite in baseline per considerare un articolo "alto rotante".</summary>
    public double RegularMoverRatio { get; init; } = 0.6;
    /// <summary>Rapporto minimo righe oggi / righe tipiche sotto il quale il caricamento è parziale.</summary>
    public double MinLoadCompleteness { get; init; } = 0.7;
    /// <summary>Tolleranza di quadratura giacenza = ieri + entrate - uscite + rettifiche.</summary>
    public double ReconciliationTolerance { get; init; } = 0.001;
    public double MinMagnitude { get; init; } = 20;
    public int MaxFindings { get; init; } = 40;
}
