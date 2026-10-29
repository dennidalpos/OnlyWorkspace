using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.Core.Agents;

/// <summary>What the user wants out of a set of emails: a preset, free text, or both.</summary>
public sealed class AnalysisGoal
{
    public const string Summary = "summary";
    public const string Reconstruct = "reconstruct";
    public const string OpenItems = "open";
    public const string Table = "table";
    public const string Calendar = "calendar";
    public const string FollowUps = "followups";
    /// <summary>Only the user's own request, no preset meaning.</summary>
    public const string Custom = "custom";

    public string Preset { get; set; } = Summary;
    public string Instruction { get; set; } = string.Empty;

    public bool IsReconstruction => Preset == Reconstruct;
    public bool IsTable => Preset == Table;
    public bool IsCalendar => Preset == Calendar;
    public bool IsFollowUps => Preset == FollowUps;

    public string Label => Preset switch
    {
        Reconstruct => "Ricostruisci la pratica",
        OpenItems => "Punti aperti",
        Table => "Tabella dati",
        Calendar => "Appuntamenti e scadenze",
        FollowUps => "Email senza risposta",
        Custom => "Personalizzata",
        _ => "Riepilogo"
    };

    /// <summary>Goal sentence for the prompts: preset meaning, then the user's own indications.</summary>
    public string Describe()
    {
        var preset = Preset switch
        {
            OpenItems => "Elenca i punti aperti: richieste in attesa di risposta, impegni presi, problemi non risolti, con chi li ha posti e quando.",
            Table => "Raccogli i dati principali di ogni email o documento (controparte, oggetto, date, scadenze, importi, riferimenti).",
            _ => "Riepiloga i contenuti: argomenti, decisioni, persone coinvolte, date e importi rilevanti."
        };
        var instruction = Instruction.Trim();
        if (instruction.Length == 0) return preset;
        // Free text alone drives a summary or a custom search; with another preset it refines it.
        return Preset is Summary or Custom ? instruction : preset + "\nIndicazioni dell'utente: " + instruction;
    }
}

public enum DocumentStatus
{
    Read,
    Scanned,
    Protected,
    Unsupported,
    TooLarge,
    /// <summary>Link or OLE object: Outlook cannot save it as a file.</summary>
    NotCopiable,
    /// <summary>External profile without permission to receive attachment content.</summary>
    NotSent,
    Empty,
    Error,
    Incomplete,
    /// <summary>Archived metadata only: the attachment content was intentionally not extracted.</summary>
    Reference
}

/// <summary>One attachment met during an analysis and what happened to it.</summary>
public sealed class DocumentOutcome
{
    private string? _extractionProblem;

    public string Name { get; set; } = string.Empty;
    public DocumentStatus Status { get; set; } = DocumentStatus.Incomplete;
    public string EmailSubject { get; set; } = string.Empty;
    public DateTime EmailDate { get; set; }

    public bool WasRead => Status == DocumentStatus.Read;

    /// <summary>Why an attachment was not copied for reading (OutlookDataService.CopyAttachments).</summary>
    public static DocumentStatus NotCopied(AttachmentFacts attachment, bool hasReader, bool contentAllowed) =>
        !hasReader ? DocumentStatus.Unsupported
        : !contentAllowed ? DocumentStatus.NotSent
        : attachment.Size > AttachmentFacts.MaxCopyBytes ? DocumentStatus.TooLarge
        : DocumentStatus.NotCopiable;

    /// <summary>Only complete extraction can contribute text; partial does not necessarily mean oversized.</summary>
    public void ApplyExtraction(AttachmentExtraction extraction)
    {
        _extractionProblem = extraction.Problem;
        Status = extraction.Truncated
            ? _extractionProblem is null or "" or "text-limit" or "size-limit" or "expanded-size-limit"
                ? DocumentStatus.TooLarge : DocumentStatus.Incomplete
            : extraction.Error != null ? DocumentStatus.Error
            : _extractionProblem switch
            {
                "scanned" or "ocr-unavailable" or "ocr-off" => DocumentStatus.Scanned,
                "protected" => DocumentStatus.Protected,
                "unsupported" => DocumentStatus.Unsupported,
                "archive-container" or "executable" => DocumentStatus.Reference,
                "text-limit" or "size-limit" or "expanded-size-limit" => DocumentStatus.TooLarge,
                null or "" => string.IsNullOrWhiteSpace(extraction.Text) ? DocumentStatus.Empty : DocumentStatus.Read,
                _ => DocumentStatus.Incomplete
            };
    }

    public string Reason => Status switch
    {
        DocumentStatus.Scanned => _extractionProblem switch
        {
            "ocr-unavailable" => "scansione o immagine: OCR non disponibile su questo PC",
            "ocr-off" => "scansione o immagine: OCR disattivato nelle impostazioni",
            _ => "scansione o immagine senza testo riconoscibile"
        },
        DocumentStatus.Protected => "protetto da password",
        DocumentStatus.Unsupported => UnsupportedReason(Name),
        DocumentStatus.Reference => "solo riferimento: contenuto non estratto",
        DocumentStatus.TooLarge => "troppo grande per essere letto intero",
        DocumentStatus.NotCopiable => "collegamento o oggetto incorporato che Outlook non salva come file",
        DocumentStatus.NotSent => "contenuto non inviato al provider esterno",
        DocumentStatus.Empty => "nessun testo all'interno",
        DocumentStatus.Error => "errore di lettura",
        DocumentStatus.Incomplete => _extractionProblem switch
        {
            "depth-limit" => "contenitore oltre la profondità massima di lettura",
            "scanned" => "documento non letto per intero: contiene pagine scansionate senza testo riconoscibile",
            "ocr-unavailable" => "documento non letto per intero: contiene pagine scansionate e l'OCR non è disponibile su questo PC",
            "ocr-off" => "documento non letto per intero: contiene pagine scansionate e l'OCR è disattivato nelle impostazioni",
            "protected" => "documento non letto per intero: contiene contenuti protetti da password",
            "unsupported" or "unsupported-msg-attachment" => "documento non letto per intero: contiene elementi non supportati",
            "nested-read-error" => "documento non letto per intero: errore di lettura di un elemento interno",
            "malformed-calendar" => "documento non letto per intero: calendario non valido",
            _ => "documento non letto per intero"
        },
        _ => "letto"
    };

    private static string UnsupportedReason(string name)
    {
        int dot = name.LastIndexOf('.');
        var ext = dot >= 0 ? name.Substring(dot + 1).ToLowerInvariant() : string.Empty;
        return ext switch
        {
            "doc" or "dot" or "xls" or "xlt" or "zip" => "struttura o contenuto non supportati",
            "ppt" or "pps" => "formato PowerPoint 97-2003 non supportato (salvare come .pptx)",
            "heic" or "webp" => "immagine in un formato senza OCR",
            "rar" or "7z" or "gz" or "tgz" or "cab" => "archivio compresso non supportato",
            "p7m" => "file firmato: il documento al suo interno non è in un formato supportato",
            "p7s" => "firma digitale separata, senza documento",
            _ => "formato non supportato"
        };
    }
}

public sealed class AnalysisStats
{
    public int Found { get; set; }
    public int Analyzed { get; set; }
    /// <summary>Emails cited by the final answer.</summary>
    public int Relevant { get; set; }
    public int Failed { get; set; }
    public bool Stopped { get; set; }
    public DateTime? Oldest { get; set; }
    public DateTime? Newest { get; set; }
    public List<DocumentOutcome> Documents { get; } = new();
    /// <summary>Emails (subject and date) whose whole text does not fit the profile's context: not read at all.</summary>
    public List<string> TooLong { get; } = new();
    /// <summary>Emails the profile's exposure level kept out: no card at «Minimo», or past the level's data limit.</summary>
    public List<string> NotExposed { get; } = new();
}

public static class AnalysisReport
{
    /// <summary>Deterministic closing section: what was read and, by name, what was not and why.</summary>
    public static string BuildFooter(AnalysisStats s)
    {
        var sb = new StringBuilder();
        sb.Append("**Analisi").Append(s.Stopped ? " interrotta" : string.Empty).Append("**: ")
          .Append(s.Analyzed == 1 ? "1 email letta" : $"{s.Analyzed} email lette").Append(" su ").Append(s.Found);
        if (s.Oldest.HasValue && s.Newest.HasValue) sb.Append($" (dal {s.Oldest:dd/MM/yyyy} al {s.Newest:dd/MM/yyyy})");
        sb.Append(", ").Append(s.Relevant).Append(s.Relevant == 1 ? " citata" : " citate").Append(" nella risposta.");
        if (s.Failed > 0) sb.Append(s.Failed == 1 ? " 1 email non analizzata per un errore del modello." : $" {s.Failed} email non analizzate per un errore del modello.");

        int read = s.Documents.Count(d => d.WasRead);
        if (read > 0) sb.Append(read == 1 ? " 1 allegato letto." : $" {read} allegati letti.");
        AppendNotSent(sb, s.Documents);

        if (s.TooLong.Count > 0)
        {
            sb.Append("\n\n**Email troppo lunghe per il contesto del profilo in uso (").Append(s.TooLong.Count)
              .Append(")**: non lette, per non rispondere su una parte sola. Ripeti l'analisi con un profilo dal contesto più ampio.");
            foreach (var e in s.TooLong.Take(MaxListed)) sb.Append("\n- ").Append(e);
            if (s.TooLong.Count > MaxListed) sb.Append("\n- … e altre ").Append(s.TooLong.Count - MaxListed).Append('.');
        }

        if (s.NotExposed.Count > 0)
        {
            sb.Append("\n\n**Email non inviate per il livello di esposizione del profilo (").Append(s.NotExposed.Count)
              .Append(")**: senza scheda di sintesi o oltre il limite di dati del livello. Restano nell'archivio locale.");
            foreach (var e in s.NotExposed.Take(MaxListed)) sb.Append("\n- ").Append(e);
            if (s.NotExposed.Count > MaxListed) sb.Append("\n- … e altre ").Append(s.NotExposed.Count - MaxListed).Append('.');
        }

        AppendNotRead(sb, s.Documents, withEmail: true);
        return sb.ToString();
    }

    /// <summary>
    /// Lines under an action on one email, whatever the model answered: the attachments not read and why,
    /// with the labels of the analysis footer. Empty when every attachment was read.
    /// </summary>
    public static string EmailDocuments(IReadOnlyCollection<DocumentOutcome> documents)
    {
        var notSent = new StringBuilder();
        AppendNotSent(notSent, documents);
        var sb = new StringBuilder();
        if (notSent.Length > 0) sb.Append("\n\n*").Append(notSent.ToString().TrimStart()).Append('*');
        AppendNotRead(sb, documents, withEmail: false);
        return sb.ToString();
    }

    /// <summary>
    /// The same reasons for the model, after the attachments it reads: it learns which documents the email carries
    /// and why it cannot see them, instead of guessing.
    /// </summary>
    public static string NotReadForModel(IReadOnlyCollection<DocumentOutcome> documents)
    {
        var list = NotReadList(documents);
        return list.Length == 0 ? string.Empty : "\n\n[Allegati non letti, contenuto non incluso: " + list + "]";
    }

    /// <summary>Every attachment not read, as «name (reason)», separated by «; ». Empty when all were read.</summary>
    public static string NotReadList(IEnumerable<DocumentOutcome> documents) =>
        string.Join("; ", documents.Where(d => !d.WasRead).Select(d => d.Name + " (" + d.Reason + ")"));

    // Same reason for all of them: a count, not a line per file.
    private static void AppendNotSent(StringBuilder sb, IEnumerable<DocumentOutcome> documents)
    {
        int notSent = documents.Count(d => d.Status == DocumentStatus.NotSent);
        if (notSent == 0) return;
        sb.Append(notSent == 1 ? " 1 allegato non inviato" : $" {notSent} allegati non inviati")
          .Append(" al provider esterno (opzione del profilo disattivata).");
    }

    private static void AppendNotRead(StringBuilder sb, IEnumerable<DocumentOutcome> documents, bool withEmail)
    {
        var problems = documents.Where(d => d.Status != DocumentStatus.Read && d.Status != DocumentStatus.NotSent).ToList();
        if (problems.Count == 0) return;
        sb.Append("\n\n**Documenti non letti (").Append(problems.Count).Append(")**");
        foreach (var d in problems.Take(MaxListed))
        {
            sb.Append("\n- `").Append(d.Name).Append("` — ").Append(d.Reason);
            if (withEmail) sb.Append(" · email «").Append(d.EmailSubject).Append("» del ").Append(d.EmailDate.ToString("dd/MM/yyyy"));
        }
        if (problems.Count > MaxListed) sb.Append("\n- … e altri ").Append(problems.Count - MaxListed).Append('.');
    }

    private const int MaxListed = 40;
}

public static class FlowAnalysisReport
{
    public static string MissingOriginals(int missing, IReadOnlyList<string> examples) =>
        $"\n{missing} originali non disponibili per questa analisi (possibile cache parziale, spostamento o rimozione). " +
        $"La sintesi non copre quelle email: {string.Join("; ", examples)}{(missing > examples.Count ? "; …" : string.Empty)}.";
}

public static class AnalysisTable
{
    /// <summary>TSV for the clipboard (quote=false) or CSV with RFC 4180 quoting.</summary>
    public static string ToDelimited(IReadOnlyList<string> headers, IEnumerable<string[]> rows, char separator, bool quote)
    {
        var sb = new StringBuilder();
        // Same cells as ExtractedData: values come from email text, so formulas are neutralized.
        void Row(IEnumerable<string> cells) => sb.AppendLine(string.Join(separator.ToString(), cells.Select(c => ExtractedData.Cell(c, separator, quote))));
        Row(headers);
        foreach (var row in rows) Row(row);
        return sb.ToString();
    }
}
