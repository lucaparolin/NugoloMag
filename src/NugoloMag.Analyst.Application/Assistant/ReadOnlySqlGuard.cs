using System.Text;

namespace NugoloMag.Analyst.Application.Assistant;

public sealed record GuardResult(bool IsAllowed, string? Reason)
{
    public static readonly GuardResult Allowed = new(true, null);
    public static GuardResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// Prima barriera: accetta solo una singola istruzione SELECT (o WITH … SELECT) senza comandi che scrivono,
/// eseguono codice o accedono ad altri server. Commenti, stringhe e identificatori quotati vengono neutralizzati
/// prima dell'analisi, così "[Delete]" come nome di colonna non è un falso positivo e "--" non nasconde nulla.
/// Seconda barriera: l'esecutore gira dentro una transazione sempre annullata. Terza: login in sola lettura.
/// </summary>
public static class ReadOnlySqlGuard
{
    public const int MaxLength = 20_000;

    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "UPSERT", "DROP", "ALTER", "CREATE", "TRUNCATE", "RENAME",
        "EXEC", "EXECUTE", "SP_EXECUTESQL", "GRANT", "REVOKE", "DENY", "BACKUP", "RESTORE", "DBCC", "SHUTDOWN",
        "KILL", "OPENROWSET", "OPENQUERY", "OPENDATASOURCE", "OPENXML", "BULK", "INTO", "WAITFOR", "USE", "SET",
        "DECLARE", "BEGIN", "COMMIT", "ROLLBACK", "SAVE", "TRANSACTION", "RECONFIGURE", "CHECKPOINT", "WRITETEXT",
        "UPDATETEXT", "READTEXT", "RAISERROR", "THROW", "PRINT", "GO", "LOAD", "DISABLE", "ENABLE", "RECEIVE", "SEND"
    };

    public static GuardResult Check(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return GuardResult.Deny("Query vuota.");
        if (sql.Length > MaxLength) return GuardResult.Deny($"Query troppo lunga (max {MaxLength} caratteri).");

        string cleaned;
        try
        {
            cleaned = Neutralize(sql).Trim();
        }
        catch (FormatException ex)
        {
            return GuardResult.Deny(ex.Message);
        }

        while (cleaned.EndsWith(';')) cleaned = cleaned[..^1].TrimEnd();
        if (cleaned.Contains(';')) return GuardResult.Deny("È ammessa una sola istruzione.");

        var tokens = Tokens(cleaned).ToList();
        if (tokens.Count == 0) return GuardResult.Deny("Query vuota.");
        if (!tokens[0].Equals("SELECT", StringComparison.OrdinalIgnoreCase) && !tokens[0].Equals("WITH", StringComparison.OrdinalIgnoreCase))
            return GuardResult.Deny("Sono ammesse solo query SELECT (anche con WITH).");

        foreach (var token in tokens)
        {
            if (Forbidden.Contains(token)) return GuardResult.Deny($"Parola chiave non ammessa in sola lettura: {token.ToUpperInvariant()}.");
            if (token.StartsWith("xp_", StringComparison.OrdinalIgnoreCase) || token.StartsWith("sp_", StringComparison.OrdinalIgnoreCase))
                return GuardResult.Deny($"Procedure di sistema non ammesse: {token}.");
        }

        return GuardResult.Allowed;
    }

    /// <summary>Sostituisce commenti, letterali stringa e identificatori quotati con segnaposto neutri.</summary>
    private static string Neutralize(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                sb.Append(' ');
            }
            else if (c == '/' && next == '*')
            {
                var depth = 0;
                do
                {
                    if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; }
                    else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { depth--; i += 2; }
                    else if (i < sql.Length) i++;
                    else throw new FormatException("Commento /* non chiuso.");
                } while (depth > 0);
                sb.Append(' ');
            }
            else if (c is '\'' or '"' or '[')
            {
                var close = c == '[' ? ']' : c;
                i++;
                while (true)
                {
                    if (i >= sql.Length) throw new FormatException("Stringa o identificatore non chiuso.");
                    if (sql[i] == close)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; } // escape raddoppiato
                        i++;
                        break;
                    }
                    i++;
                }
                sb.Append(c == '\'' ? " 'x' " : " q ");
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    private static IEnumerable<string> Tokens(string sql)
    {
        var sb = new StringBuilder();
        foreach (var c in sql)
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$')
            {
                sb.Append(c);
            }
            else if (sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
