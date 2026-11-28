using System.Collections.Generic;
using System.Linq;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.ConfigTool.Services;

/// <summary>What the guided setup returned, with no dependency on the wizard form.</summary>
public sealed class SetupWizardResult
{
    public LlmProfileConfig Profile { get; set; } = new();
    public string? Secret { get; set; }
    public bool ArchiveEnabled { get; set; }
    public bool IndexAttachments { get; set; }
}

/// <summary>Why the guided setup cannot open, null when it can.</summary>
public static class SetupWizardApplier
{
    public static string? BlockedReason(bool loadBlocked, bool locked) =>
        loadBlocked ? "La configurazione attuale non è leggibile: risolvi prima il problema indicato."
        : locked ? "Sblocca la configurazione con la password amministrativa per usare la procedura guidata."
        : null;

    /// <summary>The profile the wizard starts from: the default one, else the first.</summary>
    public static LlmProfileConfig StartingProfile(WorkspaceConfig config, ProfileEditor profiles) =>
        config.Profiles.FirstOrDefault(profiles.IsDefault) ?? config.Profiles[0];

    /// <summary>Replaces the starting profile, makes it the default and applies key and archive choices.</summary>
    public static void Apply(WorkspaceConfig config, Dictionary<string, string> secrets, LlmProfileConfig starting, SetupWizardResult result)
    {
        config.Profiles[config.Profiles.IndexOf(starting)] = result.Profile;
        config.DefaultProfile = result.Profile.Id;
        if (result.Secret != null) secrets[result.Profile.Id] = result.Secret;
        else secrets.Remove(result.Profile.Id);
        config.Archive.Enabled = result.ArchiveEnabled;
        config.Archive.IndexAttachmentText = result.IndexAttachments;
    }
}
