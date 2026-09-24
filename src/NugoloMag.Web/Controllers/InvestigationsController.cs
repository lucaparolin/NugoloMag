using Microsoft.AspNetCore.Mvc;
using NugoloMag.Analyst.Application.Agentic;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Web.Forms;
using NugoloMag.Web.ViewModels;

namespace NugoloMag.Web.Controllers;

/// <summary>Domande operative in linguaggio naturale, trasformate dall'orchestratore in indagini multi-agente.</summary>
public sealed class InvestigationsController(
    IInvestigationStore investigations, WarehouseOrchestrator orchestrator, ISourceRegistry sources, ChatModelInfo? model) : Controller
{
    public const int MaxQuestionLength = 1000;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new InvestigationsViewModel(await investigations.ListAsync(30, ct), sources.Names, model));

    [HttpPost]
    public async Task<IActionResult> Ask(IFormCollection form, CancellationToken ct)
    {
        var f = new FormReader(form);
        var source = f.Text("source");
        var question = f.Text("question");
        string? error = null;
        if (source is null || !sources.Names.Contains(source, StringComparer.OrdinalIgnoreCase)) error = "Sorgente non configurata.";
        else if (question is null || question.Length > MaxQuestionLength) error = $"Scrivere una domanda (max {MaxQuestionLength} caratteri).";

        if (error is null)
        {
            try
            {
                var id = await orchestrator.AskAsync(source!, question!, ct);
                return RedirectToAction(nameof(Details), new RouteValueDictionary { ["id"] = id });
            }
            catch (InvalidOperationException ex)
            {
                error = ex.Message;
            }
        }
        return View(nameof(Index), new InvestigationsViewModel(await investigations.ListAsync(30, ct), sources.Names, model, error, question));
    }

    [HttpGet]
    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var item = await investigations.GetAsync(id, ct);
        return item is null ? NotFound() : View(item);
    }
}
