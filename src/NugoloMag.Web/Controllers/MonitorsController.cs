using Microsoft.AspNetCore.Mvc;
using NugoloMag.Analyst.Application.Monitoring;
using NugoloMag.Web.ViewModels;

namespace NugoloMag.Web.Controllers;

public sealed class MonitorsController(IMonitorStore store, MonitoringService monitoring, TimeZoneInfo zone) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var monitor = await store.GetAsync(id, ct);
        return monitor is null ? NotFound() : View(new MonitorDetailsViewModel(monitor, await store.ListRunsAsync(id, 30, ct), zone));
    }

    [HttpPost]
    public async Task<IActionResult> RunNow(long id, CancellationToken ct)
    {
        await monitoring.RunNowAsync(id, ct);
        return RedirectToAction(nameof(Details), new RouteValueDictionary { ["id"] = id });
    }

    [HttpPost]
    public async Task<IActionResult> Pause(long id, CancellationToken ct)
    {
        await monitoring.PauseAsync(id, ct);
        return RedirectToAction(nameof(Details), new RouteValueDictionary { ["id"] = id });
    }

    [HttpPost]
    public async Task<IActionResult> Resume(long id, CancellationToken ct)
    {
        await monitoring.ResumeAsync(id, ct);
        return RedirectToAction(nameof(Details), new RouteValueDictionary { ["id"] = id });
    }
}
