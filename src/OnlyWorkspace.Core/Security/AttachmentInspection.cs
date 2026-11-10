using System.Collections.Generic;

namespace OnlyWorkspace.Core.Security;

/// <summary>
/// Output of the extractor's security mode (--security): what the static reading saw in an attachment, its
/// links and what it could not read. Separate from AttachmentExtraction, so archive text and caches never change.
/// </summary>
public sealed class AttachmentInspection
{
    public string? Format { get; set; }
    public bool HasMacros { get; set; }
    /// <summary>Text, depth or size limits reached: part of the content was not read.</summary>
    public bool Truncated { get; set; }
    /// <summary>Reader problem of the attachment itself (protected, unsupported...), as in AttachmentExtraction.</summary>
    public string? Problem { get; set; }
    public string? Error { get; set; }
    public List<InspectionFinding> Findings { get; set; } = new();
    public List<InspectionLink> Links { get; set; } = new();
    public List<InspectionLimit> Limits { get; set; } = new();
    /// <summary>Files met inside the attachment (archives, attached emails), for the name and type checks.</summary>
    public List<InspectionEntry> Entries { get; set; } = new();
    /// <summary>Files read by a static reader (scripts, pages, documents with links): an unsupported top file is then not a gap.</summary>
    public int Inspected { get; set; }
}

public sealed class InspectionFinding
{
    /// <summary>Rule code (InspectionRules), never free text.</summary>
    public string Rule { get; set; } = string.Empty;
    /// <summary>File inside the attachment: «archivio.zip › avvio.ps1»; empty for the attachment itself.</summary>
    public string File { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Column { get; set; }
    public string Snippet { get; set; } = string.Empty;
}

public sealed class InspectionLink
{
    public string File { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
}

public sealed class InspectionEntry
{
    /// <summary>Path inside the attachment: «cartella.zip › fattura.pdf.exe».</summary>
    public string File { get; set; } = string.Empty;
    /// <summary>FileSignature type of the content, null when not recognised.</summary>
    public string? DetectedType { get; set; }
}

public sealed class InspectionLimit
{
    public string File { get; set; } = string.Empty;
    /// <summary>Reason code: obfuscated, encoded-script, binary-program, unreadable-script, findings-limit, links-limit, entries-limit, text-limit, timeout.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Meaning and wording of each rule: the badge depends on these local values only.</summary>
public static class InspectionRules
{
    public static (ObservationKind Kind, string Text)? Describe(string rule) => rule switch
    {
        "script.download-exec" => (ObservationKind.Deception, "Lo script scarica codice da Internet e lo esegue."),
        "script.hidden-exec" => (ObservationKind.Deception, "Lo script avvia un comando con la finestra nascosta."),
        "script.defense-change" => (ObservationKind.Deception, "Lo script disattiva o modifica protezioni di Windows (Defender, firewall o copie di ripristino)."),
        "script.encoded" => (ObservationKind.Attention, "Lo script esegue un comando codificato, non leggibile direttamente."),
        "script.obfuscated" => (ObservationKind.Attention, "Lo script contiene codice offuscato che la lettura statica non risolve."),
        "script.download" => (ObservationKind.Attention, "Lo script scarica dati o file da Internet."),
        "script.exec" => (ObservationKind.Context, "Lo script avvia programmi o comandi."),
        "html.script" => (ObservationKind.Context, "La pagina contiene script."),
        "html.password" => (ObservationKind.Attention, "La pagina chiede una password in un modulo."),
        "html.password-external" => (ObservationKind.Deception, "La pagina chiede una password e la invia a un sito esterno."),
        "html.redirect" => (ObservationKind.Attention, "La pagina porta automaticamente a un altro indirizzo."),
        "pdf.javascript" => (ObservationKind.Attention, "Il PDF contiene azioni JavaScript."),
        "pdf.launch" => (ObservationKind.Attention, "Il PDF contiene un’azione che può avviare programmi o aprire file."),
        "ooxml.remote-template" => (ObservationKind.Attention, "Il documento carica un modello da Internet quando viene aperto."),
        _ => null
    };

    public static string LimitText(string reason) => reason switch
    {
        "obfuscated" => "codice offuscato non risolto dalla lettura statica",
        "encoded-script" => "script codificato (Script Encoder) non leggibile staticamente",
        "binary-program" => "programma compilato: il comportamento non viene dedotto dalla sola presenza",
        "unreadable-script" => "script non leggibile come testo",
        "findings-limit" => "osservazioni oltre il limite: elencate solo le prime",
        "links-limit" => "link oltre il limite: controllati solo i primi",
        "entries-limit" => "file contenuti oltre il limite: controllati solo i primi nomi",
        "text-limit" => "contenuto oltre il limite di lettura",
        "timeout" => "lettura interrotta per tempo",
        _ => "contenuto non analizzato"
    };
}
