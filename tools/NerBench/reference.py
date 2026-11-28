"""Reference for the bench: Hugging Face tokenizers + onnxruntime on the corpus dumped by `dotnet run -- dump`.
Usage (venv in tools/NerBench/.venv): python reference.py [model-dir]"""
import json, sys, pathlib
import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

here = pathlib.Path(__file__).parent
model = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else here / "models" / "rizzo-pii-0.3B"
tok = Tokenizer.from_file(str(model / "tokenizer.json"))
id2label = json.load(open(model / "config.json", encoding="utf8"))["id2label"]
session = ort.InferenceSession(str(model / "onnx" / "model.onnx"))
out = {}
for doc in json.load(open(here / "results" / "corpus.json", encoding="utf8")):
    enc = tok.encode(doc["Text"])
    ids = np.array([enc.ids], dtype=np.int64)
    feed = {i.name: (np.ones_like(ids) if "mask" in i.name else ids) for i in session.get_inputs()}
    logits = session.run(None, feed)[0]
    labels = [id2label[str(i)] for i in logits[0].argmax(-1)]
    out[doc["Id"]] = {"ids": enc.ids, "offsets": enc.offsets, "labels": labels}
json.dump(out, open(here / "results" / "reference.json", "w", encoding="utf8"))
print("reference.json:", len(out), "documents")

# Synthetic texts (newlines, CRLF, emoji, special tokens): ids and offsets only, checked by `verify-synthetic`.
syn = {}
for doc in json.load(open(here / "results" / "synthetic.json", encoding="utf8")):
    enc = tok.encode(doc["Text"])
    syn[doc["Id"]] = {"ids": enc.ids, "offsets": enc.offsets}
json.dump(syn, open(here / "results" / "synthetic-reference.json", "w", encoding="utf8"))
print("synthetic-reference.json:", len(syn), "texts")
