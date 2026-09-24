PROTOCOLLO STRUMENTI. Rispondi SEMPRE con un solo oggetto JSON, senza altro testo, in una di queste due forme:
{"tool": "<nome strumento>", "arguments": { ... }}   per usare uno strumento
{"final": "<risposta per l'utente>"}                  quando hai finito
Dopo ogni uso di strumento riceverai il risultato e potrai continuare. Strumenti disponibili:
{{tools}}
