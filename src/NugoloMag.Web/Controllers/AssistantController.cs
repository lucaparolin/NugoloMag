using Microsoft.AspNetCore.Mvc;
using NugoloMag.Analyst.Application.Assistant;
using NugoloMag.Analyst.Application.Discovery;
using NugoloMag.Web.Forms;
using NugoloMag.Web.ViewModels;

namespace NugoloMag.Web.Controllers;

/// <summary>Conversazione con l'assistente dati: domande in linguaggio naturale, query create ed eseguite in autonomia.</summary>
public sealed class AssistantController(ConversationService service, IConversationStore store, ISourceRegistry sources) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new AssistantIndexViewModel(await store.ListAsync(30, ct), sources.Names, service.IsAgentAvailable));

    [HttpPost]
    public async Task<IActionResult> Start(IFormCollection form, CancellationToken ct)
    {
        var source = new FormReader(form).Text("source");
        if (source is null || !sources.Names.Contains(source, StringComparer.OrdinalIgnoreCase)) return BadRequest("Sorgente non configurata.");
        var id = await service.StartAsync(source, ct);
        return RedirectToAction(nameof(Chat), new RouteValueDictionary { ["id"] = id });
    }

    [HttpGet]
    public async Task<IActionResult> Chat(long id, CancellationToken ct)
    {
        var conversation = await store.GetAsync(id, ct);
        return conversation is null ? NotFound() : View(new ChatViewModel(conversation, await store.EntriesAsync(id, ct), service.IsAgentAvailable));
    }

    [HttpPost]
    public async Task<IActionResult> Chat(long id, IFormCollection form, CancellationToken ct)
    {
        var conversation = await store.GetAsync(id, ct);
        if (conversation is null) return NotFound();

        var message = new FormReader(form).Text("message") ?? "";
        try
        {
            await service.SendAsync(id, message, ct);
        }
        catch (ArgumentException ex)
        {
            return View(new ChatViewModel(conversation, await store.EntriesAsync(id, ct), service.IsAgentAvailable, ex.Message, message));
        }
        return Redirect($"/Assistant/Chat/{id}#fine");
    }
}
