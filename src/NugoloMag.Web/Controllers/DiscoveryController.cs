using Microsoft.AspNetCore.Mvc;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Application.Monitoring;
using NugoloMag.Analyst.Domain.Discovery;
using NugoloMag.Web.Forms;
using NugoloMag.Web.ViewModels;

namespace NugoloMag.Web.Controllers;

/// <summary>
/// Passo 1: l'agente analizza il database. Passo 2: l'utente rivede e corregge. Passo 3: si attiva il monitoraggio.
/// </summary>
public sealed class DiscoveryController(
    ISourceRegistry sources,
    IDiscoveryStore store,
    DatabaseDiscoveryAgent agent,
    MonitoringService monitoring) : Controller
{
    [HttpPost]
    public async Task<IActionResult> Start(IFormCollection form, CancellationToken ct)
    {
        var source = new FormReader(form).Text("source");
        if (source is null || !sources.Names.Contains(source, StringComparer.OrdinalIgnoreCase))
            return BadRequest("Sorgente non configurata.");

        var report = await agent.AnalyzeAsync(sources.Get(source), ct);
        var id = await store.SaveAsync(report, ct);
        return RedirectToAction(nameof(Review), new RouteValueDictionary { ["id"] = id });
    }

    [HttpGet]
    public async Task<IActionResult> Review(long id, CancellationToken ct)
    {
        var report = await store.GetAsync(id, ct);
        return report is null ? NotFound() : View(new DiscoveryReviewViewModel(report, MappingForm.FromMapping(report.Mapping, report.MovementCodes)));
    }

    [HttpPost]
    public async Task<IActionResult> Review(long id, IFormCollection form, CancellationToken ct)
    {
        var report = await store.GetAsync(id, ct);
        if (report is null) return NotFound();

        var reader = new FormReader(form);
        MappingForm posted;
        try
        {
            posted = MappingForm.FromForm(reader);
        }
        catch (FormException ex)
        {
            return View(new DiscoveryReviewViewModel(report, MappingForm.FromMapping(report.Mapping, report.MovementCodes), ex.Message));
        }

        // Cambio di tabella nel form: si ripropone la pagina con le colonne suggerite per la nuova tabella, senza toccare il DB.
        if (reader.Text("action") == "refresh")
        {
            var previous = MappingForm.FromMapping(report.Mapping, report.MovementCodes);
            return View(new DiscoveryReviewViewModel(report, posted.WithSuggestionsFor(previous, report.Candidates)));
        }

        SourceMapping mapping;
        try
        {
            mapping = posted.ToMapping();
        }
        catch (FormException ex)
        {
            return View(new DiscoveryReviewViewModel(report, posted, ex.Message));
        }

        var updated = await agent.RevalidateAsync(sources.Get(report.SourceName), report, mapping, ct);
        await store.UpdateAsync(updated, ct);
        return RedirectToAction(nameof(Review), new RouteValueDictionary { ["id"] = id });
    }

    [HttpPost]
    public async Task<IActionResult> Activate(long id, IFormCollection form, CancellationToken ct)
    {
        var report = await store.GetAsync(id, ct);
        if (report is null) return NotFound();

        try
        {
            var monitorId = await monitoring.ActivateAsync(ActivationForm.Read(id, new FormReader(form)), ct);
            return RedirectToAction("Details", "Monitors", new RouteValueDictionary { ["id"] = monitorId });
        }
        catch (Exception ex) when (ex is FormException or ArgumentException or InvalidOperationException)
        {
            return View(nameof(Review), new DiscoveryReviewViewModel(report, MappingForm.FromMapping(report.Mapping, report.MovementCodes), ActivationError: ex.Message));
        }
    }
}
