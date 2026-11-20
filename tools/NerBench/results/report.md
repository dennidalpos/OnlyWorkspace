# Banco locale: catalogo di mascheramento con e senza NER (#281)

Data: 2026-11-20 19:48. Corpus sintetico: 24 documenti (16 con dati, 8 senza), 39 entità annotate, 2406 caratteri.
Modello: rizzo-pii-0.3B ONNX fp32 (rizzoaiacademy/rizzo-pii-0.3B, MIT), OnnxRuntime CPU, caricamento 1,3 s, memoria dopo il caricamento 672 MB.
Hardware: Intel64 Family 6 Model 165 Stepping 5, GenuineIntel, 16 thread logici, 31 GB RAM, Microsoft Windows 10.0.26300, .NET 10.0.15.

## Richiamo e falsi positivi

«mascherata» = ogni carattere dell'entità è coperto da un intervallo mascherato (qualsiasi categoria); «categoria» = coperto da intervalli della categoria giusta.

| Configurazione | PERSONA mascherata | PERSONA categoria | AZIENDA mascherata | AZIENDA categoria | Falsi positivi (spans) | per documento |
|---|---|---|---|---|---|---|
| catalogo | 1/23 (4%) | 1/23 (4%) | 0/16 (0%) | 0/16 (0%) | 0 | 0,00 |
| NER | 23/23 (100%) | 23/23 (100%) | 7/16 (44%) | 7/16 (44%) | 1 | 0,04 |
| catalogo+NER | 23/23 (100%) | 23/23 (100%) | 7/16 (44%) | 7/16 (44%) | 1 | 0,04 |

## Soglie

| Soglia (fissata prima della misura) | Valore | Misurato (catalogo+NER) | Esito |
|---|---|---|---|
| Richiamo PERSONA (categoria corretta) | >= 90% | 100% | RISPETTATA |
| Richiamo AZIENDA (categoria corretta) | >= 80% | 44% | NON rispettata |
| Falsi positivi PERSONA/AZIENDA per documento | <= 0,20 | 0,04 | RISPETTATA |
| Tempo NER per 1000 caratteri (documenti brevi) | <= 500 ms | 349 ms (testo lungo: 300 ms) | RISPETTATA |
| Memoria di picco del processo | <= 1500 MB | 805 MB | RISPETTATA |

Catalogo da solo: 2,2 ms per documento.

## Mancati (almeno una configurazione)

- p01 PERSONA «Laura Verdi»: catalogo=no NER=ok catalogo+NER=ok
- p01 AZIENDA «Edilsud S.r.l.»: catalogo=no NER=ok catalogo+NER=ok
- p02 PERSONA «Giulia Ferrari»: catalogo=no NER=ok catalogo+NER=ok
- p02 AZIENDA «Tecnoservice SpA»: catalogo=no NER=ok catalogo+NER=ok
- p02 PERSONA «Paolo Neri»: catalogo=no NER=ok catalogo+NER=ok
- p03 AZIENDA «Aurora Costruzioni»: catalogo=no NER=ok catalogo+NER=ok
- p03 PERSONA «Luca Moretti»: catalogo=no NER=ok catalogo+NER=ok
- p04 PERSONA «Francesca Romano»: catalogo=no NER=ok catalogo+NER=ok
- p04 AZIENDA «Gruppo Alfa Logistica»: catalogo=no NER=ok catalogo+NER=ok
- p05 PERSONA «Andrea Colombo»: catalogo=no NER=ok catalogo+NER=ok
- p05 AZIENDA «Meccanica Brianza»: catalogo=no NER=no catalogo+NER=no; il NER vede: «Bri» CITY
- p06 PERSONA «Chiara De Luca»: catalogo=no NER=ok catalogo+NER=ok
- p06 AZIENDA «Studio Associato Conti & Galli»: catalogo=no NER=no catalogo+NER=no; il NER vede: «Conti & Galli» ORG
- p07 PERSONA «sara conti»: catalogo=no NER=ok catalogo+NER=ok
- p07 AZIENDA «rossi impianti»: catalogo=no NER=ok catalogo+NER=ok
- p08 AZIENDA «Cantina Valdelsa»: catalogo=no NER=no catalogo+NER=no; il NER vede: niente
- p08 PERSONA «Roberto Esposito»: catalogo=no NER=ok catalogo+NER=ok
- p09 PERSONA «Elena Gallo»: catalogo=no NER=ok catalogo+NER=ok
- p09 PERSONA «Marco Fontana»: catalogo=no NER=ok catalogo+NER=ok
- p09 AZIENDA «AreaJob»: catalogo=no NER=no catalogo+NER=no; il NER vede: niente
- p10 AZIENDA «Pellegrini Forniture S.n.c.»: catalogo=no NER=no catalogo+NER=no; il NER vede: «Pellegrini Forniture S.n.c» ORG
- p10 PERSONA «Davide Pellegrini»: catalogo=no NER=ok catalogo+NER=ok
- p11 PERSONA «John Smith»: catalogo=no NER=ok catalogo+NER=ok
- p11 AZIENDA «Northwind Traders»: catalogo=no NER=no catalogo+NER=no; il NER vede: niente
- p11 PERSONA «Emily Clark»: catalogo=no NER=ok catalogo+NER=ok
- p12 PERSONA «Silvia Marchetti»: catalogo=no NER=ok catalogo+NER=ok
- p12 PERSONA «Antonio Greco»: catalogo=no NER=ok catalogo+NER=ok
- p12 PERSONA «Federica Bruno»: catalogo=no NER=ok catalogo+NER=ok
- p12 AZIENDA «Officine Meridionali»: catalogo=no NER=ok catalogo+NER=ok
- p13 PERSONA «Giuseppe Rizzi»: catalogo=no NER=ok catalogo+NER=ok
- p13 AZIENDA «Banca Popolare del Lago»: catalogo=no NER=no catalogo+NER=no; il NER vede: «lare» ORG, «Lago» ORG
- p14 PERSONA «Matteo Villa»: catalogo=no NER=ok catalogo+NER=ok
- p14 PERSONA «Alessia Sala»: catalogo=no NER=ok catalogo+NER=ok
- p14 AZIENDA «Microsoft»: catalogo=no NER=no catalogo+NER=no; il NER vede: niente
- p15 AZIENDA «Fondazione Il Girasole»: catalogo=no NER=no catalogo+NER=no; il NER vede: «Il» CITY, «Gira» ORG
- p15 PERSONA «Anna Maria Longo»: catalogo=no NER=ok catalogo+NER=ok
- p16 AZIENDA «Formazione Nord S.r.l.»: catalogo=no NER=ok catalogo+NER=ok
- p16 PERSONA «Stefano Barbieri»: catalogo=no NER=ok catalogo+NER=ok

## Falsi positivi

```json
{
  "name": "catalogo",
  "falsePositives": []
}
```
```json
{
  "name": "NER",
  "falsePositives": [
    "n01:PERSONA:«Aurora»"
  ]
}
```
```json
{
  "name": "catalogo+NER",
  "falsePositives": [
    "n01:PERSONA:«Aurora»"
  ]
}
```

## Limiti

- Il catalogo gira senza l'indice delle entità locali (nomi già noti dall'archivio) e senza intestazioni reali: la sua base è sottostimata rispetto all'uso nel prodotto.
- Corpus piccolo e scritto a mano: indica una direzione, non una qualifica.
- Modello fp32 ufficiale, non l'int8 da 308 MB del 15/11: tempi e memoria non sono confrontabili con quelle misure.
- Testi oltre 8000 token non sono finestrati dal banco.
- Nessuna distribuzione del modello né riuso dei dataset originali.
