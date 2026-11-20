using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NerBench;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

// Thresholds fixed BEFORE the first measurement (recorded in README.md and echoed in the report).
const double PersonRecall = 0.90, CompanyRecall = 0.80, FalsePositivesPerDoc = 0.20, MsPer1000Chars = 500, PeakMemoryMb = 1500;
string[] Categories = { "PERSONA", "AZIENDA" };

string root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "NerBench.csproj"))) root = Path.GetDirectoryName(root)!;
string modelDir = args.Length > 1 ? args[1] : Path.Combine(root!, "models", "rizzo-pii-0.3B");
string results = Path.Combine(root!, "results");
var docs = Corpus.Load();
string command = args.Length > 0 ? args[0] : "bench";
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

if (command == "dump")
{
    File.WriteAllText(Path.Combine(results, "corpus.json"), JsonSerializer.Serialize(docs.Select(d => new { d.Id, d.Text }), json));
    Console.WriteLine($"corpus.json: {docs.Count} documents");
    return;
}

var catalog = PseudonymizationCatalog.Load();
var loadWatch = Stopwatch.StartNew();
using var model = new NerModel(modelDir);
loadWatch.Stop();
long loadedMb = Process.GetCurrentProcess().WorkingSet64 >> 20;

if (command == "verify")
{
    var reference = JsonDocument.Parse(File.ReadAllText(args.Length > 2 ? args[2] : Path.Combine(results, "reference.json"))).RootElement;
    int idMismatch = 0, offsetMismatch = 0, labelMismatch = 0, tokens = 0;
    foreach (var doc in docs)
    {
        var r = reference.GetProperty(doc.Id);
        var pieces = model.Tokenizer.Encode(doc.Text);
        var refIds = r.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var refOffsets = r.GetProperty("offsets").EnumerateArray().Select(x => (x[0].GetInt32(), x[1].GetInt32())).ToArray();
        var refLabels = r.GetProperty("labels").EnumerateArray().Select(x => x.GetString()!).ToArray();
        tokens += pieces.Count;
        if (!pieces.Select(p => p.Id).SequenceEqual(refIds)) { idMismatch++; Console.WriteLine($"IDS differ in {doc.Id}"); continue; }
        for (int i = 0; i < pieces.Count; i++)
            if ((pieces[i].Start, pieces[i].End) != refOffsets[i]) { offsetMismatch++; Console.WriteLine($"OFFSET {doc.Id}[{i}] ours=({pieces[i].Start},{pieces[i].End}) ref={refOffsets[i]}"); break; }
        var labels = model.Classify(pieces);
        for (int i = 0; i < labels.Length; i++)
            if (labels[i] != refLabels[i]) { labelMismatch++; Console.WriteLine($"LABEL {doc.Id}[{i}] ours={labels[i]} ref={refLabels[i]}"); break; }
    }
    Console.WriteLine($"verify: {docs.Count} docs, {tokens} tokens; documents with different ids={idMismatch}, offsets={offsetMismatch}, labels={labelMismatch}");
    Environment.ExitCode = idMismatch + offsetMismatch + labelMismatch == 0 ? 0 : 1;
    return;
}

// ---- bench ----
List<Span> CatalogSpans(string text)
{
    var map = new Pseudonymizer();
    var masked = map.Mask(new[] { new ChatMessage("user", text) })[0].Content;
    var spans = new List<Span>(); int original = 0, last = 0;
    foreach (Match m in Regex.Matches(masked, @"\[[A-Z][A-Z0-9]{1,15}_\d+\]"))
    {
        original += m.Index - last; last = m.Index + m.Length;
        var value = map.Restore(m.Value);
        string category = m.Value[1..m.Value.IndexOf('_')];
        spans.Add(new Span(original, original + value.Length, category, category));
        original += value.Length;
    }
    return spans;
}

var predictions = new Dictionary<string, (List<Span> Catalog, List<Span> Ner)>();
foreach (var doc in docs) predictions[doc.Id] = (CatalogSpans(doc.Text), model.Spans(doc.Text, l => catalog.CategoryOfNerLabel(l)));

static bool Covered(Gold g, IEnumerable<Span> spans)
{
    var cover = new bool[g.End - g.Start];
    foreach (var s in spans) for (int i = Math.Max(g.Start, s.Start); i < Math.Min(g.End, s.End); i++) cover[i - g.Start] = true;
    return cover.All(c => c);
}
static bool Overlaps(Span s, IEnumerable<Gold> gold, string category) => gold.Any(g => g.Category == category && s.Start < g.End && s.End > g.Start);

var configs = new (string Name, Func<Doc, List<Span>> Spans)[]
{
    ("catalogo", d => predictions[d.Id].Catalog),
    ("NER", d => predictions[d.Id].Ner),
    ("catalogo+NER", d => predictions[d.Id].Catalog.Concat(predictions[d.Id].Ner).ToList()),
};
var table = new List<object>(); var report = new StringBuilder();
var recalls = new Dictionary<string, double>(); var falsePositiveRates = new Dictionary<string, double>();
report.AppendLine("| Configurazione | PERSONA mascherata | PERSONA categoria | AZIENDA mascherata | AZIENDA categoria | Falsi positivi (spans) | per documento |");
report.AppendLine("|---|---|---|---|---|---|---|");
foreach (var (name, spansOf) in configs)
{
    var cells = new List<string>(); int fp = 0;
    foreach (var category in Categories)
    {
        var gold = docs.SelectMany(d => d.Spans.Where(g => g.Category == category).Select(g => (d, g))).ToList();
        int masked = gold.Count(x => Covered(x.g, spansOf(x.d)));
        int exact = gold.Count(x => Covered(x.g, spansOf(x.d).Where(s => s.Category == category)));
        cells.Add($"{masked}/{gold.Count} ({100.0 * masked / gold.Count:F0}%)"); cells.Add($"{exact}/{gold.Count} ({100.0 * exact / gold.Count:F0}%)");
        recalls[name + "|" + category] = (double)exact / gold.Count;
    }
    var fpList = new List<string>();
    foreach (var d in docs)
        foreach (var s in spansOf(d).Where(s => Categories.Contains(s.Category)))
            if (!Overlaps(s, d.Spans, s.Category)) { fp++; fpList.Add($"{d.Id}:{s.Category}:«{d.Text[s.Start..s.End]}»"); }
    falsePositiveRates[name] = (double)fp / docs.Count;
    report.AppendLine($"| {name} | {cells[0]} | {cells[1]} | {cells[2]} | {cells[3]} | {fp} | {(double)fp / docs.Count:F2} |");
    table.Add(new { name, falsePositives = fpList });
}

// Misses of the combined configuration, for the dossier.
var missLines = new List<string>();
foreach (var d in docs)
    foreach (var g in d.Spans)
    {
        string flags = string.Join(" ", configs.Select(c => $"{c.Name}={(Covered(g, c.Spans(d).Where(s => s.Category == g.Category)) ? "ok" : "no")}"));
        if (flags.Contains("=no"))
        {
            string seen = string.Join(", ", predictions[d.Id].Ner.Where(s => s.Start < g.End && s.End > g.Start).Select(s => $"«{d.Text[s.Start..s.End]}» {s.Label}"));
            missLines.Add($"- {d.Id} {g.Category} «{d.Text[g.Start..g.End]}»: {flags}" + (flags.Contains("NER=no") ? $"; il NER vede: {(seen.Length == 0 ? "niente" : seen)}" : ""));
        }
    }

// Speed and memory of the NER layer (tokenizer + ONNX), warm.
foreach (var d in docs.Take(3)) model.Spans(d.Text, l => null);
var perDoc = new List<double>(); long chars = 0;
foreach (var d in docs)
{
    var times = new List<double>();
    for (int i = 0; i < 5; i++) { var w = Stopwatch.StartNew(); model.Spans(d.Text, l => null); w.Stop(); times.Add(w.Elapsed.TotalMilliseconds); }
    times.Sort(); perDoc.Add(times[times.Count / 2]); chars += d.Text.Length;
}
double msPer1000 = perDoc.Sum() / chars * 1000;
string longText = string.Join("\n\n", Enumerable.Repeat(string.Join("\n\n", docs.Where(d => !d.Negative).Select(d => d.Text)), 1));
while (longText.Length < 3000) longText += "\n\n" + longText;
var longTimes = new List<double>();
for (int i = 0; i < 5; i++) { var w = Stopwatch.StartNew(); model.Spans(longText, l => null); w.Stop(); longTimes.Add(w.Elapsed.TotalMilliseconds); }
longTimes.Sort(); double longMsPer1000 = longTimes[longTimes.Count / 2] / longText.Length * 1000;
long peakMb = Process.GetCurrentProcess().PeakWorkingSet64 >> 20;
var catalogWatch = Stopwatch.StartNew(); foreach (var d in docs) CatalogSpans(d.Text); catalogWatch.Stop();

string Verdict(bool ok) => ok ? "RISPETTATA" : "NON rispettata";
var thresholds = new StringBuilder();
thresholds.AppendLine("| Soglia (fissata prima della misura) | Valore | Misurato (catalogo+NER) | Esito |");
thresholds.AppendLine("|---|---|---|---|");
double p = recalls["catalogo+NER|PERSONA"], a = recalls["catalogo+NER|AZIENDA"], f = falsePositiveRates["catalogo+NER"];
thresholds.AppendLine($"| Richiamo PERSONA (categoria corretta) | >= {PersonRecall:P0} | {p:P0} | {Verdict(p >= PersonRecall)} |");
thresholds.AppendLine($"| Richiamo AZIENDA (categoria corretta) | >= {CompanyRecall:P0} | {a:P0} | {Verdict(a >= CompanyRecall)} |");
thresholds.AppendLine($"| Falsi positivi PERSONA/AZIENDA per documento | <= {FalsePositivesPerDoc:F2} | {f:F2} | {Verdict(f <= FalsePositivesPerDoc)} |");
thresholds.AppendLine($"| Tempo NER per 1000 caratteri (documenti brevi) | <= {MsPer1000Chars:F0} ms | {msPer1000:F0} ms (testo lungo: {longMsPer1000:F0} ms) | {Verdict(msPer1000 <= MsPer1000Chars)} |");
thresholds.AppendLine($"| Memoria di picco del processo | <= {PeakMemoryMb:F0} MB | {peakMb} MB | {Verdict(peakMb <= PeakMemoryMb)} |");

var output = new StringBuilder();
output.AppendLine("# Banco locale: catalogo di mascheramento con e senza NER (#281)\n");
output.AppendLine($"Data: {DateTime.Now:yyyy-MM-dd HH:mm}. Corpus sintetico: {docs.Count} documenti ({docs.Count(d => !d.Negative)} con dati, {docs.Count(d => d.Negative)} senza), {docs.Sum(d => d.Spans.Count)} entità annotate, {chars} caratteri.");
output.AppendLine($"Modello: rizzo-pii-0.3B ONNX fp32 (rizzoaiacademy/rizzo-pii-0.3B, MIT), OnnxRuntime CPU, caricamento {loadWatch.Elapsed.TotalSeconds:F1} s, memoria dopo il caricamento {loadedMb} MB.");
output.AppendLine($"Hardware: {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}, {Environment.ProcessorCount} thread logici, {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 30} GB RAM, {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}.\n");
output.AppendLine("## Richiamo e falsi positivi\n");
output.AppendLine("«mascherata» = ogni carattere dell'entità è coperto da un intervallo mascherato (qualsiasi categoria); «categoria» = coperto da intervalli della categoria giusta.\n");
output.Append(report).AppendLine();
output.AppendLine("## Soglie\n").Append(thresholds).AppendLine();
output.AppendLine($"Catalogo da solo: {catalogWatch.Elapsed.TotalMilliseconds / docs.Count:F1} ms per documento.\n");
output.AppendLine("## Mancati (almeno una configurazione)\n");
foreach (var line in missLines) output.AppendLine(line);
output.AppendLine("\n## Falsi positivi\n");
foreach (var t in table) { var o = JsonSerializer.Serialize(t, json); output.AppendLine("```json\n" + o + "\n```"); }
output.AppendLine("\n## Limiti\n\n- Il catalogo gira senza l'indice delle entità locali (nomi già noti dall'archivio) e senza intestazioni reali: la sua base è sottostimata rispetto all'uso nel prodotto.\n- Corpus piccolo e scritto a mano: indica una direzione, non una qualifica.\n- Modello fp32 ufficiale, non l'int8 da 308 MB del 15/11: tempi e memoria non sono confrontabili con quelle misure.\n- Testi oltre 8000 token non sono finestrati dal banco.\n- Nessuna distribuzione del modello né riuso dei dataset originali.");
File.WriteAllText(Path.Combine(results, "report.md"), output.ToString());
Console.WriteLine(output.ToString());
