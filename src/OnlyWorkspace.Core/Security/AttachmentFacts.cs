using System;
using System.Collections.Generic;
using System.Text;

namespace OnlyWorkspace.Core.Security;

public enum AttachmentScanStatus
{
    NotScanned,
    Clean,
    Threat,
    /// <summary>Saved copy deleted right away: real-time protection blocked it.</summary>
    Removed,
    Failed
}

/// <summary>Attachment metadata for the security check (phase 1: no content parsing).</summary>
public sealed class AttachmentFacts
{
    /// <summary>Larger attachments are not copied for the scan or for reading.</summary>
    public const long MaxCopyBytes = 30L * 1024 * 1024;

    public AttachmentFacts(string name, long size = 0)
    {
        Name = name ?? string.Empty;
        Size = size;
    }

    public string Name { get; }
    public long Size { get; }
    /// <summary>Type from the first bytes (FileSignature), null when unknown or not read.</summary>
    public string? DetectedType { get; set; }
    public AttachmentScanStatus ScanStatus { get; set; }
    public string? ScanDetail { get; set; }
    /// <summary>VBA macros found by the extractor helper.</summary>
    public bool HasMacros { get; set; }
    /// <summary>Static reading of the copy (extractor security mode); null when it did not run.</summary>
    public AttachmentInspection? Inspection { get; set; }
    /// <summary>Why the static reading could not run or complete.</summary>
    public string? InspectionDetail { get; set; }
    /// <summary>Image embedded in the HTML body (logo, signature), not a document.</summary>
    public bool IsInline { get; set; }
    /// <summary>daticert.xml: delivery data the PEC provider adds to every message, not a document to read.</summary>
    public bool IsPecReceiptData => Name.Equals("daticert.xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>Name for the temp copy: no path characters, no right-to-left override, the last 100 characters (the extension stays).</summary>
    public static string SafeFileName(string name)
    {
        var chars = name.ToCharArray();
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == '‮') chars[i] = '_';
        }
        var safe = new string(chars).Trim();
        return safe.Length > 100 ? safe.Substring(safe.Length - 100) : safe;
    }

    public string Extension
    {
        get
        {
            int dot = Name.LastIndexOf('.');
            return dot >= 0 && dot < Name.Length - 1 ? Name.Substring(dot + 1).Trim().ToLowerInvariant() : string.Empty;
        }
    }
}

/// <summary>Magic-number detection of common attachment formats.</summary>
public static class FileSignature
{
    public const int HeaderLength = 16;

    private static readonly (string Type, byte[] Magic)[] Signatures =
    {
        ("exe", new byte[] { 0x4D, 0x5A }),                                    // MZ
        ("pdf", Encoding.ASCII.GetBytes("%PDF")),
        ("zip", new byte[] { 0x50, 0x4B, 0x03, 0x04 }),                        // also OOXML, ODF, JAR
        ("zip", new byte[] { 0x50, 0x4B, 0x05, 0x06 }),                        // empty zip
        ("ole", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }), // doc/xls/ppt/msg/msi
        ("rar", Encoding.ASCII.GetBytes("Rar!")),
        ("7z", new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }),
        ("gzip", new byte[] { 0x1F, 0x8B }),
        ("png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
        ("jpeg", new byte[] { 0xFF, 0xD8, 0xFF }),
        ("gif", Encoding.ASCII.GetBytes("GIF8")),
        ("rtf", Encoding.ASCII.GetBytes("{\\rtf")),
        ("cab", Encoding.ASCII.GetBytes("MSCF"))
    };

    private static readonly Dictionary<string, string> ExpectedByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = "pdf",
        ["docx"] = "zip", ["xlsx"] = "zip", ["pptx"] = "zip", ["docm"] = "zip", ["xlsm"] = "zip", ["pptm"] = "zip",
        ["odt"] = "zip", ["ods"] = "zip", ["odp"] = "zip", ["zip"] = "zip", ["jar"] = "zip",
        ["doc"] = "ole", ["xls"] = "ole", ["ppt"] = "ole", ["msg"] = "ole", ["msi"] = "ole",
        ["rar"] = "rar", ["7z"] = "7z", ["gz"] = "gzip", ["tgz"] = "gzip", ["cab"] = "cab",
        ["png"] = "png", ["jpg"] = "jpeg", ["jpeg"] = "jpeg", ["gif"] = "gif", ["rtf"] = "rtf",
        ["exe"] = "exe", ["dll"] = "exe", ["scr"] = "exe", ["cpl"] = "exe", ["sys"] = "exe", ["ocx"] = "exe"
    };

    public static string? Detect(byte[] header, int length)
    {
        foreach (var (type, magic) in Signatures)
        {
            if (length < magic.Length) continue;
            bool match = true;
            for (int i = 0; i < magic.Length && match; i++) match = header[i] == magic[i];
            if (match) return type;
        }
        return null;
    }

    /// <summary>Format the extension promises, null when the extension has no fixed signature (txt, csv, html...).</summary>
    public static string? ExpectedFor(string extension) =>
        ExpectedByExtension.TryGetValue(extension, out var type) ? type : null;
}
