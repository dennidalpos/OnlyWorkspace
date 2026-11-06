using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Tests;

/// <summary>ConfigTool editing rules that protect the administrative files and avoid paid requests.</summary>
[Collection(WinFormsCollection.Name)]
public sealed class MainConfigFormTests
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void OnSta(Action<string> action)
    {
        Exception? error = null;
        string directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace-config-" + Guid.NewGuid());
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

    // Local bearer profiles produce no validation warning, so saving shows no modal dialog.
    private static LlmProfileConfig Local(string id, bool web = false) => new()
    {
        Id = id, DisplayName = id, Type = "ollama", BaseUrl = "http://127.0.0.1:11434", Model = "synthetic",
        AuthType = "bearer", SecretRef = $"profile_{id}_token", WebSearchEnabled = web
    };

    private static void Write(string directory, WorkspaceConfig config, Dictionary<string, string> secrets)
    {
        new FileConfigurationService(directory, directory).SaveConfiguration(config);
        var vault = new DpapiSecretVault();
        foreach (var secret in secrets) vault.SetSecret(secret.Key, secret.Value);
        vault.Save(Path.Combine(directory, "secrets.bin"));
    }

    private static DpapiSecretVault ReadVault(string directory)
    {
        var vault = new DpapiSecretVault();
        vault.Load(Path.Combine(directory, "secrets.bin"));
        return vault;
    }

    private static T Field<T>(MainConfigForm form, string name) => (T)typeof(MainConfigForm).GetField(name, Flags)!.GetValue(form)!;

    private static object? Call(MainConfigForm form, string name)
    {
        var value = typeof(MainConfigForm).GetMethod(name, Flags)!.Invoke(form, null);
        if (value is Task<bool> task)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(1); }
            Assert.True(task.IsCompleted); return task.GetAwaiter().GetResult();
        }
        return value;
    }

    [Fact]
    public void GenericWizard_UsesTypedBearerAndPreservesExplicitExternalFlag()
    {
        OnSta(directory =>
        {
            using var wizard = new SetupWizardForm(new LlmProfileConfig { Id = "p", Type = ProviderCatalog.OpenAICompatible,
                BaseUrl = "http://127.0.0.1:1234/v1", Model = "synthetic", External = true }, null, true, true);
            var providers = (ComboBox)typeof(SetupWizardForm).GetField("_cmbProvider", Flags)!.GetValue(wizard)!;
            Assert.Equal(6, providers.Items.Count);
            ((TextBox)typeof(SetupWizardForm).GetField("_txtKey", Flags)!.GetValue(wizard)!).Text = "synthetic-token";
            var edited = (LlmProfileConfig)typeof(SetupWizardForm).GetMethod("Edited", Flags)!.Invoke(wizard, null)!;
            Assert.Equal("bearer", edited.AuthType);
            Assert.True(edited.External);
        });
    }

    [Theory]
    [InlineData("azure-openai")]
    [InlineData("perplexity")]
    [InlineData("ollama-cloud")]
    public void LegacyProviderAppearsOnlyForItsExistingProfile_AndEditingPreservesTransport(string type)
    {
        OnSta(directory =>
        {
            var legacy = Local("legacy");
            legacy.Type = type;
            legacy.BaseUrl = ProviderCatalog.Get(type).DefaultBaseUrl;
            legacy.AuthType = type == "azure-openai" ? "apiKey" : "bearer";
            var local = Local("local");
            Write(directory, new WorkspaceConfig { DefaultProfile = legacy.Id, Profiles = new() { legacy, local } }, new() { [legacy.SecretRef] = "synthetic-token" });
            using var form = new MainConfigForm(directory, null, directory);
            var providers = Field<ComboBox>(form, "_cmbProvider");
            Assert.Equal(7, providers.Items.Count);
            Assert.Equal(type, ((ProviderCapabilities)providers.SelectedItem!).Type);
            Call(form, "UpdateSelectedProfileFromUi");
            var edited = Field<WorkspaceConfig>(form, "_config").Profiles[0];
            Assert.Equal(type, edited.Type);
            Assert.Equal(legacy.BaseUrl, edited.BaseUrl);
            Assert.Equal(legacy.AuthType, edited.AuthType);
            Field<ListBox>(form, "_lstProfiles").SelectedIndex = 1;
            Assert.Equal(6, providers.Items.Count);
            Assert.DoesNotContain(providers.Items.Cast<ProviderCapabilities>(), p => ProviderCatalog.IsLegacy(p.Type));
            Field<ListBox>(form, "_lstProfiles").SelectedIndex = 0;
            providers.SelectedItem = ProviderCatalog.Get(ProviderCatalog.OpenAICompatible);
            Assert.Equal(6, providers.Items.Count);
            Assert.Equal(ProviderCatalog.OpenAICompatible, edited.Type);
        });
    }

    [Fact]
    public void CloudProfile_AlwaysMasks_AndNeverStoresTheForcedStateAsTheOption()
    {
        OnSta(directory =>
        {
            var cloud = Local("a"); cloud.Type = "qwen"; cloud.BaseUrl = ProviderCatalog.Get("qwen").DefaultBaseUrl;
            cloud.PseudonymizeAmounts = true;
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { cloud } }, new());
            using var form = new MainConfigForm(directory, null, directory);
            var amounts = Field<CheckBox>(form, "_chkPseudonymizeAmounts");
            var dates = Field<CheckBox>(form, "_chkPseudonymizeDates");
            // Cloud masks both regardless: the options are hidden and show only what the user chose before.
            Assert.False(OwnVisible(amounts)); Assert.False(OwnVisible(dates));
            Assert.True(amounts.Checked); Assert.False(dates.Checked);
            Call(form, "UpdateSelectedProfileFromUi");
            var saved = Field<WorkspaceConfig>(form, "_config").Profiles.Single();
            Assert.True(saved.PseudonymizeAmounts); Assert.False(saved.PseudonymizeDates);
            Assert.True(saved.IsExternal);

            Field<ComboBox>(form, "_cmbProvider").SelectedItem = ProviderCatalog.Get("ollama");
            Assert.True(OwnVisible(amounts)); Assert.True(OwnVisible(dates));
            Assert.True(amounts.Enabled); Assert.True(dates.Enabled);
            Assert.True(amounts.Checked); Assert.False(dates.Checked);
            Assert.StartsWith("Profilo locale", Field<Label>(form, "_lblExposure").Text);
            Assert.DoesNotContain("non si applicano", Field<Label>(form, "_lblExposure").Text);
        });
    }

    [Fact]
    public void LocalProfile_ChoosesAmountsAndDates_AndTheChoiceIsSaved()
    {
        OnSta(directory =>
        {
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a") } }, new());
            using var form = new MainConfigForm(directory, null, directory);
            var amounts = Field<CheckBox>(form, "_chkPseudonymizeAmounts");
            var dates = Field<CheckBox>(form, "_chkPseudonymizeDates");
            Assert.True(OwnVisible(amounts)); Assert.True(OwnVisible(dates));
            Assert.True(amounts.Enabled); Assert.True(dates.Enabled);
            Assert.False(amounts.Checked); Assert.False(dates.Checked);

            dates.Checked = true;
            Call(form, "UpdateSelectedProfileFromUi");
            var profile = Field<WorkspaceConfig>(form, "_config").Profiles.Single();
            Assert.False(profile.IsExternal);
            Assert.False(profile.PseudonymizeAmounts); Assert.True(profile.PseudonymizeDates);

            // A cloud connector masks both anyway and keeps the local choice untouched for the way back.
            Field<ComboBox>(form, "_cmbProvider").SelectedItem = ProviderCatalog.Get("qwen");
            Assert.False(OwnVisible(amounts)); Assert.False(OwnVisible(dates));
            Call(form, "UpdateSelectedProfileFromUi");
            Assert.False(profile.PseudonymizeAmounts); Assert.True(profile.PseudonymizeDates);
            Assert.True(profile.IsExternal);
        });
    }

    [Fact]
    public void SwitchingCloudConnectorBackToLoopback_DoesNotKeepTheForcedCloudFlag()
    {
        OnSta(directory =>
        {
            var cloud = Local("a");
            cloud.Type = "qwen";
            cloud.BaseUrl = ProviderCatalog.Get("qwen").DefaultBaseUrl;
            cloud.External = true;
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { cloud } }, new());
            using var form = new MainConfigForm(directory, null, directory);
            Field<ComboBox>(form, "_cmbProvider").SelectedItem = ProviderCatalog.Get("ollama");
            var edited = Field<WorkspaceConfig>(form, "_config").Profiles.Single();
            Assert.False(edited.External);
            Assert.False(edited.IsExternal);
            Assert.False(Field<CheckBox>(form, "_chkExternal").Checked);
        });
    }

    [Fact]
    public void RemoteEndpoint_ForcesTheIndicatorAndAttachmentPermission_WithoutChangingTheExplicitFlag()
    {
        OnSta(directory =>
        {
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a") } },
                new() { ["profile_a_token"] = "synthetic" });
            using var form = new MainConfigForm(directory, null, directory);
            var endpoint = Field<TextBox>(form, "_txtEndpoint");
            var external = Field<CheckBox>(form, "_chkExternal");
            var attachments = Field<CheckBox>(form, "_chkAllowAttachments");
            endpoint.Text = "https://remote.example.test/v1";
            Assert.True(external.Checked);
            Assert.False(external.Enabled);
            // External profiles never send attachment text, including at Standard.
            Assert.False(attachments.Enabled);
            var exposure = Field<ComboBox>(form, "_cmbExposure");
            exposure.SelectedIndex = (int)OnlyWorkspace.Core.Privacy.ExposureLevel.Standard;
            Assert.False(attachments.Enabled);
            var edited = Field<WorkspaceConfig>(form, "_config").Profiles.Single();
            Assert.Equal(OnlyWorkspace.Core.Privacy.ExposureLevel.Standard, edited.Exposure);
            Assert.False(edited.External);
            Assert.True(edited.IsExternal);
            attachments.Checked = true;
            Assert.True(edited.AllowAttachmentUpload);
            endpoint.Text = "http://127.0.0.1:11434";
            Assert.False(external.Checked);
            Assert.True(external.Enabled);
            Assert.False(attachments.Enabled);
            Assert.False(edited.IsExternal);
        });
    }

    [Fact]
    public void ProfileEdits_NeverStartTheWebSearchCheck()
    {
        OnSta(directory =>
        {
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a", web: true) } },
                new() { ["profile_a_token"] = "secret" });
            using var form = new MainConfigForm(directory, null, directory);
            var check = typeof(MainConfigForm).GetField("_webSupportCheck", Flags)!;
            var web = Field<CheckBox>(form, "_chkWebSearch");
            Assert.Null(check.GetValue(form));
            Assert.True(web.Checked);
            Assert.True(web.Enabled); // a saved activation can be switched off

            Field<TextBox>(form, "_txtEndpoint").Text = "http://127.0.0.1:11435";
            Field<TextBox>(form, "_txtToken").Text = "other";

            Assert.Null(check.GetValue(form));
            Assert.False(web.Checked);
            Assert.False(web.Enabled);
            Assert.Equal("Da verificare", Field<Label>(form, "_lblWebSupport").Text);
        });
    }

    [Fact]
    public void OllamaWebKeyRow_IsShownOnlyForOllamaConnectors()
    {
        OnSta(directory =>
        {
            var qwen = new LlmProfileConfig
            {
                Id = "q", DisplayName = "q", Type = "qwen", BaseUrl = "https://token-plan.maas.qwencloudapi.com/compatible-mode/v1",
                Model = "synthetic", AuthType = "bearer", SecretRef = "profile_q_token", External = true
            };
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a"), qwen } },
                new() { ["profile_a_token"] = "s1", ["profile_q_token"] = "s2" });
            using var form = new MainConfigForm(directory, null, directory);
            var key = Field<TextBox>(form, "_txtWebSearchKey");
            var row = RowOf(key);
            var profiles = Field<ListBox>(form, "_lstProfiles");

            Assert.All(row, control => Assert.True(OwnVisible(control)));
            profiles.SelectedIndex = 1;
            Assert.All(row, control => Assert.False(OwnVisible(control)));
            profiles.SelectedIndex = 0;
            Assert.All(row, control => Assert.True(OwnVisible(control)));
        });
    }

    /// <summary>The label and the field cell of the grid row holding <paramref name="field"/>.</summary>
    private static List<Control> RowOf(Control field)
    {
        Control cell = field;
        while (cell.Parent is not TableLayoutPanel) cell = cell.Parent!;
        var grid = (TableLayoutPanel)cell.Parent;
        int row = grid.GetRow(cell);
        var controls = grid.Controls.Cast<Control>().Where(control => grid.GetRow(control) == row).ToList();
        Assert.Equal(2, controls.Count);
        return controls;
    }

    /// <summary>The control's own Visible flag: the getter also reflects hidden ancestors, as in a form never shown.</summary>
    private static bool OwnVisible(Control control)
    {
        var getState = typeof(Control).GetMethod("GetState", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var flag = getState.GetParameters()[0].ParameterType;
        return (bool)getState.Invoke(control, new[] { flag.IsEnum ? Enum.ToObject(flag, 0x2) : (object)0x2 })!;
    }


    [Fact]
    public void ClearedToken_AndOrphanKeys_AreRemovedFromTheVault()
    {
        OnSta(directory =>
        {
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a"), Local("b") } },
                new() { ["profile_a_token"] = "s1", ["profile_b_token"] = "s2", ["profile_removed_token"] = "old" });
            using var form = new MainConfigForm(directory, null, directory);
            Field<TextBox>(form, "_txtToken").Text = string.Empty;

            Assert.Equal(true, Call(form, "SaveActiveConfiguration"));

            var vault = ReadVault(directory);
            Assert.Null(vault.GetSecret("profile_a_token"));
            Assert.Null(vault.GetSecret("profile_removed_token"));
            Assert.Equal("s2", vault.GetSecret("profile_b_token"));
            var saved = new FileConfigurationService(directory, directory).LoadConfiguration();
            Assert.Equal(string.Empty, saved.Profiles.Single(p => p.Id == "a").SecretRef);
        });
    }

    [Fact]
    public void RenamedProfile_MovesItsKey()
    {
        OnSta(directory =>
        {
            var config = new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a") } };
            Write(directory, config, new() { ["profile_a_token"] = "s1" });
            using var form = new MainConfigForm(directory, null, directory);
            Field<TextBox>(form, "_txtProfileId").Text = "renamed";
            Call(form, "ApplyProfileIdChange");

            Assert.Equal(true, Call(form, "SaveActiveConfiguration"));

            var vault = ReadVault(directory);
            Assert.Null(vault.GetSecret("profile_a_token"));
            Assert.Equal("s1", vault.GetSecret("profile_renamed_token"));
            var saved = new FileConfigurationService(directory, directory).LoadConfiguration();
            Assert.Equal("renamed", saved.DefaultProfile);
        });
    }

    [Fact]
    public void InvalidConfiguration_BlocksSaveAndKeepsTheFile()
    {
        OnSta(directory =>
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{ not json");
            using var form = new MainConfigForm(directory, null, directory);

            Assert.Equal(false, Call(form, "SaveActiveConfiguration"));
            Assert.False(Field<Button>(form, "_btnSave").Enabled);
            Assert.Equal("{ not json", File.ReadAllText(path));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableVault_BlocksSaveAndKeepsTheKeys(bool empty)
    {
        OnSta(directory =>
        {
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a") } }, new());
            string vaultPath = Path.Combine(directory, "secrets.bin");
            var garbage = empty ? Array.Empty<byte>() : new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            File.WriteAllBytes(vaultPath, garbage);
            using var form = new MainConfigForm(directory, null, directory);

            Assert.Equal(false, Call(form, "SaveActiveConfiguration"));
            Assert.False(Field<Button>(form, "_btnSave").Enabled);
            Assert.Equal(garbage, File.ReadAllBytes(vaultPath));
        });
    }

    /// <summary>Read-only after a failed load shows the file's own profiles, never the defaults as if they were the configuration.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableVault_ShowsTheFileProfiles(bool empty)
    {
        OnSta(directory =>
        {
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a"), Local("b") } }, new());
            File.WriteAllBytes(Path.Combine(directory, "secrets.bin"), empty ? Array.Empty<byte>() : new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            using var form = new MainConfigForm(directory, null, directory);

            var names = Field<ListBox>(form, "_lstProfiles").Items.Cast<object>().Select(o => o.ToString()).ToList();
            Assert.Equal(2, names.Count);
            Assert.True(Field<bool>(form, "_fileShown"));
            Assert.Equal("a", Field<TextBox>(form, "_txtProfileId").Text);
        });
    }

    [Fact]
    public void UnparseableConfig_SaysTheValuesAreDefaults()
    {
        OnSta(directory =>
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "config.json"), "{ not json");
            using var form = new MainConfigForm(directory, null, directory);

            Assert.False(Field<bool>(form, "_fileShown"));
            Assert.Contains(Descendants(form).OfType<Control>(), c => c.Text.Contains("quelli mostrati sono predefiniti"));
        });
    }

    /// <summary>Locked by the administrator password: every profile and section can be read, nothing edited.</summary>
    [Fact]
    public void Locked_ProfilesAndSectionsStayBrowsable()
    {
        OnSta(directory =>
        {
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a"), Local("b") } }, new());
            using var form = new MainConfigForm(directory, null, directory);
            typeof(MainConfigForm).GetMethod("SetLocked", Flags)!.Invoke(form, new object[] { true });

            var list = Field<ListBox>(form, "_lstProfiles");
            var id = Field<TextBox>(form, "_txtProfileId");
            var profilesPage = Field<List<Control>>(form, "_pages")[0];
            Assert.True(list.Enabled);
            Assert.All(Descendants(profilesPage).OfType<NavButton>(), tab => Assert.True(tab.Enabled));
            Assert.False(id.Enabled);

            list.SelectedIndex = 1;
            Assert.Equal("b", id.Text);
            Assert.False(id.Enabled);
            Assert.False(Field<Button>(form, "_btnSave").Enabled);
        });
    }

    /// <summary>Closing the ConfigTool while Le mie cassette loads cancels the request: nothing may escape the async UI handler.</summary>
    /// <summary>A140: model limits read while the ConfigTool closes must not throw from the async handlers that await them.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ModelLimits_ArrivingAfterTheFormClosed_AreIgnoredSilently(bool answered)
    {
        var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        using var reply = new ManualResetEventSlim();
        int port = ((System.Net.IPEndPoint)server.LocalEndpoint).Port;
        // Every request (the form also reads the limits when it selects the profile) waits for the form to close.
        _ = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    var client = server.AcceptTcpClient();
                    _ = Task.Run(() =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var request = new System.Text.StringBuilder();
                            var buffer = new byte[4096];
                            for (int n; request.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0 && (n = stream.Read(buffer, 0, buffer.Length)) > 0;)
                                request.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, n));
                            reply.Wait(TimeSpan.FromSeconds(15));
                            string body = answered ? "{\"model_info\":{\"llama.context_length\":8192},\"capabilities\":[\"completion\"]}" : "{\"error\":\"synthetic\"}";
                            var bytes = System.Text.Encoding.UTF8.GetBytes((answered ? "HTTP/1.1 200 OK" : "HTTP/1.1 500 Internal Server Error") +
                                "\r\nContent-Type: application/json\r\nContent-Length: " + System.Text.Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body);
                            stream.Write(bytes, 0, bytes.Length);
                        }
                    });
                }
            }
            catch (System.Net.Sockets.SocketException) { }
        });
        try
        {
            OnSta(directory =>
            {
                var profile = Local("a");
                profile.BaseUrl = "http://127.0.0.1:" + port;
                Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { profile } }, new() { ["profile_a_token"] = "secret" });
                var form = new MainConfigForm(directory, null, directory);
                var selected = Field<ListBox>(form, "_lstProfiles").SelectedItem!;
                var loaded = (LlmProfileConfig)selected.GetType().GetProperty("Profile")!.GetValue(selected)!;
                var load = (Task)typeof(MainConfigForm).GetMethod("LoadModelLimitsAsync", Flags)!.Invoke(form, new object[] { loaded })!;

                form.Dispose();
                reply.Set();
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (!load.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Sleep(10); }

                Assert.True(load.IsCompleted, "The limits request did not end.");
                Assert.Equal(TaskStatus.RanToCompletion, load.Status);
            });
        }
        finally
        {
            reply.Set();
            // Stop ends the accept loop.
            server.Stop();
        }
    }

    [Fact]
    public void MailboxPanel_CancelledWhileLoading_DoesNotThrow()
    {
        OnSta(directory =>
        {
            var type = typeof(MainConfigForm).Assembly.GetType("OnlyWorkspace.ConfigTool.Forms.MailboxAuthorizationPanel")!;
            using var tips = new ToolTip();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var config = new FileConfigurationService(directory, directory);
            using var panel = (Control)Activator.CreateInstance(type, config, tips, cancelled.Token)!;
            var load = (Task)type.GetMethod("LoadAsync")!.Invoke(panel, null)!;
            Assert.True(load.Wait(TimeSpan.FromSeconds(15)));
            Assert.Equal(TaskStatus.RanToCompletion, load.Status);
        });
    }


    /// <summary>A24: only a closed mailbox with emails in the archive offers «Togli dall'archivio», also under «all».</summary>
    [Fact]
    public void MailboxPanel_ClosedMailbox_OffersRemovalUnderAll()
    {
        OnSta(directory =>
        {
            var type = typeof(MainConfigForm).Assembly.GetType("OnlyWorkspace.ConfigTool.Forms.MailboxAuthorizationPanel")!;
            using var tips = new ToolTip();
            using var panel = (Control)Activator.CreateInstance(type, new FileConfigurationService(directory, directory), tips, CancellationToken.None)!;
            var stores = new List<ArchiveOpenStore>
            {
                new() { Id = "gmail", Name = "Gmail", Open = true },
                new() { Id = "pst", Name = "OW test", Open = false },
                new() { Id = "empty", Name = "Vuota", Open = false }
            };
            type.GetMethod("BuildGrid", Flags)!.Invoke(panel, new object?[] { stores, new List<string>(), StoreOwnAddresses.Parse(null), new List<string> { "PST", "gmail" } });

            var remove = Assert.Single(Descendants(panel).OfType<Button>(), b => b.AccessibleName?.StartsWith("Togli dall'archivio: ") == true);
            Assert.Equal("Togli dall'archivio: OW test (chiusa)", remove.AccessibleName);
            var rows = (System.Collections.IDictionary)type.GetField("_rows", Flags)!.GetValue(panel)!;
            var archive = (CheckBox)rows["pst"]!;
            Assert.True(archive.Checked);
            Assert.False(archive.Enabled);
        });
    }

    /// <summary>Only a PST without account gets the editable «Indirizzi miei» line, filled with the effective addresses.</summary>
    [Fact]
    public void MailboxPanel_PstWithoutAccount_ShowsItsOwnAddresses()
    {
        OnSta(directory =>
        {
            var type = typeof(MainConfigForm).Assembly.GetType("OnlyWorkspace.ConfigTool.Forms.MailboxAuthorizationPanel")!;
            using var tips = new ToolTip();
            using var panel = (Control)Activator.CreateInstance(type, new FileConfigurationService(directory, directory), tips, CancellationToken.None)!;
            var stores = new List<ArchiveOpenStore>
            {
                new() { Id = "ost", Name = "Lavoro", Open = true, HasAccount = true, IsDataFileStore = true },
                new() { Id = "pst", Name = "Vecchia posta", Open = true, IsDataFileStore = true }
            };
            var json = StoreOwnAddresses.WithChoice(StoreOwnAddresses.WithGuess(null, "pst", new StoreOwnEntry { Guessed = { "a@vecchia.it", "b@vecchia.it" } }), "pst", new[] { "a@vecchia.it" });
            type.GetMethod("BuildGrid", Flags)!.Invoke(panel, new object?[] { stores, new List<string>(), StoreOwnAddresses.Parse(json), new List<string>() });

            // The account's store has no line: one box, named after the PST.
            var box = Assert.Single(Descendants(panel).OfType<TextBox>(), b => b.AccessibleName?.StartsWith("Indirizzi miei: ") == true);
            Assert.Equal("Indirizzi miei: Vecchia posta", box.AccessibleName);
            Assert.Equal("a@vecchia.it", box.Text);
        });
    }

    /// <summary>A159: the note under «Esposizione dati» wraps inside the card instead of running past its right edge.</summary>
    [Fact]
    public void ExposureNote_WrapsInsideTheCard()
    {
        OnSta(directory =>
        {
            var cloud = new LlmProfileConfig
            {
                Id = "c", DisplayName = "c", Type = "openai", BaseUrl = "https://api.example.com", Model = "m", External = true,
                Exposure = OnlyWorkspace.Core.Privacy.ExposureLevel.Standard
            };
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a"), cloud } }, new() { ["profile_a_token"] = "s" });
            new FileConfigurationService(directory, directory).SetUserFlag("guidedSetupDone", true);
            using var form = new MainConfigForm(directory, trayContext: null, preferencesDir: directory)
            {
                StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000)
            };
            form.Show();
            Field<ListBox>(form, "_lstProfiles").SelectedIndex = 1;
            var note = Field<Label>(form, "_lblExposure");
            var grid = (TableLayoutPanel)note.Parent!.Parent!;
            grid.Visible = true;
            grid.PerformLayout();
            Application.DoEvents();

            Assert.StartsWith("Escono email e testo libero", note.Text);
            Assert.False(note.Visible);
            int right = grid.PointToClient(note.PointToScreen(new System.Drawing.Point(note.Width, 0))).X;
            Assert.True(right <= grid.ClientSize.Width, $"note ends at {right}, grid is {grid.ClientSize.Width} wide");
        });
    }

    /// <summary>#058: at the default window size every «?» of the connection group ends inside the grid that holds it.</summary>
    [Fact]
    public void ConnectionHelpButtons_AreFullyInsideTheirGridAtTheDefaultSize() => AssertConnectionHelpInsideGrid(1f);

    /// <summary>#058: the same holds when the window is scaled to 150% and 200%, as on a high-DPI monitor.</summary>
    [Theory]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void ConnectionHelpButtons_AreFullyInsideTheirGridAtLargerScales(float scale) => AssertConnectionHelpInsideGrid(scale);

    private void AssertConnectionHelpInsideGrid(float scale)
    {
        OnSta(directory =>
        {
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a") } }, new() { ["profile_a_token"] = "s" });
            new FileConfigurationService(directory, directory).SetUserFlag("guidedSetupDone", true);
            using var form = new MainConfigForm(directory, trayContext: null, preferencesDir: directory)
            {
                StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000)
            };
            form.Show();
            Application.DoEvents();
            if (scale > 1f)
            {
                // Stand-in for a monitor at this scale: the form font and the design-pixel sizes grow together.
                form.Font = new System.Drawing.Font(form.Font.FontFamily, form.Font.SizeInPoints * scale);
                form.Scale(new System.Drawing.SizeF(scale, scale));
                Application.DoEvents();
            }
            var provider = Field<ComboBox>(form, "_cmbProvider");
            Control cell = provider;
            while (cell.Parent is not TableLayoutPanel && cell.Parent != null) cell = cell.Parent;
            var grid = (TableLayoutPanel)cell.Parent!;
            var helps = Descendants(grid).OfType<Button>().Where(b => b.Text == "?" && b.Visible).ToList();
            Assert.NotEmpty(helps);
            foreach (var help in helps)
            {
                int right = grid.PointToClient(help.PointToScreen(new System.Drawing.Point(help.Width, 0))).X;
                Assert.True(right <= grid.ClientSize.Width, $"«{help.AccessibleName}» ends at {right}, grid is {grid.ClientSize.Width} wide");
                Assert.True(help.Width >= 28 && help.Height >= 28, $"«{help.AccessibleName}» is {help.Width}x{help.Height}");
            }
        });
    }

    /// <summary>INT01: a long description kept behind a «?» shows its first sentence beside it, and nothing runs past the card.</summary>
    [Fact]
    public void ExposureNote_ShowsALeadBesideItsHelpButton()
    {
        OnSta(directory =>
        {
            var cloud = new LlmProfileConfig
            {
                Id = "c", DisplayName = "c", Type = "openai", BaseUrl = "https://api.example.com", Model = "m", External = true,
                Exposure = OnlyWorkspace.Core.Privacy.ExposureLevel.Standard
            };
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a"), cloud } }, new() { ["profile_a_token"] = "s" });
            new FileConfigurationService(directory, directory).SetUserFlag("guidedSetupDone", true);
            using var form = new MainConfigForm(directory, trayContext: null, preferencesDir: directory)
            {
                StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000)
            };
            form.Show();
            var list = Field<ListBox>(form, "_lstProfiles");
            var note = Field<Label>(form, "_lblExposure");
            var row = (FlowLayoutPanel)note.Parent!;
            var grid = (TableLayoutPanel)row.Parent!;

            list.SelectedIndex = 0;
            Application.DoEvents();
            var lead = row.Controls.OfType<Label>().Single(l => l != note);
            // The tab is not on screen: the control's own flag, not the effective visibility.
            Assert.True(OwnVisible(lead));
            Assert.StartsWith("Profilo locale", lead.Text);
            Assert.DoesNotContain(row.Controls.OfType<Button>(), b => OwnVisible(b) && b.Text == "?");

            list.SelectedIndex = 1;
            grid.Visible = true;
            grid.PerformLayout();
            Application.DoEvents();
            Assert.True(OwnVisible(lead) && lead.Text.Length > 0 && lead.Text.Length <= 91, lead.Text);
            var help = Assert.Single(row.Controls.OfType<Button>());
            Assert.True(OwnVisible(help));
            Assert.True(note.Text.Length > lead.Text.Length, "The «?» opens more than what is already visible.");
            int right = grid.PointToClient(help.PointToScreen(new System.Drawing.Point(help.Width, 0))).X;
            Assert.True(right <= grid.ClientSize.Width, $"«?» ends at {right}, grid is {grid.ClientSize.Width} wide");
        });
    }

    /// <summary>INT01: the budget warning appears only when it applies, with no standing «?», and the probe status stays visible.</summary>
    [Fact]
    public void ContextAndCapabilities_HaveNoOrphanHelpAndKeepTheProbeStatusVisible()
    {
        OnSta(directory =>
        {
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a") } }, new() { ["profile_a_token"] = "s" });
            new FileConfigurationService(directory, directory).SetUserFlag("guidedSetupDone", true);
            using var form = new MainConfigForm(directory, trayContext: null, preferencesDir: directory)
            {
                StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000)
            };
            form.Show();
            Application.DoEvents();
            var hint = Field<Label>(form, "_lblBudgetHint");
            Assert.DoesNotContain(hint.Parent!.Controls.OfType<Button>(), b => b.Text == "?");

            var status = Field<OperationalStatusLabel>(form, "_lblProbeStatus");
            Assert.True(status.Visible || !status.Parent!.Visible, "The probe status is a label of the section, not hidden behind a «?».");
            Assert.NotEqual(OnlyWorkspace.Core.Archive.StatusLight.Working, status.State);
            var probeWeb = Field<CheckBox>(form, "_chkProbeWeb");
            Assert.Contains(probeWeb.Parent!.Controls.OfType<Button>(), b => b.AccessibleName == "Informazioni: Costo delle prove");
            Assert.True(status.Text.Length <= 100, "A short status: the cost note lives behind the «?».");
        });
    }

    /// <summary>INT01: «Livello di log» used to run past the card edge with its «?» (Generale, any scale).</summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void GeneralPage_HelpButtonsStayInsideTheirGrid(float scale)
    {
        OnSta(directory =>
        {
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a") } }, new() { ["profile_a_token"] = "s" });
            new FileConfigurationService(directory, directory).SetUserFlag("guidedSetupDone", true);
            using var form = new MainConfigForm(directory, trayContext: null, preferencesDir: directory)
            {
                StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000)
            };
            form.Show();
            Application.DoEvents();
            if (scale > 1f)
            {
                form.Font = new System.Drawing.Font(form.Font.FontFamily, form.Font.SizeInPoints * scale);
                form.Scale(new System.Drawing.SizeF(scale, scale));
            }
            typeof(MainConfigForm).GetMethod("ShowPage", Flags)!.Invoke(form, new object[] { 1 });
            Application.DoEvents();
            Control cell = Field<ComboBox>(form, "_cmbLogLevel");
            while (cell.Parent is not TableLayoutPanel && cell.Parent != null) cell = cell.Parent;
            var grid = (TableLayoutPanel)cell.Parent!;
            grid.PerformLayout();
            var helps = Descendants(grid).OfType<Button>().Where(b => b.Text == "?" && b.Visible).ToList();
            Assert.NotEmpty(helps);
            foreach (var help in helps)
            {
                int right = grid.PointToClient(help.PointToScreen(new System.Drawing.Point(help.Width, 0))).X;
                Assert.True(right <= grid.ClientSize.Width, $"«{help.AccessibleName}» ends at {right}, grid is {grid.ClientSize.Width} wide");
            }
        });
    }

    /// <summary>The credits page is built when first shown, not at every ConfigTool start.</summary>
    [Fact]
    public void AboutPage_IsFilledWhenOpened()
    {
        OnSta(directory =>
        {
            Write(directory, new WorkspaceConfig { DefaultProfile = "a", Profiles = new() { Local("a") } }, new() { ["profile_a_token"] = "s" });
            // Own preferences: without guidedSetupDone (a fresh CI user) Show would open the modal setup wizard.
            new FileConfigurationService(directory, directory).SetUserFlag("guidedSetupDone", true);
            using var form = new MainConfigForm(directory, trayContext: null, preferencesDir: directory);
            // VisibleChanged reaches a page only once its window is shown.
            form.Show();
            Application.DoEvents();
            var pages = Field<List<Control>>(form, "_pages");
            var about = pages[pages.Count - 1];
            Assert.Empty(Descendants(about).OfType<LinkLabel>());

            typeof(MainConfigForm).GetMethod("ShowPage", Flags)!.Invoke(form, new object[] { pages.Count - 1 });
            Assert.Contains(Descendants(about).OfType<LinkLabel>(), link => link.Text == OnlyWorkspace.Core.ThirdPartyComponents.GitHubUrl);
            Assert.Contains(Descendants(about).OfType<Label>(), label => label.Text.StartsWith("Microsoft.Web.WebView2"));
            // The values of both groups start at the same column, although component names are longer.
            var grids = Descendants(about).OfType<TableLayoutPanel>().Where(g => g.ColumnCount == 4).ToList();
            Assert.Equal(2, grids.Count);
            Assert.Equal(grids[0].GetColumnWidths()[0], grids[1].GetColumnWidths()[0]);
        });
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
