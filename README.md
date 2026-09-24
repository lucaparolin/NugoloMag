# NugoloMag Analyst

Un analista automatico per i magazzini, ispirato ad [Anomalo Analyst](https://www.anomalo.com/anomalo-analyst/).
Ogni giorno confronta lo stato dei magazzini con le settimane precedenti. Trova cosa è cambiato, decide
cosa conta, spiega da dove viene la variazione e scrive un report da analista, non un semplice allarme.

## Da Anomalo a NugoloMag

| Anomalo Analyst | NugoloMag Analyst |
|---|---|
| Modelli statistici (non LLM) scansionano le tabelle | 6 detector statistici in `Application/Detection`; nessun LLM nel calcolo |
| Nuovi valori, trend invertiti, drift | Nuovi articoli, articoli fermi, rotture di stock, inversioni di trend, cambio di mix (PSI) |
| Punteggio di magnitudo per ogni cambiamento | `MagnitudeScore` = sorpresa statistica × impatto sul magazzino (0-100) |
| "Decide cosa conta" | `FindingRanker` (soglie + top N) e `FindingConsolidator` (niente doppioni padre/figlio) |
| Root cause automatica | `ContributionAnalyzer`: scompone il delta per categoria e articolo |
| Report da analista | Sintesi di Claude (o template offline) + report HTML/Markdown/JSON |
| Domande di follow-up in linguaggio naturale | `nugolomag ask "..."` risponde partendo dai finding |
| Controlli di qualità del dato | Freschezza/completezza del caricamento, giacenze negative, squadrature giacenza/movimenti |

## Cosa rileva

| Detector | Esempio di finding |
|---|---|
| `DataFreshnessDetector` | "Dati di RM02 fermi al 21/09: 2 giorni mancanti" |
| `StockIntegrityDetector` | "8 squadrature di giacenza in NA03 (valore 43.173 €)" |
| `PointAnomalyDetector` | "Uscite MI01: +83% rispetto al normale del mercoledì", con root cause: Elettronica 98% |
| `TrendReversalDetector` | "Giacenza MI01: trend invertito, da +1,2% a −3,5% al giorno" |
| `ItemLifecycleDetector` | "Rottura di stock RM02/RM-015" / "Articolo fermo con 492 pezzi a stock" / "6 nuovi articoli" |
| `MixDriftDetector` | "Mix uscite NA03 cambiato (PSI 0.51): Bevande dal 22% al 55%" |

Le scelte statistiche sono fatte per dati di magazzino:
- **Stagionalità settimanale.** Il lunedì si confronta con i lunedì, quindi la domenica a zero non genera allarmi.
- **Statistiche robuste.** Mediana e MAD fanno sì che un picco passato non alzi la soglia per sempre.
- **Trend su media mobile a 7 giorni.** Il ciclo settimanale non viene scambiato per un trend.
- **Dati incompleti.** Se un magazzino non ha caricato i dati, quel magazzino viene escluso dagli altri controlli, così non escono falsi "articoli fermi".

## Architettura

Si parte dal modello di dominio, le dipendenze vanno solo verso l'interno e l'accesso ai dati è in ADO.NET puro (niente EF).

```
Domain          StockDay, WarehouseCode, Sku, Subject, AnalysisWindow, TimeSeries, Finding, Contribution, MagnitudeScore
Application     Porte (IInventoryRepository, IChangeDetector, IRootCauseAnalyzer, IInsightNarrator, IReportWriter)
                Detector, ContributionAnalyzer, FindingRanker, FindingConsolidator, AnalystService (orchestratore)
Infrastructure  DbInventoryRepository (ADO.NET su DbProviderFactory), CsvInventoryRepository,
                ClaudeInsightNarrator, report HTML/Markdown/JSON, generatore di dati demo
Cli             Composition root
```

Per aggiungere un controllo basta scrivere un nuovo `IChangeDetector` e registrarlo in `Program.cs`, senza toccare
il resto (principio Open/Closed). Il repository riceve una `DbProviderFactory`: in produzione SQL Server, nei test SQLite.

## Uso

Richiede .NET 8.

```bash
# Demo con 3 magazzini sintetici e anomalie iniettate
dotnet run --project src/NugoloMag.Analyst.Cli -- demo --asof 2026-09-23 --out report

# Da SQL Server (tabella o vista dbo.StockDaily, vedi sql/schema.sqlserver.sql)
dotnet run --project src/NugoloMag.Analyst.Cli -- analyze \
  --connection "Server=.;Database=Gestionale;Integrated Security=true;TrustServerCertificate=true" \
  --asof 2026-09-23 --out report

# Schema diverso: query propria con parametri @from/@to e le 9 colonne attese
dotnet run --project src/NugoloMag.Analyst.Cli -- analyze --connection "..." --query-file sql/query-example.sql

# Da CSV esportato: date,warehouse,sku,category,on_hand,inbound,outbound,adjustment,unit_cost
dotnet run --project src/NugoloMag.Analyst.Cli -- analyze --csv export.csv --asof 2026-09-23

# Domanda in linguaggio naturale (richiede ANTHROPIC_API_KEY)
dotnet run --project src/NugoloMag.Analyst.Cli -- ask "Perché MI01 ha avuto un picco di uscite?" --csv export.csv
```

- Se `ANTHROPIC_API_KEY` è impostata, la sintesi la scrive Claude (`claude-opus-5`, cambiabile con `--model`). Senza chiave, o con `--no-llm`, la scrive un template deterministico.
- Se ci sono finding ad alta priorità il programma esce con codice `2`: basta un job schedulato (SQL Agent, Task Scheduler, cron) per far partire un alert.
- Le soglie sono in `DetectionSettings`.

Un report di esempio è in [`docs/esempio/`](docs/esempio/).

## Test

```bash
dotnet test
```

I test coprono ogni detector (anche l'assenza di falsi positivi su dati stabili e stagionali), la root cause,
un end-to-end sui dati demo che deve ritrovare tutte le anomalie iniettate e il repository ADO.NET su SQLite.
