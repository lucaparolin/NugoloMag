namespace NugoloMag.Analyst.Application.Llm;

/// <summary>I connettori LLM configurati, per nome (sezione Llm:Connectors).</summary>
public interface IConnectorCatalog
{
    string? DefaultConnector { get; }
    IReadOnlyList<string> Names { get; }
    /// <summary>Null se il connettore è disattivato (provider "none").</summary>
    IChatModel? Get(string name);
}

/// <summary>Un agente pronto a parlare con un modello: connettore scelto, skill aggiornata, protocollo per gli strumenti emulati.</summary>
public sealed record AgentBinding(string AgentId, string Connector, IChatModel Model, Skill Skill, string ToolProtocol)
{
    public string System(IReadOnlyDictionary<string, string?>? values = null) => SkillTemplate.Render(Skill.Instructions, values);

    public string Prompt(string name, IReadOnlyDictionary<string, string?>? values = null) => SkillTemplate.Render(Skill.Prompt(name), values);

    /// <summary>Una sola richiesta, senza strumenti: sistema = skill, utente = testo.</summary>
    public Task<ChatResponse> CompleteAsync(string system, string user, CancellationToken ct = default) =>
        Model.CompleteAsync(new ChatRequest(system, [ChatMessage.User(user)], [], MaxTokens: Skill.MaxTokens(4000)), ct);

    /// <summary>Ciclo con strumenti, con i limiti della skill.</summary>
    public Task<LoopResult> RunToolsAsync(string system, IReadOnlyList<ChatMessage> history, IToolbox tools,
        Func<ToolCall, bool>? stopAfter = null, CancellationToken ct = default) =>
        ToolLoop.RunAsync(Model, system, history, tools, Skill.MaxRounds(15), stopAfter, ToolProtocol, Skill.MaxTokens(4000), ct);
}

/// <summary>
/// Collega agenti, skill e connettori. Il connettore di un agente si sceglie così:
/// 1) instradamento in configurazione (Llm:Agents:&lt;agente&gt;), 2) <c>connector</c> nella skill, 3) connettore predefinito.
/// La skill si rilegge a ogni uso: modificare un file in agents/ non richiede riavvio.
/// </summary>
public sealed class AgentLlm(ISkillLibrary skills, IConnectorCatalog connectors, IReadOnlyDictionary<string, string>? routes = null)
{
    private readonly IReadOnlyDictionary<string, string> _routes =
        routes is null ? new Dictionary<string, string>() : new Dictionary<string, string>(routes, StringComparer.OrdinalIgnoreCase);

    public ISkillLibrary Skills => skills;
    public IConnectorCatalog Connectors => connectors;

    public string? ConnectorFor(string agentId)
    {
        if (_routes.TryGetValue(agentId, out var routed) && routed.Length > 0) return routed;
        return skills.Get(agentId).Connector ?? connectors.DefaultConnector;
    }

    /// <summary>Null se all'agente non è assegnato un modello: il chiamante usa la versione deterministica.</summary>
    public AgentBinding? Resolve(string agentId)
    {
        var connector = ConnectorFor(agentId);
        if (connector is null || string.Equals(connector, "none", StringComparison.OrdinalIgnoreCase)) return null;
        if (!connectors.Names.Contains(connector, StringComparer.OrdinalIgnoreCase))
            throw new LlmException($"L'agente '{agentId}' usa il connettore '{connector}', che non è configurato (Llm:Connectors).");
        var model = connectors.Get(connector);
        return model is null ? null : new AgentBinding(agentId, connector, model, skills.Get(agentId), skills.Protocol(SkillIds.ToolEmulationProtocol));
    }

    public bool IsEnabled(string agentId) => Resolve(agentId) is not null;

    /// <summary>Controllo all'avvio: ogni skill esiste e ogni instradamento punta a un connettore noto.</summary>
    public IReadOnlyList<string> Validate(IEnumerable<string> agentIds)
    {
        var problems = new List<string>();
        foreach (var id in agentIds)
        {
            try { Resolve(id); }
            catch (Exception ex) when (ex is SkillException or LlmException) { problems.Add(ex.Message); }
        }
        foreach (var (agent, _) in _routes.Where(r => !agentIds.Contains(r.Key, StringComparer.OrdinalIgnoreCase)))
            problems.Add($"Llm:Agents:{agent}: agente sconosciuto (ammessi: {string.Join(", ", agentIds)}).");
        return problems;
    }
}
