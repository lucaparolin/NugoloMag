# Skill degli agenti

Qui stanno le istruzioni degli agenti che usano un modello linguistico, fuori dal codice.
Si modificano senza ricompilare e vengono ricaricate automaticamente quando il file cambia.

```
agents/
  <agente>/SKILL.md         istruzioni di sistema dell'agente (obbligatorio)
  <agente>/prompts/*.md     prompt aggiuntivi usati dall'agente (facoltativi)
  _shared/*.md              testi condivisi, inclusi con {{> _shared/nome.md}}
  _protocols/*.md           protocolli dei connettori (es. emulazione degli strumenti)
```

## Formato di `SKILL.md`

```markdown
---
name: Orchestratore
description: Coordina gli agenti specialisti e risponde alle domande operative.
connector: locale        # facoltativo: connettore preferito (sezione Llm:Connectors)
max_tokens: 4000         # facoltativo
max_rounds: 8            # facoltativo: passi massimi del ciclo con strumenti
---
Testo delle istruzioni. Può includere testi condivisi {{> _shared/principi.md}}
e valori a runtime come {{today}}, {{source}}, {{database}}.
```

Il connettore di un agente si sceglie in quest'ordine:
1. `Llm:Agents:<agente>` in appsettings;
2. `connector` nel front matter della skill;
3. `Llm:Default`.

## Agenti e segnaposto disponibili

| Cartella | Chi la usa | Segnaposto |
|---|---|---|
| `orchestrator` | Orchestratore (domande → indagini multi-agente) | — |
| `data-assistant` | Assistente dati (query SQL in sola lettura) | `{{source}}`, `{{database}}`, `{{today}}`, `{{discovery_brief}}` |
| `report-narrator` | Sintesi del report statistico; `prompts/narrative.md`, `prompts/question.md` | `{{question}}` in `question.md` |
| `schema-advisor` | Secondo parere sull'analisi del database | — |
| `briefing` | Sintesi del briefing direzionale | — |
| `connector-check` | Collaudo dei connettori (pagina Connettori LLM, `nugolomag llm-test`); prompt `reply`, `json`, `tools`, `tool-giacenza` | — |
| `_protocols/tool-emulation.md` | Connettori senza tool calling nativo | `{{tools}}` |

Note pratiche:
- i segnaposto senza valore diventano vuoti; spazi e a-capo iniziali e finali dei file vengono tolti
  (un a-capo finale nella descrizione di uno strumento bastava a far fallire il tool calling di qwen2.5);
- le inclusioni non possono uscire da questa cartella;
- la pagina **Connettori LLM** mostra le skill caricate e quale connettore usa ciascun agente.

Gli agenti deterministici (Inventario, Produttività, Indagine, Impatto, Raccomandazioni) non hanno prompt:
la loro logica è codice testato, perché i numeri non devono dipendere da un modello linguistico.
