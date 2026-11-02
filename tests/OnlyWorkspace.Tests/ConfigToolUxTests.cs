using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.Tests;

/// <summary>A163: what the ConfigTool shows matches what is saved (revocation, lock, warnings, confirmations).</summary>
[Collection(WinFormsCollection.Name)]
public sealed class ConfigToolUxTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ElevatedSave_PumpsTheUiAndCleansStaging_OnSuccessOrCancellation(bool success)
    {
        OnSta(directory =>
        {
            using var form = Open(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a") } });
            form.CreateControl();
            _ = form.Handle;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var completion = new TaskCompletionSource<OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.ElevationResult>();
            string? staging = null;
            Func<string, string?, Task<OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.ElevationResult>> writer = (arguments, resultDir) =>
            {
                Assert.StartsWith("apply-staged ", arguments);
                staging = resultDir;
                Assert.True(File.Exists(Path.Combine(staging!, "config.json")));
                return completion.Task;
            };
            typeof(MainConfigForm).GetField("_elevatedWriter", Flags)!.SetValue(form, writer);
            typeof(MainConfigForm).GetField("_requiresElevation", Flags)!.SetValue(form, true);
            var task = (Task<bool>)typeof(MainConfigForm).GetMethod("SaveActiveConfiguration", Flags)!.Invoke(form, null)!;
            Assert.False(task.IsCompleted);
            Assert.False(Field<Button>(form, "_btnSave").Enabled);
            Assert.Contains("In attesa", Field<Label>(form, "_lblStatus").Text);
            form.Close(); Assert.False(form.IsDisposed);
            bool painted = false;
            form.BeginInvoke(new Action(() => { painted = true; completion.SetResult(new() { Success = success, Cancelled = !success }); }));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(1); }
            Assert.True(task.IsCompleted); Assert.Equal(success, task.GetAwaiter().GetResult());
            Assert.True(painted); Assert.True(Field<Button>(form, "_btnSave").Enabled);
            Assert.False(Directory.Exists(staging));
            SynchronizationContext.SetSynchronizationContext(null);
        });
    }

    [Fact]
    public void ElevatedWriterFailure_RestoresTheUiWithoutAbandoningTheCaller()
    {
        OnSta(directory =>
        {
            using var form = Open(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a") } });
            Func<string, string?, Task<OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.ElevationResult>> writer = (_, _) =>
                Task.FromException<OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.ElevationResult>(new IOException("synthetic failure"));
            typeof(MainConfigForm).GetField("_elevatedWriter", Flags)!.SetValue(form, writer);
            var task = (Task<OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.ElevationResult>)typeof(MainConfigForm).GetMethod("RunElevatedAsync", Flags)!.Invoke(form, new object?[] { "synthetic", null })!;
            Assert.Throws<IOException>(() => task.GetAwaiter().GetResult());
            Assert.False(Field<bool>(form, "_elevationBusy"));
            Assert.True(Field<Button>(form, "_btnSave").Enabled);
        });
    }

    [Fact]
    public void ItalianLogLabels_SaveTheOriginalValues()
    {
        OnSta(directory =>
        {
            using var form = Open(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a") } });
            var levels = Field<ComboBox>(form, "_cmbLogLevel");
            levels.SelectedIndex = 1;
            Assert.Equal("Diagnostica", levels.SelectedItem!.ToString());
            Assert.True((bool)Call(form, "SaveActiveConfiguration")!);
            Assert.Equal("Debug", Saved(directory).Logging.Level);
        });
    }

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void OnSta(Action<string> action)
    {
        Exception? error = null;
        string directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace-ux-" + Guid.NewGuid());
        var thread = new Thread(() =>
        {
            try { action(directory); }
            catch (Exception ex) { error = ex; }
            finally { if (Directory.Exists(directory)) TestArtifacts.DeleteDirectory(directory, true); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "UI test did not complete.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static LlmProfileConfig Local(string id) => new()
    {
        Id = id, DisplayName = id, Type = "ollama", BaseUrl = "http://127.0.0.1:11434", Model = "synthetic", AuthType = "none"
    };

    private static LlmProfileConfig Cloud(string id) => new()
    {
        Id = id, DisplayName = id, Type = "qwen", BaseUrl = "https://remote.example.test/v1", Model = "synthetic", AuthType = "bearer",
        External = true, Exposure = ExposureLevel.Standard
    };

    private static MainConfigForm Open(string directory, WorkspaceConfig config)
    {
        new FileConfigurationService(directory, directory).SaveConfiguration(config);
        return new MainConfigForm(directory, null, directory);
    }

    private static T Field<T>(MainConfigForm form, string name) => (T)typeof(MainConfigForm).GetField(name, Flags)!.GetValue(form)!;

    private static object? Call(MainConfigForm form, string name, params object[] args)
    {
        var value = typeof(MainConfigForm).GetMethod(name, Flags)!.Invoke(form, args);
        if (value is Task<bool> task)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(1); }
            Assert.True(task.IsCompleted); return task.GetAwaiter().GetResult();
        }
        return value;
    }

    private static WorkspaceConfig Saved(string directory) => new FileConfigurationService(directory, directory).LoadConfiguration();

    [Fact]
    public void CloudDefaultWithoutConsentSavesAndStaysBlockedWithoutOpeningADialog()
    {
        OnSta(directory =>
        {
            using var form = Open(directory, new WorkspaceConfig { DefaultProfile = "cloud", Profiles = new() { Cloud("cloud") } });
            form.Show();
            using var guard = new System.Windows.Forms.Timer { Interval = 30 };
            bool dialogOpened = false;
            guard.Tick += (_, _) =>
            {
                foreach (var dialog in Application.OpenForms.OfType<DataEgressConsentForm>().ToList())
                { dialogOpened = true; dialog.DialogResult = DialogResult.Cancel; }
            };
            guard.Start();
            Assert.True((bool)Call(form, "SaveActiveConfiguration")!);
            Assert.False(dialogOpened);
            var saved = Saved(directory);
            Assert.Equal("cloud", saved.DefaultProfile);
            Assert.Null(saved.DataEgress.Consent);
            Assert.False(DataEgress.AllowsModel(saved, saved.Profiles[0]));
        });
    }

    [Fact]
    public void SavingAnAuthorizedCloudWithANamedKeyDoesNotReportBlockedDispatch()
    {
        OnSta(directory =>
        {
            var cloud = Cloud("cloud"); cloud.SecretRef = "profile_cloud_token";
            var service = new FileConfigurationService(directory, directory);
            service.StoreSecret(cloud.SecretRef, TestEgress.ResolveSecret(cloud.SecretRef)!);
            using var form = Open(directory, TestEgress.Accepted(new WorkspaceConfig { DefaultProfile = cloud.Id, Profiles = new() { cloud } }));
            Assert.True((bool)Call(form, "SaveActiveConfiguration")!);
            string status = Field<Label>(form, "_lblStatus").Text;
            Assert.DoesNotContain("invii esterni restano bloccati", status);
            Assert.DoesNotContain("da verificare con il vault", status);
            var saved = service.LoadConfiguration();
            Assert.True(DataEgress.AllowsModel(saved, saved.Profiles[0], service.ResolveSecret));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelledAcceptanceOrDeniedElevatedSavePreservesThePreviousAuthorization(bool accept)
    {
        OnSta(directory =>
        {
            var config = TestEgress.Accepted(new WorkspaceConfig { DefaultProfile = "cloud", Profiles = new() { Cloud("cloud") } });
            using var form = Open(directory, config);
            var before = Saved(directory).DataEgress.Consent!;
            Func<string, string?, Task<OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.ElevationResult>> writer = (_, _) =>
                Task.FromResult(new OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.ElevationResult { Cancelled = true });
            typeof(MainConfigForm).GetField("_elevatedWriter", Flags)!.SetValue(form, writer);
            typeof(MainConfigForm).GetField("_requiresElevation", Flags)!.SetValue(form, true);
            Exception? error = null;
            using var timer = new System.Windows.Forms.Timer { Interval = 30 };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.OfType<DataEgressConsentForm>().FirstOrDefault();
                if (dialog == null) return;
                timer.Stop();
                try
                {
                    var read = (CheckBox)typeof(DataEgressConsentForm).GetField("_read", Flags)!.GetValue(dialog)!;
                    Assert.False(read.Checked);
                    if (!accept) { dialog.DialogResult = DialogResult.Cancel; return; }
                    read.Checked = true;
                    var button = (Button)typeof(DataEgressConsentForm).GetField("_accept", Flags)!.GetValue(dialog)!;
                    Assert.True(button.Enabled);
                    button.PerformClick();
                }
                catch (Exception ex) { error = ex; dialog.DialogResult = DialogResult.Cancel; }
            };
            timer.Start();
            Assert.False((bool)Call(form, "AcceptAndSaveDataEgressAsync")!);
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
            Assert.Equal(before.AcceptedUtc, Field<WorkspaceConfig>(form, "_config").DataEgress.Consent!.AcceptedUtc);
            Assert.Equal(before.AcceptedUtc, Saved(directory).DataEgress.Consent!.AcceptedUtc);
            Assert.Single(Field<WorkspaceConfig>(form, "_config").DataEgress.History);
            Assert.False(Field<bool>(form, "_isDirty"));
        });
    }

    [Fact]
    public void DeniedElevatedRevocationRestoresAuthorizationAndHistory()
    {
        OnSta(directory =>
        {
            using var form = Open(directory, TestEgress.Accepted(new WorkspaceConfig { DefaultProfile = "cloud", Profiles = new() { Cloud("cloud") } }));
            Func<string, string?, Task<OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.ElevationResult>> writer = (_, _) =>
                Task.FromResult(new OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.ElevationResult { Cancelled = true });
            typeof(MainConfigForm).GetField("_elevatedWriter", Flags)!.SetValue(form, writer);
            typeof(MainConfigForm).GetField("_requiresElevation", Flags)!.SetValue(form, true);
            Assert.False((bool)Call(form, "ApplyRevocation")!);
            var config = Field<WorkspaceConfig>(form, "_config");
            Assert.NotNull(config.DataEgress.Consent);
            Assert.True(DataEgress.AllowsModel(config, config.Profiles[0]));
            Assert.Single(config.DataEgress.History);
            Assert.NotNull(Saved(directory).DataEgress.Consent);
            Assert.False(Field<bool>(form, "_isDirty"));
        });
    }

    [Fact]
    public void Revocation_WithCloudDefault_IsSavedAndBlocksDispatch()
    {
        OnSta(directory =>
        {
            var config = TestEgress.Accepted(new WorkspaceConfig { DefaultProfile = "cloud", Profiles = new() { Cloud("cloud"), Local("local") } });
            using var form = Open(directory, config);
            Assert.True((bool)Call(form, "ApplyRevocation")!);
            var saved = Saved(directory);
            Assert.Equal("cloud", saved.DefaultProfile);
            Assert.Null(saved.DataEgress.Consent);
            Assert.False(DataEgress.AllowsModel(saved, saved.Profiles[0]));
            Assert.Equal(DataEgressEvent.Revoked, saved.DataEgress.History.Last().Action);
        });
    }

    [Fact]
    public void Revocation_WithLocalDefault_IsSavedAndRecorded()
    {
        OnSta(directory =>
        {
            var config = TestEgress.Accepted(new WorkspaceConfig { DefaultProfile = "local", Profiles = new() { Local("local"), Cloud("cloud") } });
            using var form = Open(directory, config);

            Assert.True((bool)Call(form, "ApplyRevocation")!);

            var saved = Saved(directory);
            Assert.Null(saved.DataEgress!.Consent);
            Assert.Equal(DataEgressEvent.Revoked, saved.DataEgress.History.Last().Action);
            Assert.False(Field<bool>(form, "_isDirty"));
        });
    }

    /// <summary>A174: Avanzate shows outcome, provenance and date; a changed key drops the probes, a reload keeps them.</summary>
    [Fact]
    public void CapabilitySection_ShowsOutcomeOriginAndDate_AndAChangedKeyDropsTheProbes()
    {
        OnSta(directory =>
        {
            var cloud = Cloud("cloud");
            var cache = ModelCapabilitiesCache.For(cloud);
            cache.Record(new CapabilityProbeResult { Kind = ProbeKind.Json, Outcome = ProbeOutcome.Supported, Detail = "conforme", CheckedUtc = new DateTime(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc) });
            cache.Record(new CapabilityProbeResult { Kind = ProbeKind.Reasoning, Outcome = ProbeOutcome.Inconclusive, Detail = "nessun ragionamento", CheckedUtc = DateTime.UtcNow });
            cloud.ModelCapabilities = cache;
            using var form = Open(directory, TestEgress.Accepted(new WorkspaceConfig { DefaultProfile = "cloud", Profiles = new() { cloud } }));
            Field<ListBox>(form, "_lstProfiles").SelectedIndex = 0;

            string[] Texts() => Field<TableLayoutPanel>(form, "_capabilityTable").Controls.Cast<Control>().Select(c => c.Text).ToArray();
            var texts = Texts();
            Assert.Equal(new[] { "Funzione", "Esito", "Provenienza e data" }, texts.Take(3));
            Assert.Equal(new[] { "JSON strutturato", "✓ sì", "verificata dall'app · " + new DateTime(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm") }, texts.Skip(6).Take(3));
            Assert.Equal(new[] { "Ragionamento", "? non verificabile" }, texts.Skip(9).Take(2));
            Assert.Equal(new[] { "Visione", "? non dichiarata", "nessuna dichiarazione né prova" }, texts.Skip(18).Take(3));
            // The command that lists models is not presented as a capability check.
            Assert.Equal("Scegli modello…", Field<Button>(form, "_btnDetectModels").Text);

            Field<TextBox>(form, "_txtToken").Text = "chiave-diversa";
            Assert.Equal("? non dichiarata", Texts()[7]);
            Assert.True(Field<bool>(form, "_isDirty"));
        });
    }

    /// <summary>Another profile shown while locked: unlocking restores that profile's states, not the first one's.</summary>
    [Fact]
    public void Unlock_AfterBrowsingAnotherProfile_RestoresItsOwnStates()
    {
        OnSta(directory =>
        {
            using var form = Open(directory, new WorkspaceConfig { DefaultProfile = "local", Profiles = new() { Local("local"), Cloud("cloud") } });
            var list = Field<ListBox>(form, "_lstProfiles");
            list.SelectedIndex = 0;
            Call(form, "SetLocked", true);
            list.SelectedIndex = 1;
            Call(form, "SetLocked", false);

            Assert.Equal(ProviderCatalog.Get("qwen").SupportsNumCtx, Field<NumericUpDown>(form, "_numNumCtx").Enabled);
            Assert.True(Field<ComboBox>(form, "_cmbExposure").Enabled, "The cloud profile's exposure level stayed disabled after unlocking.");
        });
    }

    /// <summary>Read-only and per-user actions stay usable under the administrator password.</summary>
    [Fact]
    public void Locked_KeepsReadOnlyAndPerUserActions()
    {
        OnSta(directory =>
        {
            using var form = Open(directory, new WorkspaceConfig { DefaultProfile = "local", Profiles = new() { Local("local") } });
            Call(form, "SetLocked", true);

            Assert.True(Field<Button>(form, "_btnEgressExport").Enabled);
            Assert.True(Field<CheckBox>(form, "_chkAutostart").Enabled);
            Assert.True(Field<Button>(form, "_btnOpenDataFolder").Enabled);
            Assert.False(Field<Button>(form, "_btnSave").Enabled);
        });
    }

    [Fact]
    public void Save_WithValidationWarnings_SaysSoInTheStatus()
    {
        OnSta(directory =>
        {
            var config = new WorkspaceConfig { DefaultProfile = "local", Profiles = new() { Local("local") } };
            config.Profiles.Add(Local("warning"));
            config.Profiles[1].External = true;
            TestEgress.Accepted(config);
            using var form = Open(directory, config);

            Assert.True((bool)Call(form, "SaveActiveConfiguration")!);

            var status = Field<Label>(form, "_lblStatus").Text;
            Assert.Contains("avviso", status);
            Assert.Contains("cloud", status);
        });
    }

    /// <summary>A Yes/No warning confirms something hard to undo: Enter answers No, and Tab reads the buttons left to right.</summary>
    [Fact]
    public void WarningConfirmation_DefaultsToTheSafeAnswer()
    {
        OnSta(_ =>
        {
            var type = typeof(MainConfigForm).Assembly.GetType("OnlyWorkspace.ConfigTool.Forms.CopyableMessageBox", true)!;
            var show = type.GetMethod("Show", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(IWin32Window), typeof(string), typeof(string), typeof(MessageBoxButtons), typeof(MessageBoxIcon), typeof(string[]) }, null)!;
            string caption = "Synthetic removal " + Guid.NewGuid();
            Exception? error = null;
            using var timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().SingleOrDefault(form => form.Text == caption);
                if (dialog == null) return;
                timer.Stop();
                try
                {
                    Assert.Equal(DialogResult.No, ((Button)dialog.AcceptButton!).DialogResult);
                    var buttons = dialog.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().OrderBy(b => b.TabIndex).Select(b => b.Text).ToList();
                    Assert.Equal(new[] { "Copia messaggio", "Togli", "Annulla" }, buttons);
                }
                catch (Exception ex) { error = ex; }
                dialog.Close();
            };
            timer.Start();
            show.Invoke(null, new object?[] { null, "Togliere la cassetta?", caption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, new[] { "Togli", "Annulla" } });
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        });
    }

    [Fact]
    public void ErrorText_HidesFrameworkTextAndKeepsProductSentences()
    {
        Assert.Equal("Autorizzazione di amministratore annullata: nessuna modifica è stata applicata.",
            ErrorText.For(new System.ComponentModel.Win32Exception(1223)));
        Assert.DoesNotContain("path is denied", ErrorText.For(new UnauthorizedAccessException("Access to the path is denied.")));
        Assert.DoesNotContain("Sequence", ErrorText.For(new InvalidOperationException("Sequence contains no elements")));
        var product = Record.Exception(() => DataEgress.Accept(new WorkspaceConfig(), "u", "pc", DateTime.UtcNow, TestEgress.ResolveSecret));
        Assert.NotNull(product);
        Assert.Equal(product!.Message, ErrorText.For(product));
    }

    [Fact]
    public void ElevationErrorsKeepItalianInstructionsSeparateFromTheCopyableTechnicalDetail()
    {
        OnSta(directory =>
        {
            var error = new IOException("synthetic English detail");
            var result = OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.Failure(error);
            Assert.DoesNotContain("synthetic English detail", result.Message);
            Assert.Contains("synthetic English detail", result.TechnicalDetail);
            Directory.CreateDirectory(directory);
            OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.WriteFailure(directory, error);
            Assert.Equal(result.Message, File.ReadAllText(Path.Combine(directory, OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.ResultFileName)));
            Assert.Equal(result.TechnicalDetail, File.ReadAllText(Path.Combine(directory, OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.DetailFileName)));

            using var form = Open(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a") } });
            Func<string, string?, Task<OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.ElevationResult>> writer = (_, _) => Task.FromResult(result);
            typeof(MainConfigForm).GetField("_elevatedWriter", Flags)!.SetValue(form, writer);
            var task = (Task<OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.ElevationResult>)typeof(MainConfigForm).GetMethod("RunElevatedAsync", Flags)!.Invoke(form, new object?[] { "synthetic", null })!;
            Assert.Same(result, task.GetAwaiter().GetResult());
            Assert.Contains("synthetic English detail", Field<TextBox>(form, "_txtLog").Text);
        });
    }
}
