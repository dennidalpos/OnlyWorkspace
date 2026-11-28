using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace OnlyWorkspace.PiiRecognizer;

/// <summary>One token with its character range in the original text (end exclusive).</summary>
internal readonly struct Piece
{
    public Piece(int id, int start, int end) { Id = id; Start = start; End = end; }
    public int Id { get; }
    public int Start { get; }
    public int End { get; }
}

/// <summary>
/// Minimal port of the Hugging Face tokenizer.json of rizzo-pii (Gemma-style BPE, 256k vocabulary): added tokens split the
/// text, spaces become ▁, Metaspace prepends ▁ to each segment and splits before every ▁, BPE with byte fallback.
/// </summary>
internal sealed class HfTokenizer
{
    private const char Space = '▁';
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string, string), int> _ranks;
    private readonly Dictionary<string, int> _added = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly string[] _addedByLength;
    private readonly int _bos, _eos, _unk;

    public HfTokenizer(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var model = root.GetProperty("model");
        _vocab = new Dictionary<string, int>(256000, StringComparer.Ordinal);
        foreach (var p in model.GetProperty("vocab").EnumerateObject()) _vocab[p.Name] = p.Value.GetInt32();
        _ranks = new Dictionary<(string, string), int>(600000);
        int rank = 0;
        foreach (var m in model.GetProperty("merges").EnumerateArray())
        {
            string a, b;
            if (m.ValueKind == JsonValueKind.String) { var s = m.GetString()!; int i = s.IndexOf(' ', 1); a = s.Substring(0, i); b = s.Substring(i + 1); }
            else { a = m[0].GetString()!; b = m[1].GetString()!; }
            { int r0 = rank++; if (!_ranks.ContainsKey((a, b))) _ranks[(a, b)] = r0; }
        }
        foreach (var t in root.GetProperty("added_tokens").EnumerateArray()) _added[t.GetProperty("content").GetString()!] = t.GetProperty("id").GetInt32();
        _addedByLength = _added.Keys.OrderByDescending(k => k.Length).ToArray();
        _unk = _vocab[model.GetProperty("unk_token").GetString()!];
        _bos = _added["<bos>"]; _eos = _added["<eos>"];
    }

    /// <summary>Token ids with offsets, framed by &lt;bos&gt; and &lt;eos&gt; (both with an empty range).</summary>
    public List<Piece> Encode(string text)
    {
        var result = new List<Piece> { new Piece(_bos, 0, 0) };
        int segmentStart = 0, i = 0;
        while (i < text.Length)
        {
            string? hit = null;
            foreach (var token in _addedByLength)
                if (string.CompareOrdinal(text, i, token, 0, token.Length) == 0) { hit = token; break; }
            if (hit == null) { i++; continue; }
            if (i > segmentStart) EncodeSegment(text, segmentStart, i, result);
            result.Add(new Piece(_added[hit], i, i + hit.Length));
            i += hit.Length; segmentStart = i;
        }
        if (segmentStart < text.Length) EncodeSegment(text, segmentStart, text.Length, result);
        result.Add(new Piece(_eos, 0, 0));
        return result;
    }

    private void EncodeSegment(string text, int from, int to, List<Piece> output)
    {
        // Normalised characters of the segment: the prepended ▁ has an empty range at the segment start.
        // Metaspace prepends ▁ only when the segment does not already start with one (a leading space becomes it).
        var chars = new List<(string Text, int Start, int End)>();
        if (text[from] != ' ') chars.Add((Space.ToString(), from, from));
        for (int i = from; i < to;)
        {
            int length = char.IsHighSurrogate(text[i]) && i + 1 < to ? 2 : 1;
            chars.Add((text[i] == ' ' ? Space.ToString() : text.Substring(i, length), i, i + length));
            i += length;
        }
        // Split before every ▁ (the delimiter merges with the text that follows; consecutive ones stay apart).
        var pieces = new List<List<(string Text, int Start, int End)>>();
        foreach (var c in chars)
        {
            if (c.Text[0] == Space || pieces.Count == 0) pieces.Add(new List<(string Text, int Start, int End)>());
            pieces[pieces.Count - 1].Add(c);
        }
        foreach (var piece in pieces) EncodePiece(piece, output);
    }

    private void EncodePiece(List<(string Text, int Start, int End)> piece, List<Piece> output)
    {
        // Initial symbols; characters missing from the vocabulary fall back to one <0xXX> token per UTF-8 byte.
        var symbols = new List<(string Text, int Start, int End)>(piece);
        while (true)
        {
            int best = -1, bestRank = int.MaxValue;
            for (int k = 0; k + 1 < symbols.Count; k++)
                if (_ranks.TryGetValue((symbols[k].Text, symbols[k + 1].Text), out int r) && r < bestRank) { bestRank = r; best = k; }
            if (best < 0) break;
            string merged = symbols[best].Text + symbols[best + 1].Text;
            if (!_vocab.ContainsKey(merged)) { _ranks.Remove((symbols[best].Text, symbols[best + 1].Text)); continue; }
            symbols[best] = (merged, symbols[best].Start, symbols[best + 1].End);
            symbols.RemoveAt(best + 1);
        }
        foreach (var s in symbols)
        {
            if (_vocab.TryGetValue(s.Text, out int id)) { output.Add(new Piece(id, s.Start, s.End)); continue; }
            foreach (byte b in Encoding.UTF8.GetBytes(s.Text))
                output.Add(new Piece(_vocab.TryGetValue($"<0x{b:X2}>", out int fb) ? fb : _unk, s.Start, s.End));
        }
    }
}
