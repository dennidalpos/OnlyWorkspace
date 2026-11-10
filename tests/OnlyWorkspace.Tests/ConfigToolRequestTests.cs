using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.Tests;

[Collection(WinFormsCollection.Name)]
public sealed class ConfigToolRequestTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Flags)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Flags)!.SetValue(owner, value);
    /// <summary>The control's own Visible flag; the property also needs every parent shown, which a form never shown is not.</summary>
    private static bool SelfVisible(Control control)
    {
        var getState = typeof(Control).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Single(m => m.Name == "GetState" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsPrimitive | m.GetParameters()[0].ParameterType.IsEnum);
        var type = getState.GetParameters()[0].ParameterType;
        return (bool)getState.Invoke(control, new[] { type.IsEnum ? Enum.ToObject(type, 0x2) : 0x2 })!;
    }

    private static object? Call(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, Flags)!.Invoke(owner, args);
    private static void Pump(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(1); }
        Assert.True(done(), "UI operation did not finish");
    }
    private static void Sta(Action<string> action)
    {
        Exception? error = null;
        string dir = Path.Combine(Path.GetTempPath(), "ow-request-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var thread = new Thread(() => { try { SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext()); action(dir); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(40)));
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static LlmProfileConfig Profile() => new() { Id = "fixture", DisplayName = "Fixture", Type = "ollama", Model = "synthetic", BaseUrl = "http://127.0.0.1:1" };

    [Fact]
    public void HealthCheckCanBeCancelledAndDisposesTheClient()
    {
        Sta(dir =>
        {
            new FileConfigurationService(dir, dir).SaveConfiguration(new WorkspaceConfig { DefaultProfile = "fixture", Profiles = new() { Profile() } });
            using var form = new MainConfigForm(dir, null, dir); form.CreateControl(); _ = form.Handle;
            using var client = new SlowClient(); form.ConnectionClientFactory = (_, _) => client;
            Call(form, "BtnTestProfile_Click", null, EventArgs.Empty);
            Assert.True(client.Token.CanBeCanceled);
            Assert.Equal("Annulla verifica", Field<Button>(form, "_btnTestProfile").Text);
            Call(form, "BtnTestProfile_Click", null, EventArgs.Empty);
            Pump(() => client.Disposed && Field<InteractiveRequest?>(form, "_connectionCheck") == null);
            Assert.True(client.Token.IsCancellationRequested);
            Assert.Contains("annullata", Field<Label>(form, "_lblTestResult").Text);
        });
    }

    [Theory]
    [InlineData("close")]
    [InlineData("endpoint")]
    [InlineData("provider")]
    public void ClosingOrChangingProfileCancelsAndRejectsALateHealthyResult(string action)
    {
        Sta(dir =>
        {
            new FileConfigurationService(dir, dir).SaveConfiguration(new WorkspaceConfig { DefaultProfile = "fixture", Profiles = new() { Profile() } });
            using var form = new MainConfigForm(dir, null, dir); form.CreateControl(); _ = form.Handle;
            using var client = new SlowClient { IgnoreCancellation = true }; form.ConnectionClientFactory = (_, _) => client;
            Call(form, "BtnTestProfile_Click", null, EventArgs.Empty);
            var label = Field<Label>(form, "_lblTestResult");
            if (action == "close") form.Dispose();
            else if (action == "endpoint") Field<TextBox>(form, "_txtEndpoint").Text = "http://127.0.0.1:2";
            else { var providers = Field<ComboBox>(form, "_cmbProvider"); providers.SelectedIndex = (providers.SelectedIndex + 1) % providers.Items.Count; }
            Assert.True(client.Token.IsCancellationRequested);
            client.Late.SetResult(LlmHealthResult.Success("late", Array.Empty<string>()));
            Pump(() => client.Disposed && Field<InteractiveRequest?>(form, "_connectionCheck") == null);
            if (action != "close") Assert.DoesNotContain("OK", label.Text);
        });
    }

    [Fact]
    public void CapabilitiesCannotReenterAndUseTheCancelButton()
    {
        Sta(dir =>
        {
            new FileConfigurationService(dir, dir).SaveConfiguration(new WorkspaceConfig { DefaultProfile = "fixture", Profiles = new() { Profile() } });
            using var form = new MainConfigForm(dir, null, dir); form.CreateControl(); _ = form.Handle;
            using var client = new SlowClient(); form.ConnectionClientFactory = (_, _) => client;
            var task = (Task)Call(form, "RefreshDeclaredCapabilitiesAsync")!;
            Assert.False(task.IsCompleted);
            var again = (Task)Call(form, "RefreshDeclaredCapabilitiesAsync")!; Assert.True(again.IsCompleted);
            Assert.Equal(1, client.Calls);
            // PerformClick does nothing on a button of a tab that was never shown: raise the Click handlers directly,
            // otherwise only the 10 second deadline ends the run and the test measures that instead of the button.
            var cancel = Field<Button>(form, "_btnProbeCancel");
            Assert.True(cancel.Enabled);
            typeof(Button).GetMethod("OnClick", Flags)!.Invoke(cancel, new object[] { EventArgs.Empty });
            Pump(() => task.IsCompleted); task.GetAwaiter().GetResult();
            Assert.Contains("annullato", Field<Label>(form, "_lblProbeStatus").Text);
            Assert.True(client.Disposed); Assert.True(client.Token.IsCancellationRequested);
            Assert.False(Field<Button>(form, "_btnProbeCancel").Enabled);
        });
    }

    [Fact]
    public void WizardCancellationAndClosingReleaseThePendingClient()
    {
        Sta(dir =>
        {
            using var form = new SetupWizardForm(Profile(), null, true, true); form.CreateControl(); _ = form.Handle;
            using var client = new SlowClient(); form.ClientFactory = (_, _) => client;
            using var button = new Button { Text = "Verifica" };
            var task = (Task)Call(form, "TestAsync", button)!;
            Assert.False(task.IsCompleted);
            form.Dispose(); Pump(() => task.IsCompleted); task.GetAwaiter().GetResult();
            Assert.True(client.Token.IsCancellationRequested); Assert.True(client.Disposed);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WizardPointsAtCloudPresetsOnlyWithoutALocalAi(bool localAi)
    {
        Sta(dir =>
        {
            using var form = new SetupWizardForm(Profile(), null, true, true);
            form.LocalAiProbe = _ => Task.FromResult(localAi);
            var task = (Task)Call(form, "ProbeLocalAiAsync")!; Pump(() => task.IsCompleted); task.GetAwaiter().GetResult();
            var label = Field<Label>(form, "_lblLocalAi");
            Assert.Equal(!localAi, SelfVisible(label));
            if (localAi) Assert.Empty(label.Text);
            else { Assert.Contains("Nessuna IA locale", label.Text); Assert.Contains("Nulla viene inviato prima dello sblocco", label.Text); }
        });
    }

    [Fact]
    public void WizardCloudPresetFillsTheAddressAndMarksTheProfileExternal()
    {
        Sta(dir =>
        {
            using var form = new SetupWizardForm(Profile(), null, true, true);
            var provider = Field<ComboBox>(form, "_cmbProvider");
            var preset = Field<ComboBox>(form, "_cmbPreset");
            Assert.False(SelfVisible(Field<Control>(form, "_presetRow")));
            provider.SelectedItem = ProviderCatalog.Get(ProviderCatalog.OpenAICompatible);
            Assert.True(SelfVisible(Field<Control>(form, "_presetRow")));
            var scaleway = CloudProviderPresets.All.Single(p => p.Id == "scaleway");
            preset.SelectedItem = scaleway;
            Assert.Equal(scaleway.BaseUrl, Field<TextBox>(form, "_txtEndpoint").Text);
            var edited = (LlmProfileConfig)Call(form, "Edited")!;
            Assert.Equal(ProviderCatalog.OpenAICompatible, edited.Type);
            Assert.Equal(scaleway.BaseUrl, edited.BaseUrl);
            Assert.True(edited.External);
            provider.SelectedItem = ProviderCatalog.Get(ProviderCatalog.Ollama);
            Assert.False(SelfVisible(Field<Control>(form, "_presetRow")));
            Assert.IsNotType<CloudProviderPreset>(preset.SelectedItem);
        });
    }

    [Fact]
    public void WizardKeepsTheMissingModelFeedbackAfterAHealthyConnection()
    {
        Sta(dir =>
        {
            using var form = new SetupWizardForm(Profile(), null, true, true);
            using var client = new SlowClient { IgnoreCancellation = true };
            client.Late.SetResult(LlmHealthResult.Success("fixture", new[] { "another-model" }));
            form.ClientFactory = (_, _) => client;
            using var button = new Button { Text = "Verifica" };
            var task = (Task)Call(form, "TestAsync", button)!; Pump(() => task.IsCompleted); task.GetAwaiter().GetResult();
            Assert.Contains("non è tra", Field<Label>(form, "_lblTest").Text);
            Assert.True(client.Disposed);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WizardDistinguishesAnUnavailableModelFromAnUnreachableService(bool missingModel)
    {
        Sta(dir =>
        {
            using var form = new SetupWizardForm(Profile(), null, true, true);
            using var client = new SlowClient { IgnoreCancellation = true };
            client.Late.SetResult(missingModel
                ? LlmHealthResult.ModelMissing("fixture", new[] { "another-model" }, "Servizio raggiungibile, modello assente.")
                : LlmHealthResult.Failure("Servizio non raggiungibile: controlla l'indirizzo."));
            form.ClientFactory = (_, _) => client;
            using var button = new Button { Text = "Verifica" };
            var task = (Task)Call(form, "TestAsync", button)!; Pump(() => task.IsCompleted); task.GetAwaiter().GetResult();
            string text = Field<Label>(form, "_lblTest").Text;
            Assert.StartsWith(missingModel ? "Connessione riuscita, ma il modello" : "Connessione non riuscita:", text);
            Assert.DoesNotContain(missingModel ? "Connessione non riuscita" : "Connessione riuscita", text);
            Assert.True(client.Disposed);
        });
    }

    [Theory]
    [InlineData("ollama", "synthetic", "synthetic:latest")]
    [InlineData("gemini", "models/synthetic", "synthetic")]
    public void WizardAcceptsTheProvidersModelAlias(string provider, string configured, string listed)
    {
        Sta(dir =>
        {
            var profile = Profile(); profile.Type = provider; profile.Model = configured;
            using var form = new SetupWizardForm(profile, null, true, true);
            using var client = new SlowClient { IgnoreCancellation = true };
            client.Late.SetResult(LlmHealthResult.Success("fixture", new[] { listed }));
            form.ClientFactory = (_, _) => client;
            using var button = new Button { Text = "Verifica" };
            var task = (Task)Call(form, "TestAsync", button)!; Pump(() => task.IsCompleted); task.GetAwaiter().GetResult();
            string text = Field<Label>(form, "_lblTest").Text;
            Assert.StartsWith("Connessione riuscita (", text);
            Assert.DoesNotContain("non è tra", text);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedMailboxSaveRereadsAuthoritativeStateOrMarksItUnknown(bool unreadable, bool corrupt)
    {
        Sta(dir =>
        {
            var config = new FileConfigurationService(dir, dir); using var tips = new ToolTip();
            using var panel = new MailboxAuthorizationPanel(config, tips, CancellationToken.None);
            Set(panel, "_building", true);
            var profiles = Field<ComboBox>(panel, "_profiles"); profiles.Items.Add("synthetic"); profiles.SelectedIndex = 0;
            Field<HashSet<string>>(panel, "_archived").Add("synthetic");
            Field<CheckBox>(panel, "_mine").Checked = true;
            Set(panel, "_building", false);
            Call(panel, "BuildGrid", new List<ArchiveOpenStore> { new() { Id = "a", Name = "A", Open = true }, new() { Id = "b", Name = "B", Open = true }, new() { Id = "closed", Name = "Chiusa", Open = false } }, new List<string> { "a" }, StoreOwnAddresses.Parse(null), Array.Empty<string>());
            var rows = Field<Dictionary<string, CheckBox>>(panel, "_rows");
            var completion = new TaskCompletionSource<bool>();
            int writes = 0;
            panel.WriteStores = async (_, _, _) => { writes++; await completion.Task; throw new IOException("synthetic"); };
            panel.ReadStores = (_, _) => corrupt ? Task.FromResult<string?>("[42]")
                : unreadable ? Task.FromException<string?>(new IOException("synthetic read")) : Task.FromResult<string?>("[\"a\"]");
            Set(panel, "_building", true); rows["b"].Checked = true; Set(panel, "_building", false);
            var save = (Task)Call(panel, "ApplyAsync")!;
            Assert.False(Field<Control>(panel, "_grid").Enabled);
            ((Task)Call(panel, "ApplyAsync")!).GetAwaiter().GetResult(); Assert.Equal(1, writes);
            completion.SetResult(true); Pump(() => save.IsCompleted); save.GetAwaiter().GetResult();
            if (unreadable) { Assert.All(rows.Values, b => Assert.Equal(CheckState.Indeterminate, b.CheckState)); Assert.False(Field<Control>(panel, "_grid").Enabled); }
            else { Assert.True(rows["a"].Checked); Assert.False(rows["b"].Checked); Assert.True(Field<Control>(panel, "_grid").Enabled); }
            Assert.False(string.IsNullOrEmpty(tips.GetToolTip(rows["closed"])));
        });
    }

    [Fact]
    public async Task SlowLoopbackHttpIsCancelledByTheCommonDeadline()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            using var request = new InteractiveRequest(TimeSpan.FromMilliseconds(250));
            using var client = new HttpClient();
            var pending = client.GetAsync("http://127.0.0.1:" + ((System.Net.IPEndPoint)listener.LocalEndpoint).Port, request.Token);
            using var peer = await accept;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            Assert.True(request.TimedOut); Assert.Equal(TimeSpan.FromSeconds(10), InteractiveRequest.Timeout);
        }
        finally { listener.Stop(); }
    }

    private sealed class SlowClient : ILlmClient, IDisposable
    {
        public CancellationToken Token; public bool Disposed; public int Calls;
        public bool IgnoreCancellation; public readonly TaskCompletionSource<LlmHealthResult> Late = new();
        private async Task Wait(CancellationToken token) { Token = token; Calls++; await Task.Delay(Timeout.Infinite, token); }
        public async Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) { if (IgnoreCancellation) { Token = cancellationToken; Calls++; return await Late.Task; } await Wait(cancellationToken); return LlmHealthResult.Success("fixture", Array.Empty<string>()); }
        public async Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default) { await Wait(cancellationToken); return null; }
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<string> StreamChatAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CompleteStructuredAsync(IEnumerable<ChatMessage> messages, string jsonSchema, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public LlmTokenUsage? LastTokenUsage => null; public LlmCompletion? LastCompletion => null;
        public void Dispose() => Disposed = true;
    }
}
