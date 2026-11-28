using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.PiiRecognizer;

/// <summary>
/// ONNX token classifier (rizzo-pii layout): argmax per token, BIO tags merged into character spans (#246, #289).
/// Every file of the model folder, including the tokenizer, the config and the external weights, must be listed in
/// <see cref="PiiModelPackage.Companions"/> and so be hash-verified before the model is used.
/// </summary>
internal sealed class OnnxPiiModel : IPiiModel, IDisposable
{
    private const int MaxTokens = 8000;
    private readonly InferenceSession _session;
    private readonly HfTokenizer _tokenizer;
    private readonly string[] _labels;
    private readonly string[] _inputs;

    public OnnxPiiModel(PiiModelPackage package)
    {
        string directory = Path.GetDirectoryName(package.Path)!;
        foreach (var file in Directory.GetFiles(directory))
        {
            string name = Path.GetFileName(file);
            if (name == PiiModelPackage.ManifestFileName || name == package.File) continue;
            if (!package.Companions.ContainsKey(name))
                throw new DataEgressBlockedException("Modello del riconoscimento locale con file non verificato: nessun dato inviato.");
        }
        foreach (var required in new[] { "tokenizer.json", "config.json" })
            if (!package.Companions.ContainsKey(required))
                throw new DataEgressBlockedException("Modello del riconoscimento locale incompleto: nessun dato inviato.");
        _tokenizer = new HfTokenizer(Path.Combine(directory, "tokenizer.json"));
        using (var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "config.json"))))
        {
            var id2label = config.RootElement.GetProperty("id2label");
            _labels = new string[id2label.EnumerateObject().Count()];
            foreach (var p in id2label.EnumerateObject()) _labels[int.Parse(p.Name)] = p.Value.GetString()!;
        }
        _session = new InferenceSession(package.Path, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
        _inputs = _session.InputMetadata.Keys.ToArray();
    }

    public IReadOnlyList<NerSpan> Recognize(string block, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pieces = _tokenizer.Encode(block);
        int n = pieces.Count;
        if (n > MaxTokens) throw new NotSupportedException("Block too long for the model.");
        var ids = new DenseTensor<long>(new[] { 1, n });
        var mask = new DenseTensor<long>(new[] { 1, n });
        for (int i = 0; i < n; i++) { ids[0, i] = pieces[i].Id; mask[0, i] = 1; }
        var inputs = new List<NamedOnnxValue>();
        foreach (var name in _inputs) inputs.Add(NamedOnnxValue.CreateFromTensor(name, name.Contains("mask") ? mask : ids));
        string[] labels;
        using (var results = _session.Run(inputs))
        {
            var logits = results.First().AsTensor<float>();
            int classes = logits.Dimensions[2];
            labels = new string[n];
            for (int i = 0; i < n; i++)
            {
                int best = 0; float max = float.NegativeInfinity;
                for (int c = 0; c < classes; c++) { float v = logits[0, i, c]; if (v > max) { max = v; best = c; } }
                labels[i] = _labels[best];
            }
        }
        token.ThrowIfCancellationRequested();
        var spans = new List<NerSpan>();
        string? current = null; int start = 0, end = 0;
        void Flush()
        {
            if (current == null) return;
            while (start < end && char.IsWhiteSpace(block[start])) start++;
            while (end > start && char.IsWhiteSpace(block[end - 1])) end--;
            if (end > start) spans.Add(new NerSpan { Start = start, Length = end - start, Label = current });
            current = null;
        }
        for (int i = 0; i < n; i++)
        {
            string label = labels[i];
            if (label == "O" || pieces[i].End == pieces[i].Start) { Flush(); continue; }
            string tag = label.Substring(2);
            if (label[0] == 'B' || tag != current) { Flush(); current = tag; start = pieces[i].Start; }
            end = pieces[i].End;
        }
        Flush();
        return spans;
    }

    public void Dispose() => _session.Dispose();
}
