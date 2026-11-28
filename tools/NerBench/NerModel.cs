using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace NerBench;

public sealed record Span(int Start, int End, string Category, string Label);

/// <summary>ONNX token classifier of rizzo-pii: argmax per token, BIO tags merged into character spans.</summary>
public sealed class NerModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string[] _labels;
    public HfTokenizer Tokenizer { get; }
    public IReadOnlyList<string> InputNames => _session.InputMetadata.Keys.ToList();

    public NerModel(string dir)
    {
        Tokenizer = new HfTokenizer(Path.Combine(dir, "tokenizer.json"));
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "config.json")));
        var id2label = config.RootElement.GetProperty("id2label");
        _labels = new string[id2label.EnumerateObject().Count()];
        foreach (var p in id2label.EnumerateObject()) _labels[int.Parse(p.Name)] = p.Value.GetString()!;
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(Path.Combine(dir, "onnx", "model.onnx"), options);
    }

    /// <summary>Label (e.g. B-FULLNAME) of every token of <paramref name="pieces"/>.</summary>
    public string[] Classify(IReadOnlyList<Piece> pieces)
    {
        int n = pieces.Count;
        if (n > 8000) throw new NotSupportedException("The bench does not window texts over 8000 tokens.");
        var ids = new DenseTensor<long>(new[] { 1, n });
        var mask = new DenseTensor<long>(new[] { 1, n });
        for (int i = 0; i < n; i++) { ids[0, i] = pieces[i].Id; mask[0, i] = 1; }
        var inputs = new List<NamedOnnxValue>();
        foreach (var name in _session.InputMetadata.Keys)
            inputs.Add(NamedOnnxValue.CreateFromTensor(name, name.Contains("mask") ? mask : ids));
        using var results = _session.Run(inputs);
        var logits = results.First().AsTensor<float>();
        int classes = logits.Dimensions[2];
        var labels = new string[n];
        for (int i = 0; i < n; i++)
        {
            int best = 0; float max = float.NegativeInfinity;
            for (int c = 0; c < classes; c++) { float v = logits[0, i, c]; if (v > max) { max = v; best = c; } }
            labels[i] = _labels[best];
        }
        return labels;
    }

    /// <summary>Character spans of the entities, with the catalog category of each model label.</summary>
    public List<Span> Spans(string text, Func<string, string?> categoryOf)
    {
        var pieces = Tokenizer.Encode(text);
        var labels = Classify(pieces);
        var spans = new List<Span>();
        string? current = null; int start = 0, end = 0;
        void Flush()
        {
            if (current == null) return;
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            if (end > start) spans.Add(new Span(start, end, categoryOf(current) ?? current, current));
            current = null;
        }
        for (int i = 0; i < pieces.Count; i++)
        {
            var label = labels[i];
            if (label == "O" || pieces[i].End == pieces[i].Start) { Flush(); continue; }
            string tag = label[2..];
            if (label[0] == 'B' || tag != current) { Flush(); current = tag; start = pieces[i].Start; }
            end = pieces[i].End;
        }
        Flush();
        return spans;
    }

    public void Dispose() => _session.Dispose();
}
