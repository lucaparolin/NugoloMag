using NugoloMag.Analyst.Application.Monitoring;

namespace NugoloMag.Web.Forms;

public static class ActivationForm
{
    public static ActivationRequest Read(long discoveryId, FormReader f) => new(
        discoveryId,
        f.Required("name", "Nome"),
        f.Time("runAt", "Orario di esecuzione"),
        f.Int("baselineDays", "Giorni di baseline", 7, 365),
        f.Int("recentDays", "Giorni recenti", 1, 60));
}
