# NerBench

Local bench (task #281): the masking catalog (`PseudonymizationCatalog`) with and without the `rizzo-pii-0.3B` NER model.
Not part of the product or of `OnlyWorkspace.sln`; nothing here is shipped.

## Files

- `HfTokenizer.cs`: port of the model's `tokenizer.json` (Gemma-style BPE, ▁ Metaspace, byte fallback, added tokens).
- `NerModel.cs`: OnnxRuntime inference, BIO tags merged into character spans.
- `Corpus.cs`: synthetic corpus (invented people and companies, 16 documents with data, 8 without).
- `Program.cs`: `dump`, `verify` and `bench` commands. `reference.py`: Hugging Face tokenizers + onnxruntime reference.
- `results/report.md`: last measurement. `results/corpus.json` and `results/reference.json` feed `verify`.

## Model (not committed)

Official fp32 export, MIT license, downloaded from `https://huggingface.co/rizzoaiacademy/rizzo-pii-0.3B`
(`resolve/main/`: `tokenizer.json`, `tokenizer_config.json`, `special_tokens_map.json`, `config.json`, `onnx/model.onnx`,
`onnx/model.onnx_data`, about 1.3 GB) into `models/rizzo-pii-0.3B/`. That folder and `.venv/` are ignored by git.

## Run

```powershell
dotnet run -c Release -- dump                 # writes results/corpus.json
python -m venv .venv; .\.venv\Scripts\python.exe -m pip install tokenizers onnxruntime numpy
.\.venv\Scripts\python.exe reference.py       # writes results/reference.json
dotnet run -c Release -- verify               # ids, offsets and labels must equal the reference
dotnet run -c Release -- verify-synthetic     # ids and offsets on results/synthetic.json (newlines, CRLF, emoji, special tokens); reference.py writes synthetic-reference.json
dotnet run -c Release -- bench                # writes results/report.md
dotnet run -c Release -- coverage             # writes results/coverage.md (#308)
dotnet run -c Release -- real-coverage [--profile P] [--sample 300] [--timeout 30] [--directory D]   # real archive, counts only (#316)
```

### `coverage` (#308)

Measures AZIENDA recall in the correct category at archive coverage 0, 30, 50, 70, 90 and 100%, with the product configuration
(`indice+catalogo+NER`, or `indice+catalogo` when the model is absent).

- Selection rule: an annotated entity is a known archive name when `position * 37 % 100 < coverage`, with the position counted per category in corpus order.
- The archive also holds 600 invented distractor names that never occur in the corpus.
- Output: `results/coverage.md`, one row per coverage with archive size, AZIENDA and PERSONA recall, false positives per document and whether AZIENDA >= 80%.
- Last result (88 documents, 75 AZIENDA): 53, 63, 73, 83, 92 and 96% AZIENDA at 0, 30, 50, 70, 90 and 100% coverage; the 80% threshold is first met at 70%.
- Limit: corpus and archive names are synthetic, so this is not the coverage of a real archive (see #310).
- Target (#314, owner decision 25/11): the 80% AZIENDA threshold applies to the full product (archive index + catalog + NER), not to the archive index alone. On the real archive (#310) the index already holds 3% of the company names that the NER finds, so the index cannot carry the target; the lever is the NER and the rules, and the real-archive figure comes from `real-coverage` (#316), counts only.

### `real-coverage` (#316)

Measures how many company names that the NER finds in real mails are already in the local archive index. It reads the live tray (`ArchiveClient` over the pipe), so the tray must run and the model must be in `models/`.

- Index: all entities of `GetPrivacyEntities` (as the task pane reads them), AZIENDA by `PrivacyEntityCategory`.
- Sample: the newest `--sample` mails (default 300) of all authorized stores; the original comes from the encrypted archive, else from Outlook through `RequestOriginal` (`--timeout` seconds each).
- Proxy: NER `ORG` mentions, distinct after trimming and case folding (names of 3+ characters).
- Report (stdout only, nothing is written): index size, mails readable and unreadable with the reason class (`original-source-error`, `original-empty`, `original-timeout`, `engine-timeout`, `engine-error`), distinct names, exact match and containment match (either name contains the other).
- Privacy rule: counts only. The command never prints, logs or stores a name, subject, address or text, and reports failures as a class, not as the exception message. Do not paste the output anywhere that the counts could identify a person.
- With several archive profiles, pass `--profile`; without it the command stops and reports only the number of profiles.

## Thresholds (fixed before the first measurement, applied to catalog + NER)

PERSONA recall >= 90%, AZIENDA recall >= 80%, false positives <= 0.20 PERSONA/AZIENDA spans per document,
<= 500 ms per 1000 characters (short documents, warm), peak working set <= 1500 MB.

### #292 re-measurement (fixed before the run, 2026-11-22)

The numbers above do not change. The product configuration is now archive index + catalog + NER (`indice+catalogo+NER`);
`catalogo+NER` stays as the comparison row. Corpus: first run 60 synthetic documents (44 with data, 16 without, 45 AZIENDA); extended to 88 documents (72 with data, 75 AZIENDA)
with the held-out slice q29-q56.
Archive fixture rule: in corpus order, every annotated entity at an even position within its category is a known archive display name
(about 50% coverage); its category is `PrivacyEntityCategory.OfDisplayName`, exactly as `ArchiveDb.GetPrivacyEntities` does.
Second step, fixed before the first run of the extended rule (2026-11-22): the first run above gave AZIENDA 69% (correct category) and
0.23 false positives per document because the index labelled 12 known companies without a legal form as PERSONA. The rule
`OfDisplayName` is extended in this order, first match wins, otherwise PERSONA: (1) legal-form or institution word (unchanged);
(2) the name contains `&` or a digit; (3) the name is a single word of letters (hyphen and apostrophe allowed, no dots or `@`);
(4) any word is in a fixed lexicon of generic Italian and English business nouns (list in `PrivacyEntityCategory.cs`).
The lexicon is not derived from the corpus. Limit: the author wrote both the lexicon and the corpus, so the held-out slice
(documents q29-q56, written before the rule was extended and never used to tune it) is optimistic, not independent.
Acceptance, on the held-out slice and on the full corpus: AZIENDA >= 80% in the correct category, false positives <= 0.20,
PERSONA >= 90%; person names labelled AZIENDA by the index are counted and reported.

Without the model on disk the bench still runs the index and catalog rows and reports NER as not measured; the thresholds are
then judged only on `indice+catalogo+NER` once the model is present.
