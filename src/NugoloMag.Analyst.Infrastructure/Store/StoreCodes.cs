using NugoloMag.Analyst.Domain.Discovery;
using NugoloMag.Analyst.Domain.Monitoring;

namespace NugoloMag.Analyst.Infrastructure.Store;

/// <summary>Codici di stato salvati nel DB, mappati esplicitamente nei due sensi.</summary>
internal static class StoreCodes
{
    public static string Code(Readiness r) => r switch
    {
        Readiness.Ready => "ready",
        Readiness.NeedsReview => "needs-review",
        Readiness.Blocked => "blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(r))
    };

    public static Readiness ParseReadiness(string code) => code switch
    {
        "ready" => Readiness.Ready,
        "needs-review" => Readiness.NeedsReview,
        "blocked" => Readiness.Blocked,
        _ => throw new FormatException($"Readiness sconosciuta: {code}")
    };

    public static string Code(RunStatus s) => s switch
    {
        RunStatus.Running => "running",
        RunStatus.Succeeded => "succeeded",
        RunStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(s))
    };

    public static RunStatus ParseRunStatus(string code) => code switch
    {
        "running" => RunStatus.Running,
        "succeeded" => RunStatus.Succeeded,
        "failed" => RunStatus.Failed,
        _ => throw new FormatException($"Stato esecuzione sconosciuto: {code}")
    };
}
