using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Application;

/// <summary>"Decidere cosa conta": filtra il rumore e ordina per magnitudo.</summary>
public sealed class FindingRanker(DetectionSettings settings)
{
    public IReadOnlyList<Finding> Rank(IEnumerable<Finding> findings) =>
        findings
            .Where(f => f.Magnitude >= settings.MinMagnitude)
            .OrderByDescending(f => f.Magnitude)
            .ThenBy(f => f.Subject.ToString())
            .Take(settings.MaxFindings)
            .ToList();
}
