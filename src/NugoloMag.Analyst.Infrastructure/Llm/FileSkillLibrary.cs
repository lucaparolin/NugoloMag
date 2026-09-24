using System.Text;
using System.Text.RegularExpressions;
using NugoloMag.Analyst.Application.Llm;

namespace NugoloMag.Analyst.Infrastructure.Llm;

/// <summary>
/// Skill lette dalla cartella agents/: <c>&lt;id&gt;/SKILL.md</c> (front matter + istruzioni), <c>&lt;id&gt;/prompts/*.md</c>,
/// inclusioni <c>{{&gt; percorso.md}}</c> relative alla radice. Si ricaricano quando un file della cartella cambia.
/// </summary>
public sealed partial class FileSkillLibrary : ISkillLibrary
{
    private const int MaxIncludeDepth = 5;
    private readonly string _root;
    private readonly object _lock = new();
    private Dictionary<string, Skill> _skills = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _protocols = new(StringComparer.OrdinalIgnoreCase);
    private (DateTime Stamp, int Count) _loaded = (DateTime.MinValue, -1);

    public FileSkillLibrary(string root)
    {
        _root = Path.GetFullPath(root);
        if (!Directory.Exists(_root))
            throw new SkillException($"Cartella delle skill non trovata: {_root} (impostare Skills:Path).");
    }

    public string Root => _root;

    public Skill Get(string id)
    {
        Refresh();
        return _skills.TryGetValue(id, out var skill) ? skill
            : throw new SkillException($"Skill '{id}' non trovata: atteso {Path.Combine(_root, id, "SKILL.md")}.");
    }

    public IReadOnlyList<Skill> All()
    {
        Refresh();
        return _skills.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
    }

    public string Protocol(string name)
    {
        Refresh();
        return _protocols.TryGetValue(name, out var text) ? text
            : throw new SkillException($"Protocollo '{name}' non trovato: atteso {Path.Combine(_root, "_protocols", name + ".md")}.");
    }

    /// <summary>Errori bloccanti per l'avvio: skill mancanti o file non leggibili.</summary>
    public IReadOnlyList<string> Validate(IEnumerable<string> requiredIds)
    {
        var problems = new List<string>();
        try { Refresh(); }
        catch (SkillException ex) { return [ex.Message]; }
        problems.AddRange(requiredIds.Where(id => !_skills.ContainsKey(id)).Select(id => $"Skill mancante: {Path.Combine(_root, id, "SKILL.md")}."));
        if (!_protocols.ContainsKey(SkillIds.ToolEmulationProtocol))
            problems.Add($"Protocollo mancante: {Path.Combine(_root, "_protocols", SkillIds.ToolEmulationProtocol + ".md")}.");
        problems.AddRange(_skills.Values.Where(s => s.Instructions.Length == 0).Select(s => $"Skill '{s.Id}': istruzioni vuote."));
        return problems;
    }

    // ------------------------------------------------------------------ caricamento

    private void Refresh()
    {
        var files = Directory.EnumerateFiles(_root, "*.md", SearchOption.AllDirectories).ToList();
        var stamp = (files.Count == 0 ? DateTime.MinValue : files.Max(File.GetLastWriteTimeUtc), files.Count);
        if (stamp == _loaded) return;

        lock (_lock)
        {
            if (stamp == _loaded) return;
            var skills = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(_root).Where(d => !Path.GetFileName(d).StartsWith('_')))
            {
                var file = Path.Combine(dir, "SKILL.md");
                if (File.Exists(file)) skills[Path.GetFileName(dir)] = Load(Path.GetFileName(dir), dir, file);
            }

            var protocols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var protocolDir = Path.Combine(_root, "_protocols");
            if (Directory.Exists(protocolDir))
                foreach (var file in Directory.EnumerateFiles(protocolDir, "*.md"))
                    protocols[Path.GetFileNameWithoutExtension(file)] = Resolve(Read(file), file, 0).Trim();

            _skills = skills;
            _protocols = protocols;
            _loaded = stamp;
        }
    }

    private Skill Load(string id, string dir, string file)
    {
        var (settings, body) = ParseFrontMatter(Read(file), file);
        var prompts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var promptDir = Path.Combine(dir, "prompts");
        if (Directory.Exists(promptDir))
            foreach (var p in Directory.EnumerateFiles(promptDir, "*.md"))
                prompts[Path.GetFileNameWithoutExtension(p)] = Resolve(Read(p), p, 0).Trim();

        return new Skill(
            id,
            settings.GetValueOrDefault("name") ?? id,
            settings.GetValueOrDefault("description") ?? "",
            Resolve(body, file, 0),
            prompts,
            settings);
    }

    /// <summary>Front matter minimale "chiave: valore" tra due righe "---"; i commenti "# ..." a fine riga sono ignorati.</summary>
    public static (Dictionary<string, string> Settings, string Body) ParseFrontMatter(string text, string file = "")
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return (settings, text.Trim());

        var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (end < 0) throw new SkillException($"{file}: front matter non chiuso (manca la riga '---').");

        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            var hash = line.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) line = line[..hash];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new SkillException($"{file}, riga {i + 1}: atteso 'chiave: valore'.");
            settings[line[..colon].Trim()] = line[(colon + 1)..].Trim().Trim('"');
        }
        return (settings, string.Join('\n', lines[(end + 1)..]).Trim());
    }

    [GeneratedRegex(@"\{\{>\s*([^}\s]+)\s*\}\}")]
    private static partial Regex Include();

    private string Resolve(string text, string file, int depth)
    {
        if (depth > MaxIncludeDepth) throw new SkillException($"{file}: troppe inclusioni annidate.");
        return Include().Replace(text, m =>
        {
            var path = Path.GetFullPath(Path.Combine(_root, m.Groups[1].Value));
            if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new SkillException($"{file}: inclusione fuori dalla cartella delle skill ({m.Groups[1].Value}).");
            if (!File.Exists(path)) throw new SkillException($"{file}: file incluso non trovato ({m.Groups[1].Value}).");
            return Resolve(Read(path), path, depth + 1).Trim();
        });
    }

    private static string Read(string path)
    {
        // Lettura tollerante ai salvataggi concorrenti di un editor.
        for (var attempt = 0; ; attempt++)
        {
            try { return File.ReadAllText(path, Encoding.UTF8); }
            catch (IOException) when (attempt < 3) { Thread.Sleep(50); }
        }
    }
}
