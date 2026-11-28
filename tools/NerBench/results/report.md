# Banco locale: catalogo di mascheramento con e senza NER (#281)

Data: 2026-11-24 16:09. Corpus sintetico: 88 documenti (72 con dati, 16 senza), 157 entità annotate, 7230 caratteri.
Modello: rizzo-pii-0.3B ONNX fp32 (rizzoaiacademy/rizzo-pii-0.3B, MIT), OnnxRuntime CPU, caricamento 1,4 s, memoria dopo il caricamento 672 MB.
Archivio di prova: 79 nomi noti (una entità annotata su due per categoria, categoria con PrivacyEntityCategory.OfDisplayName).
Hardware: Intel64 Family 6 Model 165 Stepping 5, GenuineIntel, 16 thread logici, 31 GB RAM, Microsoft Windows 10.0.26300, .NET 10.0.15.

## Richiamo e falsi positivi

«mascherata» = ogni carattere dell'entità è coperto da un intervallo mascherato (qualsiasi categoria); «categoria» = coperto da intervalli della categoria giusta.

| Configurazione | PERSONA mascherata | PERSONA categoria | AZIENDA mascherata | AZIENDA categoria | Falsi positivi (spans) | per documento |
|---|---|---|---|---|---|---|
| catalogo | 1/82 (1%) | 1/82 (1%) | 25/75 (33%) | 25/75 (33%) | 0 | 0,00 |
| indice+catalogo | 41/82 (50%) | 41/82 (50%) | 49/75 (65%) | 48/75 (64%) | 1 | 0,01 |
| NER | 82/82 (100%) | 82/82 (100%) | 36/75 (48%) | 30/75 (40%) | 5 | 0,06 |
| catalogo+NER | 82/82 (100%) | 82/82 (100%) | 46/75 (61%) | 40/75 (53%) | 5 | 0,06 |
| indice+catalogo+NER | 82/82 (100%) | 82/82 (100%) | 60/75 (80%) | 56/75 (75%) | 6 | 0,07 |

## Soglie

| Soglia (fissata prima della misura) | Valore | Misurato (indice+catalogo+NER) | Esito |
|---|---|---|---|
| Richiamo PERSONA (categoria corretta) | >= 90% | 100% | RISPETTATA |
| Richiamo AZIENDA (categoria corretta) | >= 80% | 75% | NON rispettata |
| Falsi positivi PERSONA/AZIENDA per documento | <= 0,20 | 0,07 | RISPETTATA |
| Tempo NER per 1000 caratteri (documenti brevi) | <= 500 ms | 527 ms (testo lungo: 455 ms) | NON rispettata |
| Memoria di picco del processo | <= 1500 MB | 1002 MB | RISPETTATA |

Fetta tenuta fuori (indice+catalogo+NER, documenti q29-q56): PERSONA 29/29 (100%); AZIENDA 17/30 (57%); falsi positivi 4 su 28 documenti (0,14 per documento).
Nomi di persona etichettati AZIENDA dall'indice (corpus intero): 0.

Catalogo da solo: 1,9 ms per documento.

## Mancati (almeno una configurazione)

- p01 PERSONA «Laura Verdi»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p02 PERSONA «Giulia Ferrari»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p02 PERSONA «Paolo Neri»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p03 AZIENDA «Aurora Costruzioni»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p03 PERSONA «Luca Moretti»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p04 PERSONA «Francesca Romano»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p05 PERSONA «Andrea Colombo»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p05 AZIENDA «Meccanica Brianza»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: «Bri» CITY
- p06 PERSONA «Chiara De Luca»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p06 AZIENDA «Studio Associato Conti & Galli»: catalogo=ok indice+catalogo=ok NER=no catalogo+NER=ok indice+catalogo+NER=ok; il NER vede: «Conti & Galli» ORG
- p07 PERSONA «sara conti»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p07 AZIENDA «rossi impianti»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p08 AZIENDA «Cantina Valdelsa»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: niente
- p08 PERSONA «Roberto Esposito»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p09 PERSONA «Elena Gallo»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p09 PERSONA «Marco Fontana»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p09 AZIENDA «AreaJob»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: niente
- p10 AZIENDA «Pellegrini Forniture S.n.c.»: catalogo=ok indice+catalogo=ok NER=no catalogo+NER=ok indice+catalogo+NER=ok; il NER vede: «Pellegrini Forniture S.n.c» ORG
- p10 PERSONA «Davide Pellegrini»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p11 PERSONA «John Smith»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p11 AZIENDA «Northwind Traders»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: niente
- p11 PERSONA «Emily Clark»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p12 PERSONA «Silvia Marchetti»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p12 PERSONA «Antonio Greco»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p12 PERSONA «Federica Bruno»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p12 AZIENDA «Officine Meridionali»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p13 PERSONA «Giuseppe Rizzi»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p13 AZIENDA «Banca Popolare del Lago»: catalogo=ok indice+catalogo=ok NER=no catalogo+NER=ok indice+catalogo+NER=ok; il NER vede: «lare» ORG, «Lago» ORG
- p14 PERSONA «Matteo Villa»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p14 PERSONA «Alessia Sala»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p14 AZIENDA «Microsoft»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: niente
- p15 AZIENDA «Fondazione Il Girasole»: catalogo=ok indice+catalogo=ok NER=no catalogo+NER=ok indice+catalogo+NER=ok; il NER vede: «Il» CITY, «Gira» ORG
- p15 PERSONA «Anna Maria Longo»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- p16 PERSONA «Stefano Barbieri»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q01 AZIENDA «Verdemare Logistica S.r.l.»: catalogo=ok indice+catalogo=ok NER=no catalogo+NER=ok indice+catalogo+NER=ok; il NER vede: «Verdemare Logistica S.r.l» ORG
- q01 PERSONA «Nicola Basile»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q02 PERSONA «Irene Caputo»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q02 AZIENDA «Lumen Digitale»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q03 AZIENDA «Cartiera del Serio SpA»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q03 PERSONA «Valerio Monti»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q04 PERSONA «Sofia Gentile»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q04 AZIENDA «Orizzonte Energia»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q05 PERSONA «Carla Fabbri»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q06 AZIENDA «Termoidraulica Vega»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: niente
- q06 PERSONA «Enrico Sartori»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q07 PERSONA «Olivia Brown»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q07 AZIENDA «Harbor Lane Consulting»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: «Harbor Lane» STREET
- q08 AZIENDA «Associazione Amici del Parco»: catalogo=ok indice+catalogo=ok NER=no catalogo+NER=ok indice+catalogo+NER=ok; il NER vede: niente
- q08 PERSONA «Tommaso Leone»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q08 PERSONA «Marta Piras»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q09 AZIENDA «Studio Legale Ferri & Partners»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q09 PERSONA «Michele Ferri»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q10 PERSONA «Beatrice Conte»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q10 AZIENDA «Nuova Serigrafia Adriatica S.r.l.»: catalogo=ok indice+catalogo=ok NER=no catalogo+NER=ok indice+catalogo+NER=ok; il NER vede: «Adriatica S.r.l.» ORG
- q11 PERSONA «Simone Orlando»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q11 AZIENDA «TalentBridge»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: niente
- q12 AZIENDA «Ascensori Fenice»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q12 PERSONA «Daniela Costa»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q13 PERSONA «Luigi Mancini»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q13 AZIENDA «Mobilificio Casalini S.n.c.»: catalogo=ok indice+catalogo=ok NER=no catalogo+NER=ok indice+catalogo+NER=ok; il NER vede: «Casalini S.n.c..» ORG
- q14 AZIENDA «Ortofrutta Delle Colline»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q14 PERSONA «Patrizia Vitale»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q15 PERSONA «Giorgio Amato»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q16 PERSONA «Daniel Wright»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q16 AZIENDA «Bluebird Analytics Ltd»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: niente
- q17 AZIENDA «Tipografia Moderna»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: niente
- q17 PERSONA «Roberta Neri»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q18 PERSONA «Alberto Ricci»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q18 PERSONA «Lucia Marini»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q19 PERSONA «Fabio Cattaneo»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q20 PERSONA «Elisa Parisi»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q20 AZIENDA «Ferramenta Centrale»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: niente
- q21 PERSONA «Claudio Benedetti»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q21 AZIENDA «Vinicola Montecchio»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: «Vin» CITY, «icola Montecchio» FULLNAME
- q22 PERSONA «Monica Lombardi»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q22 AZIENDA «Banca Territoriale del Piave»: catalogo=ok indice+catalogo=ok NER=no catalogo+NER=ok indice+catalogo+NER=ok; il NER vede: niente
- q23 AZIENDA «Rapido Express S.r.l.»: catalogo=ok indice+catalogo=ok NER=no catalogo+NER=ok indice+catalogo+NER=ok; il NER vede: «Rap» ORG, «Express S.r.l.» ORG
- q23 PERSONA «Gianni Pellegrino»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q24 PERSONA «Valentina Sala»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q25 PERSONA «Pietro Fiore»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q26 AZIENDA «Galleria Palazzo Vecchio»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: «Galleria Palazzo Vecchio» STREET
- q26 PERSONA «Cristina Bellini»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q27 PERSONA «Sarah Johnson»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q27 AZIENDA «Maple Street Printing»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: «Maple Street» STREET
- q28 PERSONA «Massimo Rinaldi»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q28 AZIENDA «Servizi Casa»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q29 PERSONA «Alessandro Gatti»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q29 AZIENDA «Veltro»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: «Veltro» FULLNAME
- q30 AZIENDA «Officina Meccanica Tre Ponti»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: «Officina Mec» ORG, «Tre» ORG
- q30 PERSONA «Lorenzo Silvestri»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q31 AZIENDA «Blu Cielo Viaggi»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: «Blu» CITY, «Cielo» ORG
- q31 PERSONA «Anna De Santis»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q32 PERSONA «Michael Turner»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q32 AZIENDA «Greenfield Logistics»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: «Greenfield» CITY
- q33 PERSONA «Giada Ferraro»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q34 PERSONA «Paolo Fiore»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q34 AZIENDA «Panificio Sant'Anna»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: «Panificio Sant'Anna» CITY
- q35 PERSONA «Gian Luca Bruni»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q35 AZIENDA «Rossi & Figli»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q36 AZIENDA «Tecnomar»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q36 PERSONA «Silvia Pagano»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q37 PERSONA «Riccardo Marchi»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q37 AZIENDA «Accademia delle Arti»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: niente
- q38 PERSONA «Laura Evans»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q38 AZIENDA «Redwood Software»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q39 AZIENDA «Monte Rosa Editori»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: «Monte Rosa» CITY
- q39 PERSONA «Federico Longhi»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q40 PERSONA «Rita Gallo»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q41 PERSONA «Stefano Leoni»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q41 PERSONA «Marina Zanetti»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q41 AZIENDA «Società Agricola La Quercia»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: «La Quercia» CITY
- q42 AZIENDA «Novaterra»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: «Novaterra» FULLNAME
- q42 PERSONA «Cesare Amoroso»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q43 PERSONA «Ilaria Santoro»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q43 AZIENDA «Studio 21»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: «21» BUILDINGNUM
- q44 AZIENDA «Libreria Il Faro»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: «eria Il Faro» ORG
- q44 PERSONA «Giulio Rizzo»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q45 AZIENDA «Pro Loco di Valmare»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: «Valmare» CITY
- q45 PERSONA «Chiara Sorrentino»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q46 PERSONA «Thomas Baker»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q46 AZIENDA «Ironwood Partners LLC»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q47 AZIENDA «Autofficina Delta»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: «Delta» ORG
- q47 PERSONA «Mauro Bellucci»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q48 AZIENDA «Cantieri Navali Liguri»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: «eri» ORG, «ali» ORG, «uri» ORG
- q48 PERSONA «Antonella Guidi»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q49 AZIENDA «Solaria»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q49 PERSONA «Franco Villani»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q50 PERSONA «Noemi Carbone»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q50 AZIENDA «Hotel Belvedere»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: «Hotel» CITY, «Belvedere» STREET
- q51 AZIENDA «Casa Editrice Orsa Maggiore»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: «Orsa Maggiore» CITY
- q51 AZIENDA «Circolo Culturale Aurelio»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: «Aurelio» CITY
- q51 PERSONA «Davide Moretti»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q52 PERSONA «Emma Collins»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q52 AZIENDA «Silverline Travel»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: niente
- q53 AZIENDA «Pellicceria Montanari»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: «Pellicceria Montanari» FULLNAME
- q53 PERSONA «Luciana Ferri»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q54 PERSONA «Bruno Esposito»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q55 AZIENDA «Fornaci Riunite»: catalogo=no indice+catalogo=no NER=no catalogo+NER=no indice+catalogo+NER=no; il NER vede: niente
- q55 PERSONA «Elisabetta Greco»: catalogo=no indice+catalogo=ok NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q56 PERSONA «Walter Conti»: catalogo=no indice+catalogo=no NER=ok catalogo+NER=ok indice+catalogo+NER=ok
- q56 AZIENDA «Datacore Systems»: catalogo=no indice+catalogo=ok NER=no catalogo+NER=no indice+catalogo+NER=ok; il NER vede: niente

## Falsi positivi

```json
{
  "name": "catalogo",
  "falsePositives": []
}
```
```json
{
  "name": "indice+catalogo",
  "falsePositives": [
    "q48:PERSONA:«Cantieri Navali Liguri»"
  ]
}
```
```json
{
  "name": "NER",
  "falsePositives": [
    "q21:PERSONA:«icola Montecchio»",
    "q29:PERSONA:«Veltro»",
    "q42:PERSONA:«Novaterra»",
    "q53:PERSONA:«Pellicceria Montanari»",
    "n01:PERSONA:«Aurora»"
  ]
}
```
```json
{
  "name": "catalogo+NER",
  "falsePositives": [
    "q21:PERSONA:«icola Montecchio»",
    "q29:PERSONA:«Veltro»",
    "q42:PERSONA:«Novaterra»",
    "q53:PERSONA:«Pellicceria Montanari»",
    "n01:PERSONA:«Aurora»"
  ]
}
```
```json
{
  "name": "indice+catalogo+NER",
  "falsePositives": [
    "q21:PERSONA:«icola Montecchio»",
    "q29:PERSONA:«Veltro»",
    "q42:PERSONA:«Novaterra»",
    "q48:PERSONA:«Cantieri Navali Liguri»",
    "q53:PERSONA:«Pellicceria Montanari»",
    "n01:PERSONA:«Aurora»"
  ]
}
```

## Limiti

- Le righe «catalogo» girano senza l'indice delle entità locali; le righe «indice+...» usano un archivio di prova con circa metà delle entità (regola fissata nel README) e senza intestazioni reali: la copertura reale dell'archivio può essere diversa.
- Corpus scritto a mano (88 documenti, 75 AZIENDA): indica una direzione, non una qualifica. La fetta q29-q56 è tenuta fuori dalla regola ma scritta dallo stesso autore del lessico: è ottimistica.
- Modello fp32 ufficiale, non l'int8 da 308 MB del 15/11: tempi e memoria non sono confrontabili con quelle misure.
- Testi oltre 8000 token non sono finestrati dal banco.
- Nessuna distribuzione del modello né riuso dei dataset originali.
