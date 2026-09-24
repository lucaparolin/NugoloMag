using Microsoft.AspNetCore.Mvc;
using NugoloMag.Analyst.Application.Agentic;
using NugoloMag.Analyst.Application.Monitoring;
using NugoloMag.Web.Forms;
using NugoloMag.Web.ViewModels;

namespace NugoloMag.Web.Controllers;

/// <summary>Briefing direzionale: l'ultimo per sorgente, lo storico, e l'avvio manuale di un ciclo di osservazione.</summary>
public sealed class BriefingController(
    IBriefingStore briefings, IMonitorStore monitors, WarehouseOrchestrator orchestrator, TimeProvider clock, TimeZoneInfo zone) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var history = await briefings.ListAsync(30, ct);
        var latest = history.Count == 0 ? null : await briefings.GetAsync(history[0].Id, ct);
        return View(new BriefingViewModel(latest, history, await monitors.ListAsync(ct)));
    }

    [HttpGet]
    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var briefing = await briefings.GetAsync(id, ct);
        return briefing is null ? NotFound() : View(nameof(Index), new BriefingViewModel(briefing, await briefings.ListAsync(30, ct), await monitors.ListAsync(ct)));
    }

    /// <summary>Esegue subito il ciclo agentico sulla giornata di ieri per il monitoraggio scelto.</summary>
    [HttpPost]
    public async Task<IActionResult> Run(IFormCollection form, CancellationToken ct)
    {
        var id = long.TryParse(new FormReader(form).Text("monitor"), out var m) ? m : 0;
        var monitor = await monitors.GetAsync(id, ct);
        if (monitor is null) return BadRequest("Monitoraggio non trovato.");

        var asOf = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime).AddDays(-1);
        var cycle = await orchestrator.RunCycleAsync(monitor, asOf, ct);
        return RedirectToAction(nameof(Details), new RouteValueDictionary { ["id"] = cycle.Briefing.Id });
    }
}
