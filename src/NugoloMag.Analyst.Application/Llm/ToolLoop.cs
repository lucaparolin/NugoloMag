using System.Text;
using System.Text.Json;

namespace NugoloMag.Analyst.Application.Llm;

/// <summary>Un passo registrato del ciclo: testo intermedio del modello oppure uso di uno strumento.</summary>
public sealed record LoopStep(string? Text, ToolCall? Call, ToolOutcome? Outcome);

public sealed record LoopResult(string FinalText, IReadOnlyList<LoopStep> Steps, bool Completed, IReadOnlyList<ChatMessage> Transcript);

/// <summary>
/// Il ciclo agentico condiviso da tutti gli agenti: il modello ragiona, chiede strumenti, riceve risultati, finché risponde.
/// Funziona con qualsiasi <see cref="IChatModel"/>: se il modello non supporta gli strumenti in modo nativo
/// (molti modelli locali su Ollama), li emula con un protocollo JSON documentato nel prompt.
/// </summary>
public static class ToolLoop
{
    public static async Task<LoopResult> RunAsync(
        IChatModel model,
        string system,
        IReadOnlyList<ChatMessage> history,
        IToolbox tools,
        int maxRounds = 15,
        Func<ToolCall, bool>? stopAfter = null,
        CancellationToken ct = default)
    {
        var native = model.Info.SupportsTools;
        var messages = history.ToList();
        var steps = new List<LoopStep>();
        var effectiveSystem = native || tools.Specs.Count == 0 ? system : system + "\n\n" + EmulationProtocol(tools.Specs);

        for (var round = 0; round < maxRounds; round++)
        {
            var response = await model.CompleteAsync(new ChatRequest(
                effectiveSystem, messages.ToList(), native ? tools.Specs : [], JsonOnly: !native && tools.Specs.Count > 0), ct);

            if (response.Stop == ChatStop.Refusal)
                return new LoopResult("Il modello ha rifiutato la richiesta.", steps, false, messages);

            var (text, calls) = native ? (response.Text, response.ToolCalls) : ParseEmulated(response.Text, round);

            if (calls.Count == 0)
            {
                var final = text.Trim();
                if (response.Stop == ChatStop.MaxTokens) final += "\n\n(Risposta interrotta per lunghezza.)";
                messages.Add(ChatMessage.Assistant(final));
                return new LoopResult(final.Length == 0 ? "(nessuna risposta)" : final, steps, true, messages);
            }

            if (!string.IsNullOrWhiteSpace(text)) steps.Add(new LoopStep(text.Trim(), null, null));
            messages.Add(native ? response.ToAssistantMessage() : ChatMessage.Assistant(response.Text));

            var stop = false;
            var emulatedResults = new StringBuilder();
            foreach (var call in calls)
            {
                ToolOutcome outcome;
                try
                {
                    using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                    outcome = await tools.ExecuteAsync(call.Name, args.RootElement.Clone(), ct);
                }
                catch (JsonException ex)
                {
                    outcome = new ToolOutcome($"Argomenti non validi (JSON): {ex.Message}", true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Qualunque errore di uno strumento torna al modello come risultato d'errore: può correggersi e continuare.
                    outcome = new ToolOutcome($"Errore dello strumento {call.Name}: {ex.Message}", true);
                }

                steps.Add(new LoopStep(null, call, outcome));
                if (native) messages.Add(ChatMessage.ToolResult(call, outcome.Content, outcome.IsError));
                else emulatedResults.AppendLine($"Risultato di {call.Name}{(outcome.IsError ? " (ERRORE)" : "")}:\n{outcome.Content}\n");
                if (stopAfter?.Invoke(call) == true && !outcome.IsError) stop = true;
            }

            if (!native) messages.Add(ChatMessage.User(emulatedResults.ToString().Trim()));
            if (stop) return new LoopResult("", steps, true, messages);
        }

        return new LoopResult($"Limite di {maxRounds} passi raggiunto senza una risposta completa.", steps, false, messages);
    }

    /// <summary>Istruzioni per i modelli senza tool calling nativo: una risposta = un oggetto JSON.</summary>
    private static string EmulationProtocol(IReadOnlyList<ToolSpec> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PROTOCOLLO STRUMENTI. Rispondi SEMPRE con un solo oggetto JSON, senza altro testo, in una di queste due forme:");
        sb.AppendLine("""{"tool": "<nome strumento>", "arguments": { ... }}   per usare uno strumento""");
        sb.AppendLine("""{"final": "<risposta per l'utente>"}                  quando hai finito""");
        sb.AppendLine("Dopo ogni uso di strumento riceverai il risultato e potrai continuare. Strumenti disponibili:");
        foreach (var t in tools) sb.AppendLine($"- {t.Name}: {t.Description}\n  argomenti (JSON Schema): {t.InputSchemaJson}");
        return sb.ToString();
    }

    /// <summary>Interpreta la risposta emulata. Testo non-JSON viene trattato come risposta finale (modello che ignora il protocollo).</summary>
    public static (string Text, IReadOnlyList<ToolCall> Calls) ParseEmulated(string raw, int round)
    {
        var json = ExtractJsonObject(raw);
        if (json is null) return (raw, []);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (raw, []);
            if (root.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String)
            {
                var args = root.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetRawText() : "{}";
                return ("", [new ToolCall($"emu_{round}", tool.GetString()!, args)]);
            }
            if (root.TryGetProperty("final", out var final))
                return (final.ValueKind == JsonValueKind.String ? final.GetString()! : final.GetRawText(), []);
            return (raw, []);
        }
        catch (JsonException)
        {
            return (raw, []);
        }
    }

    /// <summary>Estrae il primo oggetto JSON bilanciato dal testo (i modelli locali a volte aggiungono ```json ... ```).</summary>
    public static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text[start..(i + 1)];
        }
        return null;
    }
}
