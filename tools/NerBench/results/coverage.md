# #308: AZIENDA recall against archive coverage (88 documents, 75 AZIENDA)

Archive = known corpus names at the stated coverage + 600 invented distractor names. Product row = indice+catalogo+NER.

| Coverage | Archive names | AZIENDA correct category | PERSONA correct category | False positives per doc | AZIENDA >= 80% |
|---|---|---|---|---|---|
| 0% | 600 | 40/75 (53%) | 82/82 (100%) | 0,06 | no |
| 30% | 648 | 47/75 (63%) | 82/82 (100%) | 0,07 | no |
| 50% | 679 | 55/75 (73%) | 82/82 (100%) | 0,07 | no |
| 70% | 710 | 62/75 (83%) | 82/82 (100%) | 0,09 | yes |
| 90% | 740 | 69/75 (92%) | 82/82 (100%) | 0,09 | yes |
| 100% | 757 | 72/75 (96%) | 82/82 (100%) | 0,09 | yes |
