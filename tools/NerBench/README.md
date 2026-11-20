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
dotnet run -c Release -- bench                # writes results/report.md
```

## Thresholds (fixed before the first measurement, applied to catalog + NER)

PERSONA recall >= 90%, AZIENDA recall >= 80%, false positives <= 0.20 PERSONA/AZIENDA spans per document,
<= 500 ms per 1000 characters (short documents, warm), peak working set <= 1500 MB.
