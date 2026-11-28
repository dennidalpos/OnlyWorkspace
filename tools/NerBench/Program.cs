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
string modelDir = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.Combine(root!, "models", "rizzo-pii-0.3B");
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
// #292: without the model folder the bench still measures the archive index and the catalog; NER is reported as not measured.
bool hasModel = File.Exists(Path.Combine(modelDir, "tokenizer.json"));
using var model = hasModel ? new NerModel(modelDir) : null;
loadWatch.Stop();
long loadedMb = Process.GetCurrentProcess().WorkingSet64 >> 20;

if (command == "real-coverage")
{
    // #316: counts only, never names or text (see RealCoverage).
    if (model == null) { Console.Error.WriteLine($"real-coverage needs the model in {modelDir}"); Environment.ExitCode = 2; return; }
    try { Console.WriteLine(await RealCoverage.RunAsync(model, catalog, RealCoverage.Parse(args))); }
    catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException) { Console.Error.WriteLine($"real-coverage failed: {ex.GetType().Name}"); Environment.ExitCode = 3; }
    return;
}

if (command == "verify")
{
    if (model == null) { Console.Error.WriteLine($"verify needs the model in {modelDir}"); Environment.ExitCode = 2; return; }
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

if (command == "verify-synthetic")
{
    if (model == null) { Console.Error.WriteLine($"verify-synthetic needs the model in {modelDir}"); Environment.ExitCode = 2; return; }
    var texts = JsonDocument.Parse(File.ReadAllText(Path.Combine(results, "synthetic.json"))).RootElement;
    var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(results, "synthetic-reference.json"))).RootElement;
    int different = 0, count = 0;
    foreach (var item in texts.EnumerateArray())
    {
        count++;
        string id = item.GetProperty("Id").GetString()!;
        var r = expected.GetProperty(id);
        var pieces = model.Tokenizer.Encode(item.GetProperty("Text").GetString()!);
        var refIds = r.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        // The reference counts code points, .NET counts UTF-16 units: translate the reference before comparing.
        string source = item.GetProperty("Text").GetString()!;
        var utf16 = new List<int>(); // code point index -> UTF-16 index
        for (int k = 0; k < source.Length; k += char.IsHighSurrogate(source[k]) && k + 1 < source.Length ? 2 : 1) utf16.Add(k);
        utf16.Add(source.Length);
        var refOffsets = r.GetProperty("offsets").EnumerateArray().Select(x => (utf16[x[0].GetInt32()], utf16[x[1].GetInt32()])).ToArray();
        bool same = pieces.Select(p => p.Id).SequenceEqual(refIds) && pieces.Select(p => (p.Start, p.End)).SequenceEqual(refOffsets);
        if (!same)
        {
            different++;
            Console.WriteLine($"DIFFERENT {id}: ours={pieces.Count} ref={refIds.Length}");
            Console.WriteLine("  ours: " + string.Join(" ", pieces.Select(p => $"{p.Id}({p.Start},{p.End})")));
            Console.WriteLine("  ref:  " + string.Join(" ", refIds.Select((x, i) => $"{x}({refOffsets[i].Item1},{refOffsets[i].Item2})")));
        }
    }
    Console.WriteLine($"verify-synthetic: {count} texts, different={different}");
    Environment.ExitCode = different == 0 ? 0 : 1;
    return;
}

// ---- bench ----
// Archive fixture (rule fixed in README.md): every annotated entity at an even position within its category is a known display name.
var archive = new List<PrivacyEntity>();
foreach (var category in Categories)
{
    int position = 0;
    foreach (var g in docs.SelectMany(d => d.Spans.Where(g => g.Category == category).Select(g => (d, g))))
        if (position++ % 2 == 0)
        {
            var name = g.d.Text[g.g.Start..g.g.End];
            archive.Add(new PrivacyEntity { Category = PrivacyEntityCategory.OfDisplayName(name), Value = name });
        }
}

List<Span> CatalogSpans(string text, IEnumerable<PrivacyEntity>? entities = null)
{
    var map = new Pseudonymizer(entities);
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

var predictions = new Dictionary<string, (List<Span> Catalog, List<Span> Index, List<Span> Ner)>();
foreach (var doc in docs) predictions[doc.Id] = (CatalogSpans(doc.Text), CatalogSpans(doc.Text, archive), model?.Spans(doc.Text, l => catalog.CategoryOfNerLabel(l)) ?? new List<Span>());

static bool Covered(Gold g, IEnumerable<Span> spans)
{
    var cover = new bool[g.End - g.Start];
    foreach (var s in spans) for (int i = Math.Max(g.Start, s.Start); i < Math.Min(g.End, s.End); i++) cover[i - g.Start] = true;
    return cover.All(c => c);
}
static bool Overlaps(Span s, IEnumerable<Gold> gold, string category) => gold.Any(g => g.Category == category && s.Start < g.End && s.End > g.Start);

// #308: archive coverage sweep. Fixed before the run: an annotated entity is a known archive name when (position * 37 % 100) < coverage
// (position counted per category in corpus order); the archive also holds 600 invented distractor names that never occur in the corpus.
// Question answered: at which archive coverage does AZIENDA reach 80% in the correct category (false positives <= 0.20, PERSONA >= 90%)?
if (command == "coverage")
{
    string[] firstNames = { "Alberto", "Beatrice", "Carlo", "Daniela", "Emilio", "Franca", "Gianni", "Irene", "Livio", "Marta", "Nicola", "Paola", "Renzo", "Sonia", "Tullio", "Vera" };
    string[] surnames = { "Arduini", "Bellandi", "Cortesi", "Dallari", "Fumagalli", "Gentilini", "Lombardo", "Mazzanti", "Nardini", "Orlandi", "Pasini", "Quaranta", "Ricciardi", "Sartori", "Tedeschi", "Vannucci" };
    string[] stems = { "Veltrix", "Casalbo", "Montecor", "Aldrinia", "Brenta", "Cavallis", "Dorimar", "Elbasud", "Fontaris", "Galvani", "Hertel", "Ilario", "Joviana", "Karmel", "Lunaria", "Merletti" };
    string[] tails = { "Costruzioni", "Servizi", "Trasporti", "Consulting", "Impianti", "Alimentari", "Editoriale", "Logistica", "Arredi", "Software" };
    string[] forms = { "S.r.l.", "S.p.A.", "S.n.c.", "& C.", "" };
    var distractors = new List<PrivacyEntity>();
    for (int i = 0; i < 300; i++)
    {
        var person = $"{firstNames[i % firstNames.Length]} {surnames[(i / firstNames.Length + i * 3) % surnames.Length]}{(i >= 256 ? " " + (char)('A' + i % 26) + "." : "")}";
        var company = $"{stems[(i * 5) % stems.Length]} {tails[(i / stems.Length) % tails.Length]} {forms[i % forms.Length]}".Trim();
        distractors.Add(new PrivacyEntity { Category = PrivacyEntityCategory.OfDisplayName(person), Value = person });
        distractors.Add(new PrivacyEntity { Category = PrivacyEntityCategory.OfDisplayName(company), Value = company });
    }
    var nerSpans = docs.ToDictionary(d => d.Id, d => model?.Spans(d.Text, l => catalog.CategoryOfNerLabel(l)) ?? new List<Span>());
    var sweep = new StringBuilder();
    sweep.AppendLine($"# #308: AZIENDA recall against archive coverage ({docs.Count} documents, {docs.Sum(d => d.Spans.Count(g => g.Category == "AZIENDA"))} AZIENDA)\n");
    sweep.AppendLine($"Archive = known corpus names at the stated coverage + {distractors.Count} invented distractor names. Product row = {(model != null ? "indice+catalogo+NER" : "indice+catalogo (model absent)")}.\n");
    sweep.AppendLine("| Coverage | Archive names | AZIENDA correct category | PERSONA correct category | False positives per doc | AZIENDA >= 80% |");
    sweep.AppendLine("|---|---|---|---|---|---|");
    foreach (int coverage in new[] { 0, 30, 50, 70, 90, 100 })
    {
        var known = new List<PrivacyEntity>(distractors);
        foreach (var category in Categories)
        {
            int position = 0;
            foreach (var g in docs.SelectMany(d => d.Spans.Where(g => g.Category == category).Select(g => (d, g))))
                if (position++ * 37 % 100 < coverage)
                {
                    var name = g.d.Text[g.g.Start..g.g.End];
                    known.Add(new PrivacyEntity { Category = PrivacyEntityCategory.OfDisplayName(name), Value = name });
                }
        }
        List<Span> Product(Doc d) => CatalogSpans(d.Text, known).Concat(nerSpans[d.Id]).ToList();
        var cache = docs.ToDictionary(d => d.Id, Product);
        var cells = new List<string>(); double companyRecall = 0;
        foreach (var category in Categories)
        {
            var gold = docs.SelectMany(d => d.Spans.Where(g => g.Category == category).Select(g => (d, g))).ToList();
            int exact = gold.Count(x => Covered(x.g, cache[x.d.Id].Where(s => s.Category == category)));
            if (category == "AZIENDA") companyRecall = (double)exact / gold.Count;
            cells.Add($"{exact}/{gold.Count} ({100.0 * exact / gold.Count:F0}%)");
        }
        int fp = docs.Sum(d => cache[d.Id].Where(s => Categories.Contains(s.Category)).Count(s => !Overlaps(s, d.Spans, s.Category)));
        sweep.AppendLine($"| {coverage}% | {known.Count} | {cells[1]} | {cells[0]} | {(double)fp / docs.Count:F2} | {(companyRecall >= CompanyRecall ? "yes" : "no")} |");
    }
    File.WriteAllText(Path.Combine(results, "coverage.md"), sweep.ToString());
    Console.WriteLine(sweep.ToString());
    return;
}

var configList = new List<(string Name, Func<Doc, List<Span>> Spans)>
{
    ("catalogo", d => predictions[d.Id].Catalog),
    ("indice+catalogo", d => predictions[d.Id].Index),
};
if (model != null)
{
    configList.Add(("NER", d => predictions[d.Id].Ner));
    configList.Add(("catalogo+NER", d => predictions[d.Id].Catalog.Concat(predictions[d.Id].Ner).ToList()));
    configList.Add(("indice+catalogo+NER", d => predictions[d.Id].Index.Concat(predictions[d.Id].Ner).ToList()));
}
var configs = configList.ToArray();
string product = model != null ? "indice+catalogo+NER" : "indice+catalogo";
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
long chars = docs.Sum(d => d.Text.Length);
double msPer1000 = double.NaN, longMsPer1000 = double.NaN;
if (model != null)
{
    foreach (var d in docs.Take(3)) model.Spans(d.Text, l => null);
    var perDoc = new List<double>();
    foreach (var d in docs)
    {
        var times = new List<double>();
        for (int i = 0; i < 5; i++) { var w = Stopwatch.StartNew(); model.Spans(d.Text, l => null); w.Stop(); times.Add(w.Elapsed.TotalMilliseconds); }
        times.Sort(); perDoc.Add(times[times.Count / 2]);
    }
    msPer1000 = perDoc.Sum() / chars * 1000;
    string longText = string.Join("\n\n", Enumerable.Repeat(string.Join("\n\n", docs.Where(d => !d.Negative).Select(d => d.Text)), 1));
    while (longText.Length < 3000) longText += "\n\n" + longText;
    var longTimes = new List<double>();
    for (int i = 0; i < 5; i++) { var w = Stopwatch.StartNew(); model.Spans(longText, l => null); w.Stop(); longTimes.Add(w.Elapsed.TotalMilliseconds); }
    longTimes.Sort(); longMsPer1000 = longTimes[longTimes.Count / 2] / longText.Length * 1000;
}
long peakMb = Process.GetCurrentProcess().PeakWorkingSet64 >> 20;
var catalogWatch = Stopwatch.StartNew(); foreach (var d in docs) CatalogSpans(d.Text); catalogWatch.Stop();

// #292: held-out slice (q29-q56) on the product configuration, and person names the index labels AZIENDA (full corpus).
var heldOut = docs.Where(d => string.CompareOrdinal(d.Id, "q29") >= 0 && string.CompareOrdinal(d.Id, "q56") <= 0).ToList();
var productSpans = configs.First(c => c.Name == product).Spans;
var heldOutLine = new StringBuilder();
foreach (var category in Categories)
{
    var gold = heldOut.SelectMany(d => d.Spans.Where(g => g.Category == category).Select(g => (d, g))).ToList();
    int exact = gold.Count(x => Covered(x.g, productSpans(x.d).Where(s => s.Category == category)));
    recalls["heldout|" + category] = (double)exact / gold.Count;
    heldOutLine.Append($"{category} {exact}/{gold.Count} ({100.0 * exact / gold.Count:F0}%); ");
}
int heldOutFp = heldOut.Sum(d => productSpans(d).Where(s => Categories.Contains(s.Category)).Count(s => !Overlaps(s, d.Spans, s.Category)));
falsePositiveRates["heldout"] = (double)heldOutFp / heldOut.Count;
heldOutLine.Append($"falsi positivi {heldOutFp} su {heldOut.Count} documenti ({falsePositiveRates["heldout"]:F2} per documento)");
int personAsCompany = docs.SelectMany(d => d.Spans.Where(g => g.Category == "PERSONA").Select(g => (d, g)))
    .Count(x => predictions[x.d.Id].Index.Any(s => s.Category == "AZIENDA" && s.Start < x.g.End && s.End > x.g.Start));

string Verdict(bool ok) => ok ? "RISPETTATA" : "NON rispettata";
var thresholds = new StringBuilder();
thresholds.AppendLine($"| Soglia (fissata prima della misura) | Valore | Misurato ({product}) | Esito |");
thresholds.AppendLine("|---|---|---|---|");
double p = recalls[product + "|PERSONA"], a = recalls[product + "|AZIENDA"], f = falsePositiveRates[product];
thresholds.AppendLine($"| Richiamo PERSONA (categoria corretta) | >= {PersonRecall:P0} | {p:P0} | {Verdict(p >= PersonRecall)} |");
thresholds.AppendLine($"| Richiamo AZIENDA (categoria corretta) | >= {CompanyRecall:P0} | {a:P0} | {Verdict(a >= CompanyRecall)} |");
thresholds.AppendLine($"| Falsi positivi PERSONA/AZIENDA per documento | <= {FalsePositivesPerDoc:F2} | {f:F2} | {Verdict(f <= FalsePositivesPerDoc)} |");
if (model != null)
{
    thresholds.AppendLine($"| Tempo NER per 1000 caratteri (documenti brevi) | <= {MsPer1000Chars:F0} ms | {msPer1000:F0} ms (testo lungo: {longMsPer1000:F0} ms) | {Verdict(msPer1000 <= MsPer1000Chars)} |");
    thresholds.AppendLine($"| Memoria di picco del processo | <= {PeakMemoryMb:F0} MB | {peakMb} MB | {Verdict(peakMb <= PeakMemoryMb)} |");
}
else thresholds.AppendLine("| Tempo NER e memoria di picco | - | non misurati: modello assente | NON misurata |");

var output = new StringBuilder();
output.AppendLine("# Banco locale: catalogo di mascheramento con e senza NER (#281)\n");
output.AppendLine($"Data: {DateTime.Now:yyyy-MM-dd HH:mm}. Corpus sintetico: {docs.Count} documenti ({docs.Count(d => !d.Negative)} con dati, {docs.Count(d => d.Negative)} senza), {docs.Sum(d => d.Spans.Count)} entità annotate, {chars} caratteri.");
output.AppendLine(model != null
    ? $"Modello: rizzo-pii-0.3B ONNX fp32 (rizzoaiacademy/rizzo-pii-0.3B, MIT), OnnxRuntime CPU, caricamento {loadWatch.Elapsed.TotalSeconds:F1} s, memoria dopo il caricamento {loadedMb} MB."
    : "Modello: assente, NER non misurato (le righe con NER mancano; la soglia AZIENDA qui vale per indice+catalogo, senza il NER).");
output.AppendLine($"Archivio di prova: {archive.Count} nomi noti (una entità annotata su due per categoria, categoria con PrivacyEntityCategory.OfDisplayName).");
output.AppendLine($"Hardware: {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}, {Environment.ProcessorCount} thread logici, {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 30} GB RAM, {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}.\n");
output.AppendLine("## Richiamo e falsi positivi\n");
output.AppendLine("«mascherata» = ogni carattere dell'entità è coperto da un intervallo mascherato (qualsiasi categoria); «categoria» = coperto da intervalli della categoria giusta.\n");
output.Append(report).AppendLine();
output.AppendLine("## Soglie\n").Append(thresholds).AppendLine();
output.AppendLine($"Fetta tenuta fuori ({product}, documenti q29-q56): {heldOutLine}.");
output.AppendLine($"Nomi di persona etichettati AZIENDA dall'indice (corpus intero): {personAsCompany}.\n");
output.AppendLine($"Catalogo da solo: {catalogWatch.Elapsed.TotalMilliseconds / docs.Count:F1} ms per documento.\n");
output.AppendLine("## Mancati (almeno una configurazione)\n");
foreach (var line in missLines) output.AppendLine(line);
output.AppendLine("\n## Falsi positivi\n");
foreach (var t in table) { var o = JsonSerializer.Serialize(t, json); output.AppendLine("```json\n" + o + "\n```"); }
output.AppendLine("\n## Limiti\n\n- Le righe «catalogo» girano senza l'indice delle entità locali; le righe «indice+...» usano un archivio di prova con circa metà delle entità (regola fissata nel README) e senza intestazioni reali: la copertura reale dell'archivio può essere diversa.\n- Corpus scritto a mano (88 documenti, 75 AZIENDA): indica una direzione, non una qualifica. La fetta q29-q56 è tenuta fuori dalla regola ma scritta dallo stesso autore del lessico: è ottimistica.\n- Modello fp32 ufficiale, non l'int8 da 308 MB del 15/11: tempi e memoria non sono confrontabili con quelle misure.\n- Testi oltre 8000 token non sono finestrati dal banco.\n- Nessuna distribuzione del modello né riuso dei dataset originali.");
File.WriteAllText(Path.Combine(results, "report.md"), output.ToString());
Console.WriteLine(output.ToString());
