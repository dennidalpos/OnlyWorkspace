using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Tests;

/// <summary>#177: the data-egress notice flow outside MainConfigForm, with scripted answers and no dialogs.</summary>
public sealed class DataEgressFlowTests
{
    private sealed class Host : IDataEgressHost
    {
        public WorkspaceConfig Config { get; set; } = new();
        public string WindowsUser => "PC\\user";
        public string Machine => "PC";
        public bool IsBusy { get; set; }
        public bool IsLockedOrBlocked { get; set; }
        public bool IsDirty { get; set; }
        public IReadOnlyList<string>? Notice = new[] { "qwen" };
        public bool Answer = true;
        public bool SaveResult = true;
        public int Prepared, Marked, Refreshed, NoticesShown;
        public List<string> Confirmations = new(), Log = new();
        public string? ResolveSecret(string reference) => "secret";
        public void PrepareProfiles() => Prepared++;
        public IReadOnlyList<string>? ShowNotice(IReadOnlyList<LlmProfileConfig> profiles) { NoticesShown++; return Notice; }
        public bool Confirm(string message, string title, string? yes = null, string? no = null) { Confirmations.Add(title); return Answer; }
        public Task<bool> SaveAsync() => Task.FromResult(SaveResult);
        public void MarkDirty() { Marked++; IsDirty = true; }
        public void RestoreDirty(bool dirty) => IsDirty = dirty;
        public void RefreshStatus() => Refreshed++;
        public void LogActivity(string message) => Log.Add(message);
    }

    private static Host CloudHost() => new()
    {
        Config = new WorkspaceConfig
        {
            Profiles = new List<LlmProfileConfig>
            {
                new()
                {
                    Id = "qwen", DisplayName = "Qwen", Type = "qwen", BaseUrl = "https://cloud.example/v1",
                    Model = "qwen-flash", External = true, Exposure = ExposureLevel.Standard
                }
            }
        }
    };

    [Fact]
    public void Ask_WhenBusy_ShowsNothing()
    {
        var host = CloudHost();
        host.IsBusy = true;
        Assert.False(new DataEgressFlow(host).Ask());
        Assert.Equal(0, host.NoticesShown);
        Assert.Equal(0, host.Prepared);
    }

    [Fact]
    public void Ask_WithoutCloudProfiles_ShowsNothing()
    {
        var host = new Host();
        Assert.False(new DataEgressFlow(host).Ask());
        Assert.Equal(0, host.NoticesShown);
    }

    [Fact]
    public void Ask_Declined_LeavesTheConfigurationAlone()
    {
        var host = CloudHost();
        host.Notice = null;
        Assert.False(new DataEgressFlow(host).Ask());
        Assert.Null(host.Config.DataEgress?.Consent);
        Assert.Equal(0, host.Marked);
    }

    [Fact]
    public void Ask_Accepted_RecordsConsentAndMarksDirty()
    {
        var host = CloudHost();
        Assert.True(new DataEgressFlow(host).Ask());
        Assert.NotNull(host.Config.DataEgress.Consent);
        Assert.Equal(1, host.Marked);
        Assert.Equal(1, host.Refreshed);
        Assert.Single(host.Log);
    }

    [Fact]
    public async Task AcceptAndSave_WithUnsavedChangesDeclined_StopsBeforeTheNotice()
    {
        var host = CloudHost();
        host.IsDirty = true;
        host.Answer = false;
        Assert.False(await new DataEgressFlow(host).AcceptAndSaveAsync());
        Assert.Equal(new[] { "Modifiche non salvate" }, host.Confirmations);
        Assert.Equal(0, host.NoticesShown);
    }

    [Fact]
    public async Task AcceptAndSave_WhenTheSaveFails_RestoresNoticeAndDirtyFlag()
    {
        var host = CloudHost();
        host.SaveResult = false;
        Assert.False(await new DataEgressFlow(host).AcceptAndSaveAsync());
        Assert.Null(host.Config.DataEgress.Consent);
        Assert.Empty(host.Config.DataEgress.History);
        Assert.False(host.IsDirty);
    }

    [Fact]
    public async Task AcceptAndSave_WhenSaved_KeepsTheConsent()
    {
        var host = CloudHost();
        Assert.True(await new DataEgressFlow(host).AcceptAndSaveAsync());
        Assert.NotNull(host.Config.DataEgress.Consent);
        Assert.Contains("Autorizzazioni per profilo registrate e salvate.", host.Log);
    }

    [Fact]
    public async Task Revoke_WithoutConsent_AsksNothing()
    {
        var host = CloudHost();
        await new DataEgressFlow(host).RevokeAsync();
        Assert.Empty(host.Confirmations);
    }

    [Fact]
    public async Task Revoke_Declined_KeepsTheConsent()
    {
        var host = CloudHost();
        var flow = new DataEgressFlow(host);
        Assert.True(await flow.AcceptAndSaveAsync());
        host.Answer = false;
        await flow.RevokeAsync();
        Assert.NotNull(host.Config.DataEgress.Consent);
    }

    [Fact]
    public async Task Revoke_WhenSaved_ClearsTheConsent()
    {
        var host = CloudHost();
        var flow = new DataEgressFlow(host);
        Assert.True(await flow.AcceptAndSaveAsync());
        await flow.RevokeAsync();
        Assert.Null(host.Config.DataEgress.Consent);
        Assert.Contains("Informativa sull'uscita dei dati revocata.", host.Log);
    }

    [Fact]
    public async Task Revoke_WhenTheSaveFails_RestoresTheConsent()
    {
        var host = CloudHost();
        var flow = new DataEgressFlow(host);
        Assert.True(await flow.AcceptAndSaveAsync());
        var consent = host.Config.DataEgress.Consent;
        int events = host.Config.DataEgress.History.Count;
        host.SaveResult = false;
        Assert.False(await flow.ApplyRevocationAsync());
        Assert.Same(consent, host.Config.DataEgress.Consent);
        Assert.Equal(events, host.Config.DataEgress.History.Count);
    }
}
