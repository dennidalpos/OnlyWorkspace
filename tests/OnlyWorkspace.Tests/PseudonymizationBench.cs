using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Tests;

/// <summary>
/// #204: a synthetic, labelled Italian corpus and the measure of what the pseudonymization catalog recognizes in it.
/// Nothing here is real: names are common words combined by a fixed generator, e-mail addresses end in .test, phone numbers
/// sit in unassigned blocks (000), IBAN, tax code and VAT numbers are built by rule from invented parts.
/// </summary>
internal static class PseudonymizationBench
{
    internal sealed record Item(string Text, (string Category, string Value)[] Labels);

    internal sealed class Stats
    {
        public int Values, Masked, CorrectCategory, FalsePositives;
        public int Missed => Values - Masked;
    }

    internal static readonly string[] Categories = { "PERSONA", "AZIENDA", "INDIRIZZO", "EMAIL", "TEL", "IBAN", "CF", "PIVA", "IMPORTO", "DATA", "PRATICA" };

    // xorshift: the same corpus on every runtime and every run.
    private sealed class Rng
    {
        private uint _s = 204;
        public int Next(int n) { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return (int)(_s % (uint)n); }
        public T Pick<T>(IReadOnlyList<T> list) => list[Next(list.Count)];
        public string Digits(int n) { var sb = new StringBuilder(); for (int i = 0; i < n; i++) sb.Append((char)('0' + Next(10))); return sb.ToString(); }
    }

    private static readonly string[] First = { "Mario", "Giulia", "Luca", "Elena", "Paolo", "Chiara", "Marco", "Sara", "Andrea", "Francesca", "Davide", "Laura", "Stefano", "Anna", "Matteo", "Silvia" };
    private static readonly string[] Last = { "Bianchi", "Verdi", "Ferri", "Gallo", "Conti", "Marini", "Greco", "Rinaldi", "Caruso", "Longo", "Moretti", "Barbieri", "Fontana", "Santoro", "Leone", "Rizzo", "De Luca", "Dal Pozzo", "Di Stefano", "Della Torre" };
    private static readonly string[] Companies = { "Alfa Costruzioni S.r.l.", "Rossi & Figli S.p.A.", "Beta Servizi SRL", "Studio Associato Gamma", "Delta Logistica SpA", "Officine Epsilon S.n.c.", "Zeta Consulting S.r.l.", "Cooperativa Sociale Eta", "Teta Impianti S.a.s.", "Iota Forniture SRL" };
    private static readonly string[] Streets = { "Via dei Test", "Via Esempio", "Viale delle Prove", "Piazza Campione", "Corso Fittizio", "Largo Sintetico", "Via Verdi", "Via Roma", "Strada Provinciale 12", "Vicolo Corto", "Lungotevere Prova", "Via Mazzini" };
    private static readonly string[] Towns = { "Milano", "Roma", "Torino", "Bologna", "Napoli", "Firenze", "Bari", "Padova" };
    private static readonly string[] Months = { "gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre" };

    private static string Name(Rng r) => r.Pick(First) + " " + r.Pick(Last);

    private static string Iban(Rng r, bool spaced)
    {
        string bban = "X00000" + "00000" + "0000" + r.Digits(8);
        string numeric = string.Concat((bban + "IT00").Select(c => char.IsDigit(c) ? c.ToString() : (c - 'A' + 10).ToString(CultureInfo.InvariantCulture)));
        int mod = 0; foreach (char c in numeric) mod = (mod * 10 + (c - '0')) % 97;
        string iban = "IT" + (98 - mod).ToString("00", CultureInfo.InvariantCulture) + bban;
        return spaced ? string.Join(" ", Enumerable.Range(0, (iban.Length + 3) / 4).Select(i => iban.Substring(i * 4, Math.Min(4, iban.Length - i * 4)))) : iban;
    }

    private static string TaxCode(Rng r)
    {
        const string consonants = "BCDFGHLMNPRSTVZ", months = "ABCDEHLMPRST";
        string L(int n, string set) { var sb = new StringBuilder(); for (int i = 0; i < n; i++) sb.Append(set[r.Next(set.Length)]); return sb.ToString(); }
        return L(6, consonants) + r.Digits(2) + L(1, months) + (1 + r.Next(28)).ToString("00", CultureInfo.InvariantCulture) + L(1, "ABCDEFGHILM") + r.Digits(3) + L(1, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
    }

    private static string Vat(Rng r)
    {
        string d = "00000" + r.Digits(5); int sum = 0;
        for (int i = 0; i < 10; i++) { int v = d[i] - '0'; if (i % 2 == 1) { v *= 2; if (v > 9) v -= 9; } sum += v; }
        return d + ((10 - sum % 10) % 10);
    }

    private static string Mobile(Rng r) => "3" + (20 + r.Next(70)) + " 000 " + r.Digits(4);
    private static string Fixed(Rng r) => "0" + r.Pick(new[] { "2", "6", "11", "51" }) + " 0000 " + r.Digits(4);

    private static Item I(string template, params (string, string)[] labels) =>
        new(string.Format(CultureInfo.InvariantCulture, template, labels.Select(l => (object)l.Item2).ToArray()), labels);

    internal static IReadOnlyList<Item> Corpus()
    {
        var r = new Rng(); var items = new List<Item>();
        for (int i = 0; i < 40; i++)
        {
            string n = Name(r); string mail = (n.Split(' ')[0] + "." + n.Split(' ')[1].Replace(" ", "")).ToLowerInvariant() + "@esempio.test";
            switch (i % 5)
            {
                case 0: items.Add(I("Gentile {0}, la ringrazio per la risposta di ieri.", ("PERSONA", n))); break;
                case 1: items.Add(I("Da: {0} <{1}>\nOggetto: riunione", ("PERSONA", n), ("EMAIL", mail))); break;
                case 2: items.Add(I("Ho parlato ieri con {0} del contratto in scadenza.", ("PERSONA", n))); break;
                case 3: items.Add(I("{0} ha confermato la riunione di lunedì.", ("PERSONA", n))); break;
                default: items.Add(I("Cordiali saluti,\n{0}", ("PERSONA", n))); break;
            }
        }
        for (int i = 0; i < 20; i++) items.Add(I(i % 2 == 0 ? "Il preventivo di {0} è allegato." : "Fattura intestata a {0}, da saldare.", ("AZIENDA", r.Pick(Companies))));
        for (int i = 0; i < 30; i++)
        {
            string town = r.Pick(Towns), street = r.Pick(Streets), no = (1 + r.Next(150)).ToString(CultureInfo.InvariantCulture);
            switch (i % 6)
            {
                case 0: items.Add(I("Sede in {0}, 20100 " + town + ".", ("INDIRIZZO", street + " " + no))); break;
                case 1: items.Add(I("L'incontro è in {0}.", ("INDIRIZZO", street + " " + no + "/A"))); break;
                case 2: items.Add(I("Spedire presso {0}.", ("INDIRIZZO", street + ", " + no))); break;
                case 3: items.Add(I("Indirizzo: {0} - 00100 " + town, ("INDIRIZZO", street + " " + no))); break;
                case 4: items.Add(I("Our office is at {0}, London.", ("INDIRIZZO", no + " Downing Street"))); break;
                default: items.Add(I("Besuch bei {0}, 10115 Berlin.", ("INDIRIZZO", "Hauptstraße " + no))); break;
            }
        }
        for (int i = 0; i < 20; i++)
        {
            string n = Name(r).Split(' ')[1].Replace(" ", "").ToLowerInvariant(), mail = n + r.Digits(2) + "@" + (i % 2 == 0 ? "esempio.test" : "azienda-fittizia.test");
            items.Add(I(i % 3 == 0 ? "Scrivimi a {0} per i dettagli." : i % 3 == 1 ? "Contatto: {0}" : "Inoltra a <{0}> entro venerdì.", ("EMAIL", mail)));
        }
        for (int i = 0; i < 24; i++)
        {
            string phone = i % 3 == 0 ? Mobile(r) : i % 3 == 1 ? Fixed(r) : "+39 " + Mobile(r);
            items.Add(I(i % 2 == 0 ? "Mi trovi al {0} dopo le 15." : "Tel. {0} (ufficio).", ("TEL", phone)));
        }
        for (int i = 0; i < 16; i++) items.Add(I(i % 2 == 0 ? "Bonifico sul conto {0}." : "IBAN {0} intestato alla società.", ("IBAN", Iban(r, i % 4 >= 2))));
        for (int i = 0; i < 16; i++) items.Add(I(i % 2 == 0 ? "Codice fiscale {0}." : "Il dipendente (CF {0}) ha firmato.", ("CF", TaxCode(r))));
        for (int i = 0; i < 16; i++)
        {
            string v = Vat(r); items.Add(I(i % 3 == 0 ? "P.IVA {0}." : i % 3 == 1 ? "Partita IVA IT{0}, sede legale a Milano." : "Fattura a {0} per la fornitura.", ("PIVA", v)));
        }
        string[] amounts = { "€ 1.250,00", "EUR 980,50", "1.250,00 euro", "euro 300", "€300", "2.500 €", "USD 1,000.00", "importo di 45,90 euro" };
        for (int i = 0; i < 24; i++) items.Add(I(i % 2 == 0 ? "Il totale è {0}." : "Saldo residuo: {0} entro il mese.", ("IMPORTO", amounts[i % amounts.Length])));
        for (int i = 0; i < 24; i++)
        {
            int d = 1 + r.Next(28), m = 1 + r.Next(12), y = 2026;
            string[] forms =
            {
                d.ToString("00", CultureInfo.InvariantCulture) + "/" + m.ToString("00", CultureInfo.InvariantCulture) + "/" + y,
                d.ToString("00", CultureInfo.InvariantCulture) + "-" + m.ToString("00", CultureInfo.InvariantCulture) + "-" + y,
                y + "-" + m.ToString("00", CultureInfo.InvariantCulture) + "-" + d.ToString("00", CultureInfo.InvariantCulture),
                d.ToString("00", CultureInfo.InvariantCulture) + "." + m.ToString("00", CultureInfo.InvariantCulture) + "." + y,
                d + " " + Months[m - 1] + " " + y,
                d.ToString("00", CultureInfo.InvariantCulture) + "/" + m.ToString("00", CultureInfo.InvariantCulture) + "/26"
            };
            items.Add(I(i % 2 == 0 ? "La scadenza è fissata al {0}." : "Riunione del {0} in sede.", ("DATA", forms[i % forms.Length])));
        }
        string[] files = { "2026-0142", "AB/2026/17", "CC-2026-088", "77/A-2026" };
        for (int i = 0; i < 10; i++) items.Add(I(i % 2 == 0 ? "Riferimento pratica {0}." : "Protocollo {0} in attesa.", ("PRATICA", files[i % files.Length])));
        // Decoys: text with no personal data that looks like some.
        string[] decoys =
        {
            "L'ordine n. 12345678901 è stato spedito.", "Codice cliente 40012345678 aggiornato.", "Versione 3.10.2020 del software installata.",
            "Il file è di 12 pagine.", "Sala 2, piano 3, scala B.", "Pagina 3 di 12.", "Interno 123 per le urgenze.", "Gentile Cliente, la contattiamo presto.",
            "Abbiamo installato Microsoft Office Outlook.", "Il progetto Via Lattea è in corso.", "Ministero dell'Economia e delle Finanze ha pubblicato la circolare.",
            "IBAN non disponibile al momento.", "Certificazione ISO 27001:2022 rinnovata.", "CAP 20100 per tutta la zona.", "Aumento del 5 per cento sul listino.",
            "Obiettivo Q3 2026 raggiunto.", "Codice articolo AB123456789 a magazzino.", "Tracking 3401234567890 in consegna.", "Protocollo interno senza numero.", "Il sistema ha 16 GB di memoria.",
            "Stanza 301 al terzo piano.", "Aggiornamento KB5034123 installato.", "Capitolo 4.2.1 della guida.", "Speriamo di vederci presto.", "Buongiorno a tutti, grazie dell'attenzione.",
            "Il rapporto 2026/11 è in bozza.", "Tabella 7 riga 12 colonna 3.", "Temperatura di 21 gradi in sala.", "Il treno delle 8.45 è in ritardo.", "Lotto 20260315 consegnato.",
            "Riunione in sala Verdi con il team Marketing.", "Contattare il Servizio Clienti Nazionale.", "La Banca Centrale ha aumentato i tassi.", "Alta Direzione e Consiglio di Amministrazione presenti.",
            "Telefonare alla reception per assistenza.", "Il documento è nella cartella Contratti Fornitori.", "Piano Nazionale di Ripresa.", "Domani riunione plenaria.", "Per info scrivere alla segreteria.", "Nessun dato personale in questo messaggio."
        };
        items.AddRange(decoys.Select(d => new Item(d, Array.Empty<(string, string)>())));
        return items;
    }

    private static readonly Regex Placeholder = new(@"\[([A-Z][A-Z0-9]{1,15})_\d+\]", RegexOptions.CultureInvariant);

    private static bool Overlaps(string a, string b) =>
        a.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0 || b.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0;

    internal static Dictionary<string, Stats> Measure(IReadOnlyList<Item> corpus)
    {
        var stats = Categories.ToDictionary(c => c, _ => new Stats(), StringComparer.Ordinal);
        foreach (var item in corpus)
        {
            var map = new Pseudonymizer(maskAmounts: true, maskDates: true);
            string masked = map.Mask(new[] { ChatMessage.User(item.Text) })[0].Content;
            var placeholders = Placeholder.Matches(masked).Cast<Match>().Select(m => (Category: m.Groups[1].Value, Original: map.Restore(m.Value))).ToList();
            foreach (var (category, value) in item.Labels)
            {
                var s = stats[category]; s.Values++;
                if (masked.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0) s.Masked++;
                if (placeholders.Any(p => p.Category == category && Overlaps(p.Original, value))) s.CorrectCategory++;
            }
            foreach (var p in placeholders)
                if (!item.Labels.Any(l => Overlaps(p.Original, l.Value)))
                {
                    if (!stats.TryGetValue(p.Category, out var s)) stats[p.Category] = s = new Stats();
                    s.FalsePositives++;
                }
        }
        return stats;
    }

    private static string Percent(int part, int whole) => whole == 0 ? "-" : (100.0 * part / whole).ToString("0", CultureInfo.InvariantCulture) + " %";

    /// <summary>The table published in docs/tecnico/pseudonimizzazione.md: the guide must equal it.</summary>
    internal static string Render(IReadOnlyList<Item> corpus, Dictionary<string, Stats> stats)
    {
        int values = corpus.Sum(i => i.Labels.Length), decoys = corpus.Count(i => i.Labels.Length == 0);
        var sb = new StringBuilder();
        sb.Append("Catalogo revisione ").Append(PseudonymizationCatalog.CurrentRevision).Append(": corpus sintetico di ").Append(corpus.Count)
          .Append(" testi, ").Append(values).Append(" valori etichettati e ").Append(decoys).Append(" testi senza dati personali (esche).\n\n");
        sb.Append("| Categoria | Valori | Mascherati | Categoria giusta | Mancati | Falsi positivi |\n| --- | --- | --- | --- | --- | --- |\n");
        foreach (var kv in stats.OrderBy(kv => Array.IndexOf(Categories, kv.Key) is var i && i < 0 ? 99 : i).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string category = kv.Key; var s = kv.Value;
            sb.Append("| ").Append(category).Append(" | ").Append(s.Values).Append(" | ").Append(Percent(s.Masked, s.Values)).Append(" | ")
              .Append(Percent(s.CorrectCategory, s.Values)).Append(" | ").Append(s.Missed).Append(" | ").Append(s.FalsePositives).Append(" |\n");
        }
        int total = stats.Values.Sum(s => s.Values), masked = stats.Values.Sum(s => s.Masked), right = stats.Values.Sum(s => s.CorrectCategory);
        sb.Append("| **Totale** | ").Append(total).Append(" | ").Append(Percent(masked, total)).Append(" | ").Append(Percent(right, total)).Append(" | ")
          .Append(total - masked).Append(" | ").Append(stats.Values.Sum(s => s.FalsePositives)).Append(" |");
        return sb.ToString();
    }
}
