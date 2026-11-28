using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace OnlyWorkspace.ConfigTool.Services;

public enum AddinState { Ready, NotRegistered, DisabledByUser, DisabledByOutlook }

/// <summary>Registry values that decide whether classic Outlook loads the add-in for the current user.</summary>
public sealed class AddinSnapshot
{
    /// <summary>HKLM LoadBehavior in the 64-bit and 32-bit views (null when the key is absent).</summary>
    public int? MachineLoadBehavior64 { get; set; }
    public int? MachineLoadBehavior32 { get; set; }
    /// <summary>HKCU LoadBehavior: it takes precedence over the machine value for this user.</summary>
    public int? UserLoadBehavior { get; set; }
    /// <summary>Outlook put the add-in in its Resiliency\DisabledItems list after a crash or a slow start.</summary>
    public bool InDisabledItems { get; set; }
}

/// <summary>Reads and repairs the per-user loading state of the add-in (Office 2016 and later share the 16.0 key).</summary>
public static class AddinRegistration
{
    public const string ProgId = "OnlyWorkspace.Addin";
    private const string AddinKey = @"Software\Microsoft\Office\Outlook\Addins\" + ProgId;
    private const string ResiliencyKey = @"Software\Microsoft\Office\16.0\Outlook\Resiliency";

    public static (AddinState State, string Message) Evaluate(AddinSnapshot s)
    {
        if (s.MachineLoadBehavior64 == null && s.MachineLoadBehavior32 == null && s.UserLoadBehavior == null)
            return (AddinState.NotRegistered, "Add-in non registrato su questo PC: reinstallare OnlyWorkspace.");
        if (s.InDisabledItems)
            return (AddinState.DisabledByOutlook, "Outlook ha disattivato l'add-in dopo un errore o un avvio lento. «Ripara» lo riattiva per questo utente.");
        int effective = s.UserLoadBehavior ?? s.MachineLoadBehavior64 ?? s.MachineLoadBehavior32 ?? 0;
        if (effective != 3)
            return (AddinState.DisabledByUser, $"L'add-in non si carica all'avvio di Outlook (LoadBehavior {effective}). «Ripara» lo riattiva per questo utente.");
        return (AddinState.Ready, "Add-in registrato e attivo: Outlook lo carica all'avvio.");
    }

    /// <summary>DisabledItems values are binary blobs holding the add-in path or ProgID in UTF-16.</summary>
    public static bool MentionsAddin(byte[]? blob) =>
        blob != null && blob.Length > 1 && Encoding.Unicode.GetString(blob).IndexOf("onlyworkspace", StringComparison.OrdinalIgnoreCase) >= 0;

    public static AddinSnapshot Read() => new()
    {
        MachineLoadBehavior64 = ReadLoadBehavior(RegistryHive.LocalMachine, RegistryView.Registry64),
        MachineLoadBehavior32 = ReadLoadBehavior(RegistryHive.LocalMachine, RegistryView.Registry32),
        UserLoadBehavior = ReadLoadBehavior(RegistryHive.CurrentUser, RegistryView.Default),
        InDisabledItems = DisabledItemNames().Count > 0
    };

    /// <summary>Per-user repair: load at startup, remove Outlook's disable entry, ask Outlook not to disable it again.</summary>
    public static void Repair()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(AddinKey))
            key!.SetValue("LoadBehavior", 3, RegistryValueKind.DWord);
        using (var disabled = Registry.CurrentUser.OpenSubKey(ResiliencyKey + @"\DisabledItems", writable: true))
            if (disabled != null)
                foreach (var name in DisabledItemNames()) disabled.DeleteValue(name, throwOnMissingValue: false);
        using (var keep = Registry.CurrentUser.CreateSubKey(ResiliencyKey + @"\DoNotDisableAddinList"))
            keep!.SetValue(ProgId, 1, RegistryValueKind.DWord);
    }

    private static int? ReadLoadBehavior(RegistryHive hive, RegistryView view)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(AddinKey);
            return key?.GetValue("LoadBehavior") is int value ? value : key == null ? null : 0;
        }
        catch (Exception) { return null; }
    }

    private static List<string> DisabledItemNames()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ResiliencyKey + @"\DisabledItems");
            return key == null ? new List<string>() : key.GetValueNames().Where(n => MentionsAddin(key.GetValue(n) as byte[])).ToList();
        }
        catch (Exception) { return new List<string>(); }
    }
}
