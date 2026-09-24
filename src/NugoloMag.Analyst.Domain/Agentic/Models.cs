namespace NugoloMag.Analyst.Domain.Agentic;

/// <summary>Fattori del modello di severità, ciascuno tra 0 e 1 (blueprint §19).</summary>
public sealed record SeverityInputs(double Magnitude, double Scope, double BusinessImpact, double Urgency, ConfidenceLevel Confidence);

/// <summary>
/// severità = magnitudo × ampiezza × impatto × urgenza × confidenza (blueprint §19).
/// Si usa una media geometrica pesata: resta moltiplicativa (un fattore nullo azzera il risultato)
/// ma non schiaccia verso zero cinque numeri minori di uno. L'impatto pesa più della sola ampiezza della deviazione.
/// La persistenza (§2.4) entra nell'urgenza: un picco di un giorno già passato è meno urgente di un calo che dura.
/// </summary>
public static class SeverityModel
{
    public static double Score(SeverityInputs i)
    {
        static double C(double v) => Math.Clamp(v, 0.001, 1);
        var confidence = i.Confidence switch { ConfidenceLevel.High => 1.0, ConfidenceLevel.Medium => 0.8, _ => 0.6 };
        return Math.Round(
            Math.Pow(C(i.Magnitude), 0.30) *
            Math.Pow(C(i.Scope), 0.15) *
            Math.Pow(C(i.BusinessImpact), 0.30) *
            Math.Pow(C(i.Urgency), 0.15) *
            Math.Pow(confidence, 0.10), 3);
    }

    public static SeverityLevel Level(SeverityInputs i) => Level(Score(i));

    public static SeverityLevel Level(double score) => score switch
    {
        >= 0.85 => SeverityLevel.Critical,
        >= 0.65 => SeverityLevel.High,
        >= 0.45 => SeverityLevel.Medium,
        >= 0.25 => SeverityLevel.Low,
        _ => SeverityLevel.Informational
    };
}

/// <summary>Modello di confidenza (blueprint §20).</summary>
public static class ConfidenceModel
{
    /// <param name="independentSupport">Segnali indipendenti a favore.</param>
    /// <param name="contradicting">Prove contrarie rilevanti.</param>
    /// <param name="plausibleAlternatives">Spiegazioni alternative ancora in piedi.</param>
    public static ConfidenceLevel Assess(int independentSupport, int contradicting, int plausibleAlternatives)
    {
        if (independentSupport >= 2 && contradicting == 0 && plausibleAlternatives == 0) return ConfidenceLevel.High;
        if (independentSupport >= 1 && contradicting <= independentSupport - 1 + (plausibleAlternatives == 0 ? 1 : 0)) return ConfidenceLevel.Medium;
        return ConfidenceLevel.Low;
    }

    public static ConfidenceLevel Min(ConfidenceLevel a, ConfidenceLevel b) => a < b ? a : b;
}
