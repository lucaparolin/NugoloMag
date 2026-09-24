# NugoloMag — Warehouse Agentic Intelligence

Un sistema agentico che osserva di continuo i magazzini, capisce cosa è "normale" nel contesto, rileva deviazioni e rischi,
indaga le cause, ne quantifica l'impatto, raccomanda azioni e verifica se hanno funzionato:

> Osserva → Rileva → Indaga → Spiega → Quantifica → Raccomanda → Verifica

Implementa l'MVP del *Warehouse Agentic Intelligence Blueprint* (Orchestratore, agente Inventario, agente Produttività,
agente Indagine, Briefing direzionale), con in più gli agenti di supporto Impatto, Raccomandazioni e Apprendimento.
Il sistema è **indipendente dal modello linguistico** e funziona anche con modelli locali via **Ollama**.
È scritto in C# / ASP.NET Core MVC, con accesso ai dati in ADO.NET (niente Entity Framework) e senza reflection nel codice applicativo.

## Il sistema agentico

| Blueprint | Implementazione |
|---|---|
| §4 Orchestratore | `WarehouseOrchestrator`. Nel ciclo proattivo gira a ogni esecuzione del monitoraggio. Nelle domande (§24) classifica l'intento, pianifica le sotto-domande, sceglie gli agenti, riconcilia le prove e decide l'escalation |
| §5 Agente Inventario | `InventoryAgent` riusa i detector statistici. Aggiunge esaurimento accelerato con giorni di copertura, eccessi, stock fermo e squilibri tra magazzini |
| §8 Agente Produttività | `ProductivityAgent` + `ProductivityAnalysis`. Confronta a parità di mix (classi d'ordine, zone, pezzi per riga) e attribuisce lo scostamento a zone, articoli e articoli spostati. Non valuta gli operatori (equità) |
| §13 Agente Indagine | `InvestigationAgent`. Usa playbook per pattern (§18 A, B, D + eventi di domanda) e interroga gli specialisti. Pesa le ipotesi con prove a favore, contro e mancanti; ricostruisce sequenza temporale e catena causale; aggiunge il controfattuale |
| §14 Impatto | `ImpactAgent`: stime a intervallo, con e senza intervento |
| §15 Raccomandazioni | `RecommendationAgent`: azioni specifiche con livello di approvazione e reversibilità. Con confidenza bassa propone la prossima indagine (§26) |
| §16 Briefing | `BriefingAgent` con le 6 sezioni standard e il confronto tra magazzini. Una sintesi LLM facoltativa non aggiunge numeri |
| §17 Apprendimento | `LearningAgent`: feedback degli utenti, declassamento degli avvisi giudicati irrilevanti, verifica dell'efficacia delle azioni |
| §19–22 | `SeverityModel` (magnitudo × ampiezza × impatto × urgenza × confidenza), `ConfidenceModel`, `Incident`, contratto `AgentFinding` |
| §23 | `IncidentManager`: comunica transizioni (nuovo, in peggioramento, in miglioramento, risolto) e non ripete lo stesso avviso |

**Prove prima delle conclusioni.** Ogni affermazione è etichettata come *fatto*, *segnale*, *ipotesi* o *causa confermata*.
Tutti i numeri vengono da calcoli deterministici. L'LLM coordina, fa domande agli agenti (usati come strumenti), scrive in
linguaggio naturale e, se serve, esegue query SQL in sola lettura; non produce mai i numeri da solo.

**Esempio reale sui dati demo** (il caso §30 del blueprint):
- **Rilevamento.** Agente Produttività: "prelievo in MI01 −33% rispetto all'atteso a parità di mix; volume normale (+3%)".
- **Indagine.** Chiede allo stesso agente carico, complessità, spostamenti e congestione, e all'agente Inventario la disponibilità. Conclusione: "Lo spostamento di 19 articoli ad alta rotazione ha allungato i percorsi" (confidenza alta). Ipotesi scartate: carico di lavoro e prestazione degli operatori.
- **Impatto.** 3–4 ore di lavoro in più al giorno, 430–650 € in 5 giorni.
- **Raccomandazione.** Ricollocare gli articoli (approvazione del responsabile, azione reversibile) e ribilanciare il lavoro della zona C.
- **Verifica.** Nel ciclo successivo, dopo l'azione, l'incidente risulta risolto e l'azione efficace.

A RM02, invece, la produttività cala del 20% per la maggiore complessità del lavoro (pezzi per riga 4 → 12). Il sistema lo segnala come
informazione ed esclude esplicitamente un problema degli operatori.

## Modelli linguistici: indipendenti dal fornitore, anche locali

| `Llm:Provider` | Cosa usa | Note |
|---|---|---|
| `ollama` | API nativa `/api/chat` | Modelli locali. Serve un modello con tool calling (qwen2.5, llama3.1+, mistral-nemo…); altrimenti `SupportsTools=false` |
| `openai` | `/v1/chat/completions` | OpenAI, Azure OpenAI, LM Studio, vLLM, llama.cpp server, anche Ollama `/v1` |
| `anthropic` | SDK ufficiale | Claude; chiave in `ANTHROPIC_API_KEY` |
| `none` | — | Tutto funziona; le risposte sono costruite dagli agenti deterministici |

```json
"Llm": { "Provider": "ollama", "Model": "qwen2.5:7b", "BaseUrl": "http://localhost:11434",
         "SupportsTools": true, "ContextWindow": 16384, "TimeoutSeconds": 300 }
```

- Con `SupportsTools=false` gli strumenti vengono **emulati via JSON** (`ToolLoop`): il modello risponde con `{"tool": …}` oppure `{"final": …}`. Così funzionano anche modelli locali senza tool calling nativo.
- Le chiavi non stanno nella configurazione: `ApiKeyEnvironmentVariable` indica il nome della variabile d'ambiente da cui leggerla.
- Tutti gli agenti LLM (sintesi del report, revisore dello schema, assistente dati, orchestratore, briefing) dipendono solo da `IChatModel`.

## Pagine dell'applicazione

| Pagina | Cosa fa |
|---|---|
| **Centro operativo** | Incidenti aperti per severità, con stato e escalation. Il dettaglio mostra il caso completo e il modulo di feedback |
| **Briefing** | Briefing direzionale dell'ultimo ciclo e traccia degli agenti; avvio manuale di un ciclo |
| **Indagini** | Domande operative trasformate in indagini multi-agente, con piano, tracciato, ipotesi, impatto e azioni |
| **Assistente dati** | Conversazione libera che scrive ed esegue query SQL in sola lettura |
| **Query salvate** | Analisi rieseguibili |
| **Configurazione** | Analisi del database del gestionale (agente di discovery) e attivazione del monitoraggio |

## Da Anomalo a NugoloMag

| Anomalo Analyst | NugoloMag Analyst |
|---|---|
| Modelli statistici (non LLM) scansionano le tabelle | 6 detector statistici in `Application/Detection`; nessun LLM nel calcolo |
| Nuovi valori, trend invertiti, drift | Nuovi articoli, articoli fermi, rotture di stock, inversioni di trend, cambio di mix (PSI) |
| Punteggio di magnitudo per ogni cambiamento | `MagnitudeScore` = sorpresa statistica × impatto sul magazzino (0-100) |
| "Decide cosa conta" | `FindingRanker` (soglie + top N) e `FindingConsolidator` (niente doppioni padre/figlio) |
| Root cause automatica | `ContributionAnalyzer`: scompone il delta per categoria e articolo |
| Report da analista | Sintesi scritta da un LLM a scelta (o da un template offline) + report HTML/Markdown/JSON |
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
 → prova sui dati reali → [LLM]             → "Attiva monitoraggio"   → report + incidenti + briefing
```

**L'agente** (`DatabaseDiscoveryAgent`) lavora in sola lettura e lascia una traccia di ogni passo:

1. **Schema.** Legge tabelle, viste, colonne, chiavi primarie e foreign key dalle viste di sistema `sys.*`.
2. **Classificazione.** Riconosce le colonne dal nome (italiano e inglese: `CodArt`, `QtaCar`, `Causale`, `Giacenza`...) e dal tipo, poi assegna alle tabelle un ruolo con punteggio e motivazioni: movimenti, saldi, anagrafica articoli.
3. **Profilazione.** Per ogni candidata conta righe, date, articoli, magazzini e attività degli ultimi 30 giorni. Scarta le tabelle ferme e i "saldi correnti" che non hanno storico.
4. **Proposta.** Deduce il verso dei movimenti in uno di tre modi: dalla causale (CAR/VEN/INV...), dal segno della quantità, oppure da colonne separate di carico e scarico. Collega l'anagrafica tramite foreign key.
5. **Prova.** Genera la query T-SQL (giacenza come somma progressiva o da saldi storici) e la esegue sugli ultimi 14 giorni. Controlla freschezza, uscite, giacenze negative, categorie e costi mancanti.
6. **Secondo parere (facoltativo).** L'LLM configurato commenta la proposta. Non modifica nulla da solo.

L'esito è **Pronto**, **Da rivedere** o **Bloccato**. Il monitoraggio si può attivare solo se l'analisi non è bloccata.

**Sicurezza**
- Le stringhe di connessione restano in configurazione (`Sources`); nel browser e nel database viaggia solo il nome della sorgente.
- Il mapping modificato dall'utente accetta solo tabelle e colonne presenti nel catalogo analizzato. Gli identificatori sono sempre quotati e le causali passano con escape.
- Si consiglia un login SQL con solo `db_datareader` sul gestionale.

## Assistente dati conversazionale

Pagina **Assistente**: si parla con un agente che conosce il database del gestionale. L'agente:
- **esplora** lo schema quanto serve, partendo dall'analisi del database già fatta dall'agente di discovery (tabelle giuste, causali);
- **chiede chiarimenti** quando la domanda è ambigua in modo che cambierebbe il risultato ("per venduto intendo causale VEN, ultimi 30 giorni: va bene?"), proponendo sempre un'interpretazione predefinita;
- **scrive ed esegue da solo** le query T-SQL; se una query fallisce ne legge l'errore e la corregge;
- **salva** le query utili nella pagina **Query salvate**, da dove si rieseguono sui dati aggiornati (si possono anche scrivere a mano).

Ogni passo compare nella conversazione: lo scopo, il testo SQL e il risultato.

Gli strumenti dell'agente sono `list_tables`, `describe_table`, `sample_rows`, `run_query`, `save_query` e `list_saved_queries`. Sono definiti in `DataAgentToolbox`, indipendente dal fornitore dell'LLM; il ciclo di chiamate all'LLM, per qualunque fornitore, è in `LlmDataAgent` + `ToolLoop`.

**Sola lettura, con tre barriere indipendenti**
1. `ReadOnlySqlGuard` accetta una sola istruzione `SELECT`/`WITH`. Prima di controllare neutralizza commenti, stringhe e identificatori quotati, poi rifiuta INSERT, UPDATE, DELETE, EXEC, INTO, SET, DECLARE, OPENROWSET, `xp_`/`sp_` e simili.
2. L'esecutore lavora dentro una transazione **sempre annullata**, con timeout di 60 secondi e un limite di righe (200 per l'agente, 1.000 in pagina). Un test d'integrazione verifica che perfino un `DELETE` che aggira il filtro non lasci traccia.
3. Resta consigliato un login SQL con solo `db_datareader`.

Richiede un modello linguistico configurato nella sezione `Llm` (Ollama, compatibile OpenAI o Anthropic). Senza modello la pagina spiega come configurarlo, e il resto dell'app funziona comunque.

## Architettura

Si parte dal modello di dominio, secondo i principi SOLID. L'accesso ai dati è in ADO.NET puro: niente Entity Framework, niente Dapper.

```
Domain          StockDay, Finding, AnalysisWindow, ...        (analisi)
                DatabaseCatalog, TableCandidate, SourceMapping, DiscoveryReport   (discovery)
                MonitorDefinition, MonitorRun                                     (monitoraggio)
Domain          Agentic: Incident, AgentFinding, EvidenceItem, Hypothesis, Recommendation, ImpactEstimate,
                         ExecutiveBriefing, InvestigationCase, SeverityModel, ConfidenceModel; WarehouseTask
Application     Porte: IInventoryRepository, ITaskRepository, ISourceRegistry/ISourceDatabase, IDiscoveryStore, IMonitorStore,
                       IChangeDetector, IRootCauseAnalyzer, IInsightNarrator, ISchemaAdvisor, IChatModel, IToolbox,
                       IIncidentStore, IBriefingStore, IInvestigationStore
                Llm: IChatModel, ToolLoop (tool calling nativo o emulato), CompositeToolbox
                Agentic: WarehouseOrchestrator, InventoryAgent, ProductivityAgent, InvestigationAgent, ImpactAgent,
                         RecommendationAgent, BriefingAgent, LearningAgent, IncidentManager, IntentClassifier
                Detector, AnalystService, DatabaseDiscoveryAgent (+ classificatori, proposer, validator),
                MonitoringService, ConversationService, DataAgentToolbox, ReadOnlySqlGuard
Infrastructure  SqlServerSourceDatabase (catalogo, profilazione, InventoryQueryBuilder T-SQL),
                SqlDiscoveryStore / SqlMonitorStore + StoreSchemaInstaller (schema nugolo),
                Llm: AnthropicChatModel, OpenAiCompatibleChatModel, OllamaChatModel, LlmInsightNarrator, LlmSchemaAdvisor;
                store di conversazioni, query salvate, incidenti, briefing e indagini,
                report HTML/MD/JSON, CSV, dati demo
Web (MVC)       Controller (Operations, Briefing, Investigations, Home, Discovery, Monitors, Runs, Assistant, Queries), viste Razor tipizzate, MonitoringWorker
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
  "Agentic": { "LaborCostPerHour": 28, "MaterialAmount": 5000, "ImpactHorizonDays": 5 },
  "Llm": { "Provider": "ollama", "Model": "qwen2.5:7b", "BaseUrl": "http://localhost:11434" }
}
```

- `NugoloStore` è il database dell'app. Schema e tabelle `nugolo.*` si creano da soli all'avvio.
- `Sources` elenca uno o più gestionali, letti in sola lettura.
- `Agentic` contiene i parametri economici degli agenti (costo orario, soglia di materialità, orizzonte d'impatto); `Llm` sceglie il modello linguistico (vedi sopra).

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

# Domanda in linguaggio naturale (richiede un LLM: --llm ollama --model qwen2.5:7b, oppure ANTHROPIC_API_KEY)
dotnet run --project src/NugoloMag.Analyst.Cli -- ask "Perché MI01 ha avuto un picco di uscite?" --csv export.csv
```

- La sintesi la scrive l'LLM scelto con `--llm ollama|openai|anthropic --model <id> [--llm-url <url>]`. Con `ANTHROPIC_API_KEY` impostata il predefinito è Claude. Senza LLM, o con `--no-llm`, la scrive un template deterministico.
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
