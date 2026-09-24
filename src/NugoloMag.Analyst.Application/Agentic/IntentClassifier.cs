using System.Text.RegularExpressions;
using NugoloMag.Analyst.Domain.Agentic;

namespace NugoloMag.Analyst.Application.Agentic;

public enum QuestionKind { Why, Risk, Priority, Compare, Opportunity, Status }

public sealed record QuestionIntent(OperationalDomain Domain, QuestionKind Kind, IReadOnlyList<string> Warehouses, IReadOnlyList<string> Skus);

/// <summary>
/// Classificazione dell'intento (blueprint §4): dominio operativo, tipo di domanda, magazzini e articoli citati.
/// Regole trasparenti e deterministiche: funzionano con qualunque modello, anche senza LLM.
/// </summary>
public static partial class IntentClassifier
{
    public static QuestionIntent Classify(string question, AgentWorkspace ws)
    {
        var q = question.ToLowerInvariant();
        bool Any(params string[] words) => words.Any(q.Contains);

        var productivity = Any("produttiv", "picking", "prelie", "righe/ora", "righe all'ora", "lent", "tempi", "efficien", "operator", "evasion");
        var inventory = Any("stock", "giacenz", "scort", "rottur", "esaur", "inventar", "eccess", "fermo", "fermi", "copertura", "riordin", "disponib");
        var quality = Any("rettific", "ammanc", "discrepan", "errori", "squadr", "differenz inventar");

        var domain = (productivity, inventory || quality) switch
        {
            (true, false) => OperationalDomain.Productivity,
            (false, true) => quality && !inventory ? OperationalDomain.Quality : OperationalDomain.Inventory,
            _ => OperationalDomain.CrossFunctional
        };

        var kind =
            Any("perché", "perche", "come mai", "causa", "spiega", "motivo") ? QuestionKind.Why :
            Any("confront", "differenza tra", "rispetto a", " vs ", "meglio di", "peggio di") ? QuestionKind.Compare :
            Any("rischi", "probabil", "potrebbe", "rischio") ? QuestionKind.Risk :
            Any("opportunit", "miglior", "inefficien", "risparm", "evitabil") ? QuestionKind.Opportunity :
            Any("preoccup", "priorit", "oggi", "attenzione", "alert", "avvis", "urgent", "azione") ? QuestionKind.Priority :
            QuestionKind.Status;

        var warehouses = ws.Warehouses.Where(w => Regex.IsMatch(question, $@"\b{Regex.Escape(w)}\b", RegexOptions.IgnoreCase)).ToList();
        var skus = TokenRegex().Matches(question).Select(m => m.Value.ToUpperInvariant())
            .Where(t => ws.Inventory.Warehouses.Any(w => ws.Inventory.SkusIn(w).Any(s => s.Value == t)))
            .Distinct().ToList();
        return new QuestionIntent(domain, kind, warehouses, skus);
    }

    /// <summary>Scomposizione della domanda in sotto-domande (blueprint §4, pianificazione dell'indagine).</summary>
    public static IReadOnlyList<string> Plan(QuestionIntent intent) => (intent.Domain, intent.Kind) switch
    {
        (OperationalDomain.Productivity, _) =>
        [
            "La produttività è sotto l'atteso a parità di mix degli ordini?",
            "Il carico di lavoro (volume) è cambiato?",
            "La complessità degli ordini è cambiata?",
            "Ci sono articoli spostati di zona che allungano i percorsi?",
            "Ci sono segnali di congestione in qualche zona?",
            "La disponibilità di stock ha causato attese o prelievi a vuoto?"
        ],
        (OperationalDomain.Inventory, QuestionKind.Risk) or (OperationalDomain.Inventory, QuestionKind.Priority) =>
        [
            "Quali articoli si stanno esaurendo più velocemente del normale?",
            "Quali articoli sono già in rottura o fermi?",
            "I ricevimenti sono regolari per gli articoli a rischio?",
            "Quanto valgono le unità a rischio?"
        ],
        (OperationalDomain.Inventory, _) or (OperationalDomain.Quality, _) =>
        [
            "Cosa è cambiato nelle giacenze e nei consumi?",
            "Ci sono rettifiche o discrepanze anomale?",
            "Ci sono eccessi o stock fermo che immobilizzano capitale?",
            "Quale spiegazione è più supportata dalle prove?"
        ],
        (_, QuestionKind.Compare) =>
        [
            "Quali problemi sono presenti in un magazzino e non nell'altro?",
            "La produttività a parità di mix differisce?",
            "Le condizioni di stock differiscono?"
        ],
        _ =>
        [
            "Quali problemi aperti hanno la severità più alta?",
            "Cosa è peggiorato o è nuovo rispetto al ciclo precedente?",
            "Quali azioni sono raccomandate e chi deve approvarle?",
            "Cosa va solo tenuto sotto osservazione?"
        ]
    };

    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9\-_.]{2,}")]
    private static partial Regex TokenRegex();
}
