namespace OnlyWorkspace.Core.Security;

/// <summary>Text formats shared by the caller and the isolated reader.</summary>
public static class AttachmentTextFormats
{
    public static bool HasTextReader(string extension) => extension switch
    {
        "pdf" or "docx" or "docm" or "dotx" or "dotm" or "xlsx" or "xlsm" or "xltx" or "xltm" or "xlam"
            or "pptx" or "pptm" or "potx" or "potm" or "ppsx" or "ppsm" or "ppam"
            or "odt" or "ods" or "odp" or "ott" or "ots" or "otp" or "rtf" or "html" or "htm"
            or "txt" or "csv" or "tsv" or "md" or "log" or "json" or "xml" or "yaml" or "yml" or "ini" or "ics"
            or "vcf" or "sql" or "srt" or "cfg" or "conf" or "properties" or "tex" or "rst" or "kml" or "gpx"
            or "msg" or "eml" or "p7m" or "zip" or "doc" or "dot" or "xls" or "xlt"
            or "png" or "jpg" or "jpeg" or "gif" or "tif" or "tiff" or "bmp" => true,
        _ => false
    };

    /// <summary>Archive references are retained without parsing their contents. Office ZIP packages are documents.</summary>
    public static string? ArchiveReferenceReason(string extension, string? detectedType = null)
    {
        extension = extension.TrimStart('.').Trim().ToLowerInvariant();
        if (detectedType == "exe" || extension is "exe" or "dll" or "scr" or "cpl" or "sys" or "ocx" or "com" or "msi" or "msix" or "appx")
            return "executable";
        if (extension is "zip" or "rar" or "7z" or "gz" or "gzip" or "tgz" or "tar" or "bz2" or "xz" or "cab" or "jar" ||
            detectedType is "rar" or "7z" or "gzip" or "cab" || detectedType == "zip" && !IsDocumentPackage(extension))
            return "archive-container";
        return HasTextReader(extension) ? null : "unsupported";
    }

    private static bool IsDocumentPackage(string extension) => extension is
        "docx" or "docm" or "dotx" or "dotm" or "xlsx" or "xlsm" or "xltx" or "xltm" or "xlam"
        or "pptx" or "pptm" or "potx" or "potm" or "ppsx" or "ppsm" or "ppam"
        or "odt" or "ods" or "odp" or "ott" or "ots" or "otp";
}
