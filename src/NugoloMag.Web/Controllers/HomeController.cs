using Microsoft.AspNetCore.Mvc;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Application.Monitoring;
using NugoloMag.Web.ViewModels;

namespace NugoloMag.Web.Controllers;

public sealed class HomeController(IMonitorStore monitors, IDiscoveryStore discoveries, ISourceRegistry sources, bool claudeEnabled) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var rows = new List<MonitorRow>();
        foreach (var monitor in await monitors.ListAsync(ct))
            rows.Add(new MonitorRow(monitor, (await monitors.ListRunsAsync(monitor.Id, 1, ct)).FirstOrDefault()));

        return View(new DashboardViewModel(rows, await discoveries.ListAsync(10, ct), sources.Names, claudeEnabled));
    }

    [HttpGet]
    public IActionResult Error() => View();
}
