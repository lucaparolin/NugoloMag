using Microsoft.AspNetCore.Mvc;
using NugoloMag.Analyst.Application.Assistant;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Analyst.Domain.Assistant;
using NugoloMag.Web.Forms;
using NugoloMag.Web.ViewModels;

namespace NugoloMag.Web.Controllers;

/// <summary>Query di analisi salvate (dall'assistente o a mano): rieseguibili in sola lettura.</summary>
public sealed class QueriesController(ISavedQueryStore store, ISourceRegistry sources, TimeProvider clock) : Controller
{
    public const int MaxRows = 1000;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new QueriesIndexViewModel(await store.ListAsync(null, ct), sources.Names));

    [HttpGet]
    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var query = await store.GetAsync(id, ct);
        return query is null ? NotFound() : View(new QueryDetailsViewModel(query));
    }

    [HttpPost]
    public async Task<IActionResult> Run(long id, CancellationToken ct)
    {
        var query = await store.GetAsync(id, ct);
        if (query is null) return NotFound();

        var guard = ReadOnlySqlGuard.Check(query.Sql);
        if (!guard.IsAllowed) return View(nameof(Details), new QueryDetailsViewModel(query, Error: guard.Reason));
        try
        {
            var result = await sources.Get(query.SourceName).QueryAsync(query.Sql, MaxRows, ct);
            return View(nameof(Details), new QueryDetailsViewModel(query, result));
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or KeyNotFoundException or InvalidOperationException)
        {
            return View(nameof(Details), new QueryDetailsViewModel(query, Error: ex.Message));
        }
    }

    [HttpPost]
    public async Task<IActionResult> Create(IFormCollection form, CancellationToken ct)
    {
        var f = new FormReader(form);
        try
        {
            var source = f.Required("source", "Sorgente");
            if (!sources.Names.Contains(source, StringComparer.OrdinalIgnoreCase)) throw new FormException("Sorgente non configurata.");
            var sql = f.Required("sql", "SQL");
            var guard = ReadOnlySqlGuard.Check(sql);
            if (!guard.IsAllowed) throw new FormException(guard.Reason!);
            await sources.Get(source).QueryAsync(sql, 1, ct); // deve funzionare prima di essere salvata

            var id = await store.SaveAsync(new SavedQuery(0, source, f.Required("name", "Nome"), f.Text("description") ?? "", sql, "utente", null, clock.GetUtcNow()), ct);
            return RedirectToAction(nameof(Details), new RouteValueDictionary { ["id"] = id });
        }
        catch (Exception ex) when (ex is FormException or Microsoft.Data.SqlClient.SqlException)
        {
            return View(nameof(Index), new QueriesIndexViewModel(await store.ListAsync(null, ct), sources.Names, ex.Message));
        }
    }

    [HttpPost]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        await store.DeleteAsync(id, ct);
        return RedirectToAction(nameof(Index));
    }
}
