using Microsoft.AspNetCore.Mvc;
using NugoloMag.Analyst.Application.Monitoring;
using NugoloMag.Analyst.Infrastructure.Store;
using NugoloMag.Web.ViewModels;

namespace NugoloMag.Web.Controllers;

public sealed class RunsController(IMonitorStore store, IRunReportQuery reports) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var run = await store.GetRunAsync(id, ct);
        if (run is null) return NotFound();
        var monitor = await store.GetAsync(run.MonitorId, ct);
        if (monitor is null) return NotFound();
        return View(new RunDetailsViewModel(monitor, run, await reports.GetReportAsync(id, ct)));
    }
}
