using System.Globalization;
using System.Text.RegularExpressions;

namespace NugoloMag.Analyst.Application.Llm;

/// <summary>Identificativi delle skill: coincidono con le cartelle in <c>agents/</c>.</summary>
public static class SkillIds
{
    public const string Orchestrator = "orchestrator";
    public const string DataAssistant = "data-assistant";
    public const string ReportNarrator = "report-narrator";
    public const string SchemaAdvisor = "schema-advisor";
    public const string Briefing = "briefing";
    /// <summary>Prove standard dei connettori (diagnostica), non instradabile.</summary>
    public const string ConnectorCheck = "connector-check";

    public const string ToolEmulationProtocol = "tool-emulation";

    public static readonly IReadOnlyList<string> All = [Orchestrator, DataAssistant, ReportNarrator, SchemaAdvisor, Briefing];
}

/// <summary>
/// Le istruzioni di un agente, lette da <c>agents/&lt;id&gt;/SKILL.md</c>: il codice decide cosa fa l'agente,
/// la skill decide come parla al modello. Le impostazioni vengono dal front matter.
/// </summary>
public sealed record Skill(
    string Id,
    string Name,
    string Description,
    string Instructions,
    IReadOnlyDictionary<string, string> Prompts,
    IReadOnlyDictionary<string, string> Settings)
{
    public string? Connector => Setting("connector");
    public int MaxTokens(int fallback) => Int("max_tokens") ?? fallback;
    public int MaxRounds(int fallback) => Int("max_rounds") ?? fallback;

    public string? Setting(string key) => Settings.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public string Prompt(string name) => Prompts.TryGetValue(name, out var p)
        ? p
        : throw new SkillException($"La skill '{Id}' non ha il prompt '{name}' (atteso agents/{Id}/prompts/{name}.md).");

    private int? Int(string key) => Setting(key) is { } v && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}

/// <summary>Libreria delle skill. L'implementazione su file ricarica i testi quando cambiano.</summary>
public interface ISkillLibrary
{
    Skill Get(string id);
    IReadOnlyList<Skill> All();
    /// <summary>Protocollo dei connettori (<c>agents/_protocols/&lt;name&gt;.md</c>).</summary>
    string Protocol(string name);
}

public sealed class SkillException(string message) : Exception(message);

/// <summary>Sostituisce i segnaposto <c>{{nome}}</c>. Quelli senza valore diventano vuoti.</summary>
public static partial class SkillTemplate
{
    [GeneratedRegex(@"\{\{\s*([a-z0-9_]+)\s*\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    public static string Render(string template, IReadOnlyDictionary<string, string?>? values = null) =>
        Placeholder().Replace(template, m => values is not null && values.TryGetValue(m.Groups[1].Value, out var v) ? v ?? "" : "").Trim();

    public static IReadOnlyList<string> Placeholders(string template) =>
        Placeholder().Matches(template).Select(m => m.Groups[1].Value).Distinct().ToList();
}
