using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Domain.Discovery;

namespace NugoloMag.Analyst.Application.Discovery;

/// <summary>Prova la query generata sugli ultimi giorni reali e controlla che i dati abbiano senso.</summary>
public sealed class SourceValidator(int days = 14, int previewRows = 20)
{
    public async Task<SourceValidation> ValidateAsync(ISourceDatabase source, string query, DateOnly asOf, CancellationToken ct)
    {
        var from = asOf.AddDays(-(days - 1));
        IReadOnlyList<StockDay> rows;
        try
        {
            rows = await source.Inventory(query).LoadAsync(from, asOf, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SourceValidation(from, asOf, 0, 0, 0,
                [new ValidationCheck("Esecuzione query", StepStatus.Failed, ex.Message)], []);
        }

        var checks = new List<ValidationCheck> { new("Esecuzione query", StepStatus.Ok, $"{rows.Count:N0} righe in {days} giorni") };
        var warehouses = rows.Select(r => r.Warehouse).Distinct().Count();
        var items = rows.Select(r => r.Sku).Distinct().Count();

        if (rows.Count == 0)
        {
            checks.Add(new("Dati presenti", StepStatus.Failed, $"Nessuna riga tra {from:dd/MM/yyyy} e {asOf:dd/MM/yyyy}: la tabella non è aggiornata o il mapping è sbagliato."));
            return new SourceValidation(from, asOf, 0, 0, 0, checks, []);
        }

        checks.Add(new("Copertura", StepStatus.Ok, $"{warehouses} magazzini, {items:N0} articoli"));

        var lastMovement = rows.Where(r => r.Inbound != 0 || r.Outbound != 0 || r.Adjustment != 0).Select(r => (DateOnly?)r.Date).Max();
        checks.Add(lastMovement is { } lm && lm >= asOf.AddDays(-3)
            ? new("Freschezza", StepStatus.Ok, $"ultimo movimento il {lm:dd/MM/yyyy}")
            : new("Freschezza", StepStatus.Warning, lastMovement is { } l ? $"ultimo movimento il {l:dd/MM/yyyy}: dati poco aggiornati" : "nessun movimento nel periodo"));

        var movedOut = rows.Sum(r => r.Outbound);
        checks.Add(movedOut > 0
            ? new("Uscite", StepStatus.Ok, $"{movedOut:N0} unità uscite nel periodo")
            : new("Uscite", StepStatus.Warning, "nessuna uscita: verificare il verso dei movimenti (causali o segno)"));

        checks.Add(Share("Giacenze negative", rows.Count(r => r.OnHand < 0), rows.Count, 0.02,
            "giacenza ricostruita negativa: storico movimenti incompleto o saldi iniziali mancanti"));
        checks.Add(Share("Categoria assente", rows.Count(r => r.Category == "N/D"), rows.Count, 0.2,
            "senza categoria la root cause per famiglia non funziona"));
        checks.Add(Share("Costo assente", rows.Count(r => r.UnitCost == 0), rows.Count, 0.5,
            "il valore di magazzino non sarà affidabile"));

        return new SourceValidation(from, asOf, rows.Count, warehouses, items, checks,
            rows.OrderByDescending(r => r.Date).ThenByDescending(r => r.Outbound).Take(previewRows).ToList());
    }

    private static ValidationCheck Share(string name, int count, int total, double threshold, string warning)
    {
        var share = count / (double)total;
        return share <= threshold
            ? new ValidationCheck(name, StepStatus.Ok, $"{share:P1} delle righe")
            : new ValidationCheck(name, StepStatus.Warning, $"{share:P1} delle righe: {warning}");
    }
}
