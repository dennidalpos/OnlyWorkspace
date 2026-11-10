using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace OnlyWorkspace.ConfigTool.Services;

/// <summary>Outlook profiles of the current Windows user, from the registry (read only); the default one first.</summary>
internal sealed class OutlookProfiles
{
    /// <summary>Outlook 2016 and later (Microsoft 365 included) keep their settings under version 16.0.</summary>
    internal const string OutlookKey = @"Software\Microsoft\Office\16.0\Outlook";

    public IReadOnlyList<string> Names { get; }
    /// <summary>The profile Outlook opens: <c>DefaultProfile</c>, or the only one; null when Outlook asks which to use or has none.</summary>
    public string? Default { get; }

    public OutlookProfiles(IReadOnlyList<string> names, string? @default)
    {
        Names = names;
        Default = @default;
    }

    public static OutlookProfiles Read(string key = OutlookKey)
    {
        try
        {
            using var outlook = Registry.CurrentUser.OpenSubKey(key);
            using var profiles = outlook?.OpenSubKey("Profiles");
            var names = profiles?.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            var named = outlook?.GetValue("DefaultProfile") as string;
            string? chosen = names.FirstOrDefault(n => string.Equals(n, named, StringComparison.OrdinalIgnoreCase)) ?? (names.Count == 1 ? names[0] : null);
            if (chosen != null)
            {
                names.Remove(chosen);
                names.Insert(0, chosen);
            }
            return new OutlookProfiles(names, chosen);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return new OutlookProfiles(Array.Empty<string>(), null);
        }
    }

    /// <summary>The line of the setup wizard: the profile the archive will follow, or why there is none yet.</summary>
    public string Describe() =>
        Default != null ? $"Profilo Outlook: {Default}." + (Names.Count > 1 ? $" Altri profili: {string.Join(", ", Names.Skip(1))}." : string.Empty)
        : Names.Count > 0 ? $"Profili Outlook: {string.Join(", ", Names)}. Outlook chiede quale usare all'avvio."
        : NotConfigured;

    public const string NotConfigured = "Outlook non è ancora stato configurato per questo utente: aprilo una volta e aggiungi l'account di posta.";
}
