namespace NugoloMag.Analyst.Domain;

/// <summary>
/// Regola unica di punteggio: sorpresa statistica (z-score o equivalente) pesata per impatto sul business (0-1).
/// Come un triage di pronto soccorso: non basta che il sintomo sia strano, deve anche essere grave.
/// </summary>
public static class MagnitudeScore
{
    public static double From(double surprise, double impact)
    {
        var s = 1 - Math.Exp(-Math.Abs(surprise) / 6d);
        var i = Math.Clamp(impact, 0, 1);
        return Math.Round(100 * s * (0.4 + 0.6 * i), 1);
    }
}
