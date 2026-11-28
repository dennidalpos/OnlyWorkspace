using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Security;

/// <summary>
/// Which attachments the AI reading of «Verifica sicurezza» may include, and the sentence that says why one was left out (#177).
/// Not sending a text the profile forbids is policy, not a gap of the check: only the other reasons make the report partial.
/// </summary>
public static class SecurityAttachmentPolicy
{
    /// <summary>The reason an attachment is excluded before it is read, or null when it can be read.</summary>
    /// <param name="hasSavedCopy">The attachment was copied locally, so its text can be extracted.</param>
    /// <returns><c>Forbidden</c> is true when the profile does not allow attachment text to be sent (policy, not a gap).</returns>
    public static (string? Reason, bool Forbidden) BeforeReading(LlmProfileConfig profile, AttachmentFacts facts, bool hasSavedCopy)
    {
        bool allowed = SecurityContentAnalysis.AttachmentContentAllowed(profile, facts);
        bool forbidden = !allowed && facts.ScanStatus is not (AttachmentScanStatus.Threat or AttachmentScanStatus.Removed);
        string? reason = !allowed
            ? forbidden ? "invio del contenuto non consentito dal profilo" : "escluso dopo il controllo antivirus"
            : !AttachmentTextFormats.HasTextReader(facts.Extension) ? "formato senza estrazione del testo" : !hasSavedCopy ? "copia locale non disponibile" : null;
        return (reason, forbidden);
    }

    /// <summary>The reason an extraction cannot be used whole, or null when its text is complete: a partial text is never used.</summary>
    public static string? AfterReading(AttachmentExtraction extraction) =>
        extraction.Truncated || extraction.Error != null || extraction.Problem != null || string.IsNullOrWhiteSpace(extraction.Text)
            ? extraction.Truncated ? "oltre il limite di lettura: nessun testo parziale usato" : "testo non leggibile integralmente"
            : null;

    /// <summary>The line of the report that names the attachment and the reason its content was not read by the model.</summary>
    public static string LimitText(string attachmentName, string reason) =>
        "«" + attachmentName + "»: contenuto non incluso nella lettura IA (" + reason + ").";
}
