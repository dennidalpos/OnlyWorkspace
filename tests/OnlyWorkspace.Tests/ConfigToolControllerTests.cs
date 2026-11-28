using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Tests;

/// <summary>#093: controllers extracted from MainConfigForm run without a form.</summary>
public sealed class ConfigToolControllerTests
{
    private static BackupRestoreFlow Flow(bool canWrite, List<string> log, Func<string, string, Task<ElevatedConfigWriter.ElevationResult>>? elevated = null) =>
        new("C:\\cfg", _ => canWrite, (backup, dir) => log.Add($"direct {backup} {dir}"), () => { log.Add("stage"); return "C:\\stg"; },
            dir => log.Add("unstage " + dir),
            elevated ?? ((arguments, staging) => { log.Add("elevated " + arguments); return Task.FromResult(new ElevatedConfigWriter.ElevationResult { Success = true }); }));

    [Fact]
    public async Task RestoreWritesDirectlyWhenTheUserMayWriteAndNeverStagesOrElevates()
    {
        var log = new List<string>();
        var result = await Flow(true, log).RestoreAsync("C:\\b.owbak");
        Assert.Equal(RestoreOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "direct C:\\b.owbak C:\\cfg" }, log);
    }

    [Theory]
    [InlineData(true, false, RestoreOutcome.Completed)]
    [InlineData(false, true, RestoreOutcome.Cancelled)]
    [InlineData(false, false, RestoreOutcome.Failed)]
    public async Task ElevatedRestoreMapsTheResultAndAlwaysRemovesTheStagingFolder(bool success, bool cancelled, RestoreOutcome expected)
    {
        var log = new List<string>();
        var result = await Flow(false, log, (_, _) => Task.FromResult(
            new ElevatedConfigWriter.ElevationResult { Success = success, Cancelled = cancelled, Message = "reason" })).RestoreAsync("C:\\b.owbak");
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(success ? string.Empty : "reason", result.Message);
        Assert.Equal(new[] { "stage", "unstage C:\\stg" }, log);
    }

    [Fact]
    public async Task ElevatedRestoreUsesTheQuotedRestoreAuthorizedCommandAndRemovesStagingWhenTheRunThrows()
    {
        var log = new List<string>();
        string? arguments = null, staged = null;
        var flow = Flow(false, log, (a, s) => { arguments = a; staged = s; throw new InvalidOperationException("boom"); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.RestoreAsync("C:\\my backup\\b.owbak"));
        Assert.Equal("restore-authorized \"C:\\my backup\\b.owbak\" \"C:\\cfg\" \"C:\\stg\"", arguments);
        Assert.Equal("C:\\stg", staged);
        Assert.Equal("unstage C:\\stg", log[^1]);
    }

    [Theory]
    [InlineData(true, false, true, false, CloseReason.UserClosing, CloseAction.Block)]
    [InlineData(true, true, true, false, CloseReason.UserClosing, CloseAction.Block)]
    [InlineData(false, true, true, false, CloseReason.UserClosing, CloseAction.AskUnsaved)]
    [InlineData(false, true, false, false, CloseReason.WindowsShutDown, CloseAction.AskUnsaved)]
    [InlineData(false, false, true, false, CloseReason.UserClosing, CloseAction.HideToTray)]
    [InlineData(false, false, true, true, CloseReason.UserClosing, CloseAction.Close)]
    [InlineData(false, false, true, false, CloseReason.WindowsShutDown, CloseAction.Close)]
    [InlineData(false, false, false, false, CloseReason.UserClosing, CloseAction.Close)]
    public void ClosePolicyDecidesFromTheStateAlone(bool busy, bool dirty, bool tray, bool exiting, CloseReason reason, CloseAction expected) =>
        Assert.Equal(expected, ClosePolicy.Decide(busy, dirty, tray, exiting, reason));

    private static (ProfileEditor Editor, WorkspaceConfig Config) Editor(params string[] ids)
    {
        var config = new WorkspaceConfig { DefaultProfile = ids[0] };
        config.Profiles = ids.Select(id => new LlmProfileConfig { Id = id }).ToList();
        return (new ProfileEditor(() => config, new Dictionary<string, string>(), new Dictionary<string, string>()), config);
    }

    [Fact]
    public void AddUsesTheFirstFreeProfileId()
    {
        var (editor, config) = Editor("profile-2", "a");
        var added = editor.Add();
        Assert.Equal("profile-3", added.Id);
        Assert.Contains(added, config.Profiles);
        Assert.Equal("Nuovo Profilo (profile-3)", added.DisplayName);
    }

    [Fact]
    public void RenameMovesKeysAndDefaultAndDropsVaultReferences()
    {
        var (editor, config) = Editor("a", "b");
        var profile = config.Profiles[0];
        profile.SecretRef = "vault:a";
        profile.WebSearchSecretRef = "vault:web-a";
        editor.Secrets["a"] = "k";
        editor.WebSecrets["a"] = "w";

        Assert.Equal(RenameOutcome.Renamed, editor.Rename(profile, " c "));

        Assert.Equal("c", profile.Id);
        Assert.Equal("c", config.DefaultProfile);
        Assert.Equal(string.Empty, profile.SecretRef);
        Assert.Equal(string.Empty, profile.WebSearchSecretRef);
        Assert.Equal("k", editor.SecretFor("c"));
        Assert.Equal("w", editor.WebSecretFor("c"));
        Assert.Null(editor.SecretFor("a"));
        Assert.Null(editor.WebSecretFor("a"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("B")]
    public void RenameRejectsEmptyOrDuplicateIdsAndChangesNothing(string newId)
    {
        var (editor, config) = Editor("a", "b");
        editor.Secrets["a"] = "k";
        Assert.Equal(RenameOutcome.Invalid, editor.Rename(config.Profiles[0], newId));
        Assert.Equal("a", config.Profiles[0].Id);
        Assert.Equal("a", config.DefaultProfile);
        Assert.Equal("k", editor.SecretFor("a"));
    }

    [Fact]
    public void RenameToTheSameIdIsUnchanged()
    {
        var (editor, config) = Editor("a");
        Assert.Equal(RenameOutcome.Unchanged, editor.Rename(config.Profiles[0], " a"));
    }

    [Fact]
    public void RemoveDropsKeysAndMovesTheDefaultToTheFirstProfileLeft()
    {
        var (editor, config) = Editor("a", "b", "c");
        editor.Secrets["a"] = "k";
        editor.WebSecrets["a"] = "w";

        Assert.Equal(RemoveOutcome.Removed, editor.Remove(config.Profiles[0]));

        Assert.Equal(new[] { "b", "c" }, config.Profiles.Select(p => p.Id));
        Assert.Equal("b", config.DefaultProfile);
        Assert.Empty(editor.Secrets);
        Assert.Empty(editor.WebSecrets);
    }

    [Fact]
    public void RemoveKeepsTheLastProfileAndTheDefaultWhenAnotherIsRemoved()
    {
        var (editor, config) = Editor("a", "b");
        Assert.Equal(RemoveOutcome.Removed, editor.Remove(config.Profiles[1]));
        Assert.Equal("a", config.DefaultProfile);
        Assert.Equal(RemoveOutcome.LastProfile, editor.Remove(config.Profiles[0]));
        Assert.Single(config.Profiles);
    }

    [Fact]
    public void BlankKeysCountAsAbsentAndTheConfigurationIsReadAtEachCall()
    {
        var config = new WorkspaceConfig();
        var editor = new ProfileEditor(() => config, new Dictionary<string, string> { ["a"] = " " }, new Dictionary<string, string>());
        Assert.Null(editor.SecretFor("a"));

        config = new WorkspaceConfig { DefaultProfile = "x" };
        Assert.True(editor.IsDefault(new LlmProfileConfig { Id = "X" }));
    }

    [Theory]
    [InlineData(true, true, "La configurazione attuale non è leggibile: risolvi prima il problema indicato.")]
    [InlineData(true, false, "La configurazione attuale non è leggibile: risolvi prima il problema indicato.")]
    [InlineData(false, true, "Sblocca la configurazione con la password amministrativa per usare la procedura guidata.")]
    [InlineData(false, false, null)]
    public void WizardIsBlockedByAnUnreadableConfigurationFirstThenByTheLock(bool loadBlocked, bool locked, string? expected) =>
        Assert.Equal(expected, SetupWizardApplier.BlockedReason(loadBlocked, locked));

    [Fact]
    public void WizardStartsFromTheDefaultProfileElseTheFirst()
    {
        var (editor, config) = Editor("a", "b");
        config.DefaultProfile = "b";
        Assert.Same(config.Profiles[1], SetupWizardApplier.StartingProfile(config, editor));
        config.DefaultProfile = "missing";
        Assert.Same(config.Profiles[0], SetupWizardApplier.StartingProfile(config, editor));
    }

    [Fact]
    public void WizardResultReplacesTheProfileSetsTheDefaultKeyAndArchiveOptions()
    {
        var (editor, config) = Editor("a", "b");
        var starting = config.Profiles[0];
        editor.Secrets["a"] = "old";
        var chosen = new LlmProfileConfig { Id = "cloud" };

        SetupWizardApplier.Apply(config, editor.Secrets, starting, new SetupWizardResult
        {
            Profile = chosen,
            Secret = "new",
            ArchiveEnabled = true,
            IndexAttachments = false
        });

        Assert.Same(chosen, config.Profiles[0]);
        Assert.Equal(2, config.Profiles.Count);
        Assert.Equal("cloud", config.DefaultProfile);
        Assert.Equal("new", editor.SecretFor("cloud"));
        Assert.True(config.Archive.Enabled);
        Assert.False(config.Archive.IndexAttachmentText);
    }

    [Fact]
    public void WizardResultWithoutAKeyRemovesAnyKeyTypedUnderTheSameId()
    {
        var (editor, config) = Editor("a");
        editor.Secrets["a"] = "old";
        SetupWizardApplier.Apply(config, editor.Secrets, config.Profiles[0], new SetupWizardResult { Profile = new LlmProfileConfig { Id = "a" } });
        Assert.Null(editor.SecretFor("a"));
    }
}
