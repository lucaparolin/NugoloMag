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

## Web app (ASP.NET Core MVC): prima l'analisi del DB, poi il monitoraggio

```
 1. Analisi del database (agente)            2. Revisione              3. Monitoraggio
 ───────────────────────────────            ─────────────             ───────────────
 schema (sys.*) → classificazione tabelle   mapping modificabile      worker in background
 → profilazione dati → causali              (tabelle, colonne,        → ogni giorno all'orario scelto
 → proposta di mapping → query T-SQL        causali) + nuova prova    → analisi di ieri vs baseline
 → prova sui dati reali → [Claude]          → "Attiva monitoraggio"   → report salvato + dashboard
```

**L'agente** (`DatabaseDiscoveryAgent`) lavora in sola lettura e lascia una traccia di ogni passo:

1. **Schema.** Legge tabelle, viste, colonne, chiavi primarie e foreign key dalle viste di sistema `sys.*`.
2. **Classificazione.** Riconosce le colonne dal nome (italiano e inglese: `CodArt`, `QtaCar`, `Causale`, `Giacenza`...) e dal tipo, poi assegna alle tabelle un ruolo con punteggio e motivazioni: movimenti, saldi, anagrafica articoli.
3. **Profilazione.** Per ogni candidata conta righe, date, articoli, magazzini e attività degli ultimi 30 giorni. Scarta le tabelle ferme e i "saldi correnti" che non hanno storico.
4. **Proposta.** Deduce il verso dei movimenti in uno di tre modi: dalla causale (CAR/VEN/INV...), dal segno della quantità, oppure da colonne separate di carico e scarico. Collega l'anagrafica tramite foreign key.
5. **Prova.** Genera la query T-SQL (giacenza come somma progressiva o da saldi storici) e la esegue sugli ultimi 14 giorni. Controlla freschezza, uscite, giacenze negative, categorie e costi mancanti.
6. **Secondo parere (facoltativo).** Claude commenta la proposta. Non modifica nulla da solo.

L'esito è **Pronto**, **Da rivedere** o **Bloccato**. Il monitoraggio si può attivare solo se l'analisi non è bloccata.

**Sicurezza**
- Le stringhe di connessione restano in configurazione (`Sources`); nel browser e nel database viaggia solo il nome della sorgente.
- Il mapping modificato dall'utente accetta solo tabelle e colonne presenti nel catalogo analizzato. Gli identificatori sono sempre quotati e le causali passano con escape.
- Si consiglia un login SQL con solo `db_datareader` sul gestionale.

## Architettura

Si parte dal modello di dominio, secondo i principi SOLID. L'accesso ai dati è in ADO.NET puro: niente Entity Framework, niente Dapper.

```
Domain          StockDay, Finding, AnalysisWindow, ...        (analisi)
                DatabaseCatalog, TableCandidate, SourceMapping, DiscoveryReport   (discovery)
                MonitorDefinition, MonitorRun                                     (monitoraggio)
Application     Porte: IInventoryRepository, ISourceRegistry/ISourceDatabase, IDiscoveryStore, IMonitorStore,
                       IChangeDetector, IRootCauseAnalyzer, IInsightNarrator, ISchemaAdvisor
                Detector, AnalystService, DatabaseDiscoveryAgent (+ classificatori, proposer, validator),
                MonitoringService
Infrastructure  SqlServerSourceDatabase (catalogo, profilazione, InventoryQueryBuilder T-SQL),
                SqlDiscoveryStore / SqlMonitorStore + StoreSchemaInstaller (schema nugolo),
                Claude (narratore e revisore), report HTML/MD/JSON, CSV, dati demo
Web (MVC)       Controller (Home, Discovery, Monitors, Runs), viste Razor tipizzate, MonitoringWorker
Cli             demo / analyze / ask / discover / seed-demo
```

**Senza reflection nel nostro codice**
- Composition root esplicita (`CompositionRoot`): ogni servizio e ogni controller è creato con `new` in una factory. `AddControllersAsServices` trova i controller già registrati e non li attiva via reflection.
- I form si leggono da `IFormCollection` con conversioni esplicite (`FormReader`, `MappingForm`), senza model binding su proprietà.
- Le viste sono tipizzate: niente `ViewBag`/`dynamic`, route values con `RouteValueDictionary`.
- Il JSON usa un `JsonSerializerContext` generato a compile-time. Gli enum persistiti hanno codici espliciti (`DomainCodes`, `StoreCodes`) e non passano da `Enum.ToString`/`Parse`.
- Limite dichiarato: il framework ASP.NET Core MVC (routing delle action, Razor, logging) e l'SDK Anthropic usano reflection al loro interno. Non è evitabile restando su MVC.

Per aggiungere un controllo basta scrivere un nuovo `IChangeDetector` e registrarlo in `CompositionRoot`.

### Configurazione (`src/NugoloMag.Web/appsettings.json`)

```json
{
  "ConnectionStrings": { "NugoloStore": "Server=...;Database=NugoloMag;..." },
  "Sources": { "Gestionale": "Server=...;Database=Gestionale;...;ApplicationIntent=ReadOnly" },
  "Monitoring": { "TimeZone": "Europe/Rome", "PollSeconds": 60 },
  "Claude": { "Model": "claude-opus-5" }
}
```

- `NugoloStore` è il database dell'app. Schema e tabelle `nugolo.*` si creano da soli all'avvio.
- `Sources` elenca uno o più gestionali, letti in sola lettura.
- Con la variabile d'ambiente `ANTHROPIC_API_KEY` si attivano la sintesi e la revisione di Claude.

### Avvio

```bash
dotnet run --project src/NugoloMag.Web
```

Per provarlo senza un gestionale reale:

```bash
docker run -d -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Nugolo#2026!' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
dotnet run --project src/NugoloMag.Analyst.Cli -- seed-demo --master "Server=localhost;User ID=sa;Password=Nugolo#2026!;TrustServerCertificate=true"
# poi in appsettings: Sources:Gestionale → Database=GestionaleDemo
```

`GestionaleDemo` imita un gestionale italiano. Contiene `MovMag` (movimenti con causali CAR/VEN/INV/INI), `Articoli`, `Magazzini`, `SaldiMagazzino` (solo saldo corrente) e alcune tabelle che non c'entrano (`OrdiniRighe`, `ListiniPrezzi`, `Clienti`...) per mettere alla prova l'agente.

## Riga di comando

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
# integrazione su SQL Server reale: analisi del DB → attivazione → esecuzione → report salvato
NUGOLO_TEST_SQLSERVER="Server=localhost;User ID=sa;Password=...;TrustServerCertificate=true" dotnet test
```

I test coprono ogni detector (anche l'assenza di falsi positivi su dati stabili e stagionali), la root cause,
un end-to-end sui dati demo che deve ritrovare tutte le anomalie iniettate e il repository ADO.NET su SQLite.
Coprono anche classificatori, proposta di mapping, escape della query, pianificazione e il flusso completo su SQL Server.
