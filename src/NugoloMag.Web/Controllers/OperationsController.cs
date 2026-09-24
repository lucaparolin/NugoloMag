using Microsoft.AspNetCore.Mvc;
using NugoloMag.Analyst.Application.Agentic;
using NugoloMag.Analyst.Domain.Agentic;
using NugoloMag.Web.Forms;
using NugoloMag.Web.ViewModels;

namespace NugoloMag.Web.Controllers;

/// <summary>Centro operativo: gli incidenti aperti per importanza, il caso completo, il feedback che fa imparare il sistema.</summary>
public sealed class OperationsController(IIncidentStore incidents, LearningAgent learning, TimeProvider clock) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var all = await incidents.ListAsync(null, includeResolved: true, take: 300, ct);
        return View(new OperationsViewModel(
            all.Where(i => i.IsOpen).OrderByDescending(i => i.SeverityScore).ToList(),
            all.Where(i => !i.IsOpen).OrderByDescending(i => i.UpdatedAt).Take(20).ToList()));
    }

    [HttpGet]
    public async Task<IActionResult> Incident(long id, CancellationToken ct)
    {
        var incident = await incidents.GetAsync(id, ct);
        return incident is null ? NotFound() : View(new IncidentViewModel(incident, await incidents.FeedbackAsync(id, ct)));
    }

    [HttpPost]
    public async Task<IActionResult> Feedback(long id, IFormCollection form, CancellationToken ct)
    {
        var incident = await incidents.GetAsync(id, ct);
        if (incident is null) return NotFound();

        var f = new FormReader(form);
        if (AgenticLabels.Parse(f.Text("verdict")) is not { } verdict)
            return View(nameof(Incident), new IncidentViewModel(incident, await incidents.FeedbackAsync(id, ct), "Scegliere un giudizio."));

        var note = f.Text("note");
        await learning.RecordAsync(new IncidentFeedback(id, clock.GetUtcNow(), verdict, note is { Length: > 1000 } ? note[..1000] : note, f.Text("user")), ct);
        return Redirect($"/Operations/Incident/{id}#feedback");
    }
}
