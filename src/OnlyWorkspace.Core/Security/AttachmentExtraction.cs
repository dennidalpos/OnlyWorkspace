namespace OnlyWorkspace.Core.Security;

/// <summary>JSON contract printed by OnlyWorkspace.AttachmentExtractor.exe on stdout.</summary>
public sealed class AttachmentExtraction
{
    /// <summary>pdf, docx, xlsx, pptx, odf, text, html, rtf, msg, eml, ole, image, p7m (envelope not opened) or unsupported.</summary>
    public string Format { get; set; } = "unsupported";
    /// <summary>Why there is no text: null when read, else scanned (scan or image without recognizable text), ocr-unavailable, ocr-off (disabled by the administrator), protected, unsupported.</summary>
    public string? Problem { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool Truncated { get; set; }
    /// <summary>VBA project found (OOXML vbaProject part or OLE VBA storage).</summary>
    public bool HasMacros { get; set; }
    public string? Error { get; set; }
}
