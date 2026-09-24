using Microsoft.AspNetCore.Mvc;
using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Infrastructure.Llm;
using NugoloMag.Web.Forms;
using NugoloMag.Web.ViewModels;

namespace NugoloMag.Web.Controllers;

/// <summary>Connettori LLM configurati, agente → connettore, skill caricate, e collaudo di un connettore.</summary>
public sealed class ConnectorsController(LlmConnectorRegistry connectors, AgentLlm agents, LlmDiagnostics diagnostics) : Controller
{
    [HttpGet]
    public IActionResult Index() => View(Build(null, null));

    [HttpPost]
    public async Task<IActionResult> Test(IFormCollection form, CancellationToken ct)
    {
        var name = new FormReader(form).Text("connector");
        if (name is null || connectors.Options(name) is not { } options)
            return View(nameof(Index), Build(null, "Connettore non configurato."));

        IChatModel? model;
        try { model = connectors.Get(name); }
        catch (Exception ex) when (ex is LlmException or ArgumentException) { return View(nameof(Index), Build(null, ex.Message)); }

        var report = await diagnostics.RunAsync(options, model, ct);
        return View(nameof(Index), Build(report, null));
    }

    private ConnectorsViewModel Build(ConnectorReport? report, string? error)
    {
        var routes = SkillIds.All.Select(id =>
        {
            try
            {
                var skill = agents.Skills.Get(id);
                var connector = agents.ConnectorFor(id);
                var origin = connectors.Settings.Agents.TryGetValue(id, out var r) && !string.IsNullOrWhiteSpace(r) ? "configurazione (Llm:Agents)"
                    : skill.Connector is not null ? "skill (connector)" : "predefinito (Llm:Default)";
                return new AgentRouteRow(id, skill.Name, skill.Description, connector ?? "none", origin, null);
            }
            catch (Exception ex) when (ex is SkillException or LlmException)
            {
                return new AgentRouteRow(id, id, "", "—", "", ex.Message);
            }
        }).ToList();

        var rows = connectors.Settings.Connectors.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new ConnectorRow(
            c,
            string.Equals(c.Name, connectors.DefaultConnector, StringComparison.OrdinalIgnoreCase),
            routes.Where(r => string.Equals(r.Connector, c.Name, StringComparison.OrdinalIgnoreCase)).Select(r => r.AgentId).ToList(),
            c.ApiKeyEnvironmentVariable is not { Length: > 0 } env ? "non richiesta" : string.IsNullOrEmpty(c.ApiKey) ? $"{env} NON impostata" : $"{env} impostata"))
            .ToList();

        return new ConnectorsViewModel(rows, routes, agents.Skills.All(), (agents.Skills as FileSkillLibrary)?.Root, report, error);
    }
}
