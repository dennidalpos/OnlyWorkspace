using System;
using System.Collections.Generic;
using System.Linq;

namespace OnlyWorkspace.Core.Archive;

/// <summary>One folder-table row: what the archive knows about an email without opening it.</summary>
public sealed class ArchiveHeader
{
    public DateTime? ModifiedUtc { get; set; }
    public string EntryId { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string InReplyTo { get; set; } = string.Empty;
    public string References { get; set; } = string.Empty;
    public DateTime? SentUtc { get; set; }
    public bool ThreadMetadataComplete { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    /// <summary>SMTP address when the store exposes it, otherwise what Outlook shows (an Exchange DN).</summary>
    public string SenderAddress { get; set; } = string.Empty;
    public DateTime ReceivedUtc { get; set; }
    public bool HasAttachments { get; set; }
    public int Size { get; set; }
    /// <summary>First characters of the body, as the table returns them (255 bytes at most).</summary>
    public string Preview { get; set; } = string.Empty;
    /// <summary>Meeting request, receipt, report: not stored, but its date moves the folder bookmark.</summary>
    public bool NotEmail { get; set; }
    public bool RefreshBody { get; set; }
}

public sealed class ArchiveRecipient
{
    public string Address { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsCc { get; set; }
}

/// <summary>What opening the email adds: words for the index, recipients with their addresses, attachment names.</summary>
public sealed class ArchiveBody
{
    public string MessageId { get; set; } = string.Empty;
    /// <summary>Nothing readable now; temporary failures can be retried.</summary>
    public bool Unavailable { get; set; }
    public bool Retryable { get; set; }
    public bool RecipientsLimited { get; set; }
    public bool AttachmentsLimited { get; set; }
    public DateTime? ModifiedUtc { get; set; }
    public string? SourceVersion { get; set; }
    public string Text { get; set; } = string.Empty;
    public List<ArchiveRecipient> Recipients { get; } = new();
    public List<string> AttachmentNames { get; } = new();
    public List<ArchiveAttachment> Attachments { get; } = new();
}

public sealed class ArchiveAttachment
{
    /// <summary>Completeness contract of the extractor, independent of the file SHA-256.</summary>
    public const int CurrentReaderRevision = 2;
    public int ReaderRevision { get; set; } = CurrentReaderRevision;
    public string SourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Original attachment size in bytes; null for metadata not acquired by older readers.</summary>
    public long? Size { get; set; }
    /// <summary>File extension, without a dot; never a claim that the content was parsed.</summary>
    public string Type { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string State { get; set; } = "pending";
    public string? Problem { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool TextLimited { get; set; }

    public string ReferenceDescription() =>
        "Solo riferimento; tipo: " + Type + "; dimensione: " +
        (Size.HasValue ? Size.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " byte" : "non disponibile") + "; contenuto non estratto.";

    /// <summary>Every state an archived attachment can have, for the labels shown to people.</summary>
    public static readonly string[] States =
        { "indexed", "facts-only", "reference", "pending", "retryable", "limited", "protected", "scanned", "ocr-unavailable", "ocr-off", "unsupported", "excluded", "unavailable" };

    /// <summary>Italian label of a state (A123); an unknown state shows as such, never as «escluso».</summary>
    public static string StateLabel(string state) => state switch
    {
        "indexed" => "indicizzato", "facts-only" => "dati per fatti locali", "reference" => "solo riferimento (contenuto non estratto)", "pending" => "in attesa", "retryable" => "da riprovare",
        "limited" => "limite raggiunto", "protected" => "protetto da password", "scanned" => "scansione o immagine senza testo riconoscibile",
        "ocr-unavailable" => "scansione o immagine: OCR non disponibile su questo PC", "ocr-off" => "scansione o immagine: OCR disattivato nelle impostazioni",
        "unsupported" => "formato senza lettore di testo", "excluded" => "escluso (testo degli allegati spento)",
        "unavailable" => "non leggibile dopo ripetuti tentativi",
        _ => "stato sconosciuto: " + state
    };
}

public enum ArchiveBodyState
{
    Pending = 0,
    Indexed = 1,
    /// <summary>Protected content excluded from indexing.</summary>
    Unavailable = 2,
    /// <summary>Moved or deleted since the header was read.</summary>
    Gone = 3,
    /// <summary>Read failed or only headers are available; retry during reconciliation.</summary>
    Retryable = 4
}

/// <summary>Where an indexed email is in Outlook.</summary>
public sealed class ArchiveEmailRef
{
    public string FolderPath { get; set; } = string.Empty;
    public long Id { get; set; }
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
}

/// <summary>Scan bookmark of one mail folder.</summary>
public sealed class ArchiveFolder
{
    private const string PreviousRecoveryAdvice = "Riaprila in Outlook e attendi il prossimo controllo completo.";
    private const string RecoveryAdvice = "Controlla in Outlook, anche nel Cestino; ripristinala solo se necessario, senza ricrearla se eliminata. " +
        "Lascia aperta la cassetta per il prossimo controllo completo.";
    public long Id { get; set; }
    public string StoreId { get; set; } = string.Empty;
    public string EntryId { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    /// <summary>Oldest email read so far: the backfill resumes from here.</summary>
    public DateTime? OldestUtc { get; set; }
    public DateTime? NewestUtc { get; set; }
    /// <summary>The backfill reached the oldest email; only new mail is read from now on.</summary>
    public bool Done { get; set; }
    public string? Error { get; set; }
    public DateTime? VerifiedUtc { get; set; }

    public string UnavailableMessage() =>
        $"Cartella «{(string.IsNullOrWhiteSpace(Path) ? "nome non disponibile" : Path)}» non disponibile; nessuna eliminazione dedotta. " +
        RecoveryAdvice;

    public string DescribeError(string error)
    {
        // Correct our previously persisted advice at presentation time, without rewriting the recorded failure.
        if (error.StartsWith("Cartella «", StringComparison.Ordinal))
            return error.EndsWith(PreviousRecoveryAdvice, StringComparison.Ordinal)
                ? error.Substring(0, error.Length - PreviousRecoveryAdvice.Length) + RecoveryAdvice
                : error;
        return $"Cartella «{(string.IsNullOrWhiteSpace(Path) ? "nome non disponibile" : Path)}»: {error} " + RecoveryAdvice;
    }
}

public sealed class ArchiveHit
{
    public List<ArchiveFact> Facts { get; } = new();
    public DateTime? SentUtc { get; set; }
    public bool SearchIndexPending { get; set; }
    public bool OutsideFilters { get; set; }
    public List<string> LinkReasons { get; set; } = new();
    public List<ArchiveFact> LinkFacts { get; set; } = new();
    public List<ArchiveEmailRef> Copies { get; set; } = new();
    public long Id { get; set; }
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string SenderAddress { get; set; } = string.Empty;
    public DateTime ReceivedUtc { get; set; }
    public string Preview { get; set; } = string.Empty;
    public long ThreadId { get; set; }
    public ArchiveBodyState BodyState { get; set; }
    public string? BodyVersion { get; set; }
    public bool TextLimited { get; set; }
    public bool RecipientsLimited { get; set; }
    public bool AttachmentsLimited { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public List<ArchiveAttachment> Attachments { get; } = new();

    /// <summary>
    /// One hit per message, in the given order: Gmail labels and copies in other folders share the Message-ID and
    /// would each reach the model (99 archived emails were 73 messages on 18/10/2026). Hits without one all stay.
    /// </summary>
    public static List<ArchiveHit> OnePerMessage(IEnumerable<ArchiveHit> hits, out int copies)
    {
        var seen = new Dictionary<string, ArchiveHit>(StringComparer.Ordinal);
        var result = new List<ArchiveHit>();
        copies = 0;
        foreach (var hit in hits)
        {
            if (hit.MessageId.Length > 0 && seen.TryGetValue(hit.MessageId, out var first))
            {
                copies++;
                first.Copies.AddRange(hit.Copies);
                first.Copies.Add(new ArchiveEmailRef { Id = hit.Id, StoreId = hit.StoreId, EntryId = hit.EntryId });
                first.Copies = first.Copies.GroupBy(c => c.StoreId + "\n" + c.EntryId, StringComparer.Ordinal).Select(g => g.First()).ToList();
                first.LinkReasons = first.LinkReasons.Concat(hit.LinkReasons).Distinct(StringComparer.Ordinal).ToList();
                first.OutsideFilters &= hit.OutsideFilters;
            }
            else
            {
                if (hit.Copies.Count == 0) hit.Copies.Add(new ArchiveEmailRef { Id = hit.Id, StoreId = hit.StoreId, EntryId = hit.EntryId });
                if (hit.MessageId.Length > 0) seen.Add(hit.MessageId, hit);
                result.Add(hit);
            }
        }
        return result;
    }
}

/// <summary>One logical message, retaining its physical StoreId/EntryId copies.</summary>
public sealed class ArchiveFlowMail
{
    public string FolderPath { get; set; } = string.Empty;
    public long Id { get; set; }
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string InReplyTo { get; set; } = string.Empty;
    public string References { get; set; } = string.Empty;
    public DateTime? SentUtc { get; set; }
    public bool ThreadMetadataComplete { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public DateTime ReceivedUtc { get; set; }
    public ArchiveBodyState BodyState { get; set; }
    public List<ArchiveEmailRef> Copies { get; set; } = new();
    public List<string> LinkReasons { get; set; } = new();
    public List<ArchiveFact> LinkFacts { get; set; } = new();
    public bool OutsideFilters { get; set; }
    public bool VisibleOnly { get; set; }
    public string ParentEntryId { get; set; } = string.Empty;
    public string ParentStoreId { get; set; } = string.Empty;
    public List<string> Attachments { get; set; } = new();
}

public sealed class ArchiveFlow
{
    public List<ArchiveFlowMail> Timeline { get; set; } = new();
    public List<ArchiveFlowMail> PossibleLinks { get; set; } = new();
    public bool LimitReached { get; set; }
    public bool ConversationLimited { get; set; }
    public bool MetadataIncomplete { get; set; }
    public bool FactsIncomplete { get; set; }
}

public sealed class ArchiveFlowSeed
{
    public List<string>? AllowedStoreIds { get; set; }
    public bool IncludeAttachmentText { get; set; } = true;
    public long Id { get; set; }
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string InReplyTo { get; set; } = string.Empty;
    public string References { get; set; } = string.Empty;
    public DateTime? SentUtc { get; set; }
    public bool ThreadMetadataComplete { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string SenderAddress { get; set; } = string.Empty;
    public DateTime? ReceivedUtc { get; set; }
}

public sealed class ArchiveStoreCoverage
{
    public string StoreId { get; set; } = string.Empty;
    /// <summary>Mailbox name as Outlook shows it, from the first segment of a folder path.</summary>
    public string DisplayName { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public int Emails { get; set; }
    public int BodiesIndexed { get; set; }
    /// <summary>Bodies still to read: queued or to retry.</summary>
    public int BodiesPending { get; set; }
    public int AttachmentsIndexed { get; set; }
    /// <summary>Every attachment text known for the mailbox, whatever its state.</summary>
    public int AttachmentsTotal { get; set; }
    public int Folders { get; set; }
    /// <summary>Listed folders whose backfill finished and which have no current reading error.</summary>
    public int FoldersDone { get; set; }
    public DateTime? LastInventoryUtc { get; set; }
    public string Error { get; set; } = string.Empty;
    public bool AbsenceVerified { get; set; }
    /// <summary>Cache store (IMAP, OST) whose last closed full check asked Outlook about every email it did not list.</summary>
    public bool CacheChecked { get; set; }
    /// <summary>No longer open in Outlook: its emails stay searchable offline, its folders are out of the totals.</summary>
    public bool Closed { get; set; }
}

public enum ArchivePartyRole
{
    Any = 0,
    Sender = 1,
    Recipient = 2
}

public sealed class ArchiveQuery
{
    public List<long>? EmailIds { get; set; }
    /// <summary>Exact physical copies; null means unrestricted, empty means none.</summary>
    public List<ArchiveEmailRef>? EmailRefs { get; set; }
    public List<ArchiveFactFilter> FactFilters { get; set; } = new();
    public bool IncludeFacts { get; set; }
    public List<string>? AllowedStoreIds { get; set; }
    /// <summary>Free text: the emails whose subject or body match best come first.</summary>
    public string? Text { get; set; }
    public bool IncludeAttachmentText { get; set; } = true;
    /// <summary>Emails sent by or to any address of this counterparty.</summary>
    public long? CounterpartyId { get; set; }
    /// <summary>Emails sent by or to one of these addresses (see ArchiveDb.FindAddresses); empty list = no email.</summary>
    public IReadOnlyCollection<long>? AddressIds { get; set; }
    /// <summary>Where the addresses must appear.</summary>
    public ArchivePartyRole Role { get; set; } = ArchivePartyRole.Any;
    /// <summary>Text contained in the subject; "*" and "?" are wildcards.</summary>
    public string? Subject { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public int Max { get; set; } = 50;
    public int Offset { get; set; }
}

public sealed class ArchiveRelatedResult
{
    public List<ArchiveHit> Hits { get; set; } = new();
    public bool MetadataIncomplete { get; set; }
    public bool FactsIncomplete { get; set; }
    public int Copies { get; set; }
}

public sealed class ArchiveStats
{
    public int PendingDeletions { get; set; }
    public int Emails { get; set; }
    public int BodiesIndexed { get; set; }
    public int BodiesPending { get; set; }
    public int BodiesUnavailable { get; set; }
    public int BodiesRetryable { get; set; }
    public int TextsLimited { get; set; }
    public int RecipientsLimited { get; set; }
    public int AttachmentsLimited { get; set; }
    public int AttachmentTextsIndexed { get; set; }
    public int AttachmentTextsExcluded { get; set; }
    public int AttachmentTextsRetryable { get; set; }
    public int AttachmentTextsLimited { get; set; }
    public int AttachmentTextsPending { get; set; }
    /// <summary>Every attachment text known, whatever its state.</summary>
    public int AttachmentTexts { get; set; }
    public int FoldersFailed { get; set; }
    public int Folders { get; set; }
    /// <summary>Listed folders whose backfill finished and which have no current reading error.</summary>
    public int FoldersDone { get; set; }
    public long DbBytes { get; set; }
    public long AvailableFreeBytes { get; set; }
    public ArchiveCapacityState DiskState { get; set; }
}
