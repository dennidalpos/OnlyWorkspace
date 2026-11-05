using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.ConfigTool.Tray;
using OnlyWorkspace.Core;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Core.Validation;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Security;
using OnlyWorkspace.Infrastructure.Storage;

namespace OnlyWorkspace.ConfigTool.Forms;

public sealed partial class MainConfigForm
{

    private void ApplyProfileIdChange()
    {
        if (_isLoading || _lstProfiles.SelectedItem is not ProfileItem item) return;

        var profile = item.Profile;
        string newId = _txtProfileId.Text.Trim();
        if (newId == profile.Id) return;

        if (string.IsNullOrWhiteSpace(newId) || _config.Profiles.Any(p => p != profile && string.Equals(p.Id, newId, StringComparison.OrdinalIgnoreCase)))
        {
            CopyableMessageBox.Show(this, "L'ID profilo deve essere univoco e non vuoto.", "ID non valido", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtProfileId.Text = profile.Id;
            return;
        }

        string oldId = profile.Id;
        profile.Id = newId;
        profile.SecretRef = string.Empty;
        profile.WebSearchSecretRef = string.Empty;

        if (_inMemorySecrets.TryGetValue(oldId, out var secret))
        {
            _inMemorySecrets.Remove(oldId);
            _inMemorySecrets[newId] = secret;
        }
        if (_inMemoryWebSecrets.TryGetValue(oldId, out var webSecret))
        {
            _inMemoryWebSecrets.Remove(oldId);
            _inMemoryWebSecrets[newId] = webSecret;
        }
        if (_config.DefaultProfile == oldId)
        {
            _config.DefaultProfile = newId;
        }

        RefreshProfileNames();

        LogActivity($"Profilo rinominato da '{oldId}' a '{newId}'.");
        MarkDirty();
        ResetWebSupport();
    }

    private ProviderCapabilities CurrentCaps() => _cmbProvider.SelectedItem as ProviderCapabilities ?? ProviderCatalog.All[0];

    private bool IsDefaultProfile(LlmProfileConfig profile) => string.Equals(profile.Id, _config.DefaultProfile, StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-reads names and the default marker in the profile list and in the default-profile combo.</summary>
    private void RefreshProfileNames(bool combo = true)
    {
        _refreshingList = true;
        try
        {
            // Re-assigning an entry is the only way to make a ListBox call ToString() again.
            for (int i = 0; i < _lstProfiles.Items.Count; i++) _lstProfiles.Items[i] = _lstProfiles.Items[i];
            if (!combo) return;

            _cmbDefaultProfile.Items.Clear();
            foreach (var p in _config.Profiles)
            {
                var choice = new DefaultChoice(p);
                _cmbDefaultProfile.Items.Add(choice);
                if (IsDefaultProfile(p)) _cmbDefaultProfile.SelectedItem = choice;
            }

        }
        finally
        {
            _refreshingList = false;
        }
    }

    private static string LimitsKey(LlmProfileConfig p) => p.Id + "|" + p.Type + "|" + p.BaseUrl + "|" + p.Model;

    /// <summary>Limits and capabilities of the selected profile's model when read for exactly that model.</summary>
    private ModelInfo? CurrentModelInfo() =>
        _lstProfiles.SelectedItem is ProfileItem item && _modelLimitsKey == LimitsKey(item.Profile) ? _modelInfo : null;

    /// <summary>Capabilities read in this session, else those saved in the profile for the same model and endpoint.</summary>
    private ModelCapabilities? CurrentCapabilities()
    {
        if (CurrentModelInfo() is { } info) return info.Capabilities;
        return _lstProfiles.SelectedItem is ProfileItem item && item.Profile.ModelCapabilities is { } saved && saved.Matches(item.Profile)
            ? saved.Capabilities : null;
    }

    /// <summary>Limits under the model field; capabilities in «Avanzate».</summary>
    private void ShowModelLimits()
    {
        var caps = CurrentCaps();
        var info = CurrentModelInfo();
        int window = info?.ContextWindow ?? (_lstProfiles.SelectedItem is ProfileItem p && p.Profile.ModelCapabilities is { } saved && saved.Matches(p.Profile) ? saved.ContextWindow : 0);

        Theme.SetTone(_lblModelLimits, Tone.Muted);
        if (window <= 0)
        {
            _lblModelLimits.Text = "";
        }
        else if (caps.SupportsNumCtx)
        {
            bool over = (int)_numNumCtx.Value > window;
            Theme.SetTone(_lblModelLimits, over ? Tone.Danger : Tone.Muted);
            _lblModelLimits.Text = $"Contesto massimo {ProviderCatalog.Tokens(window)}" + (over ? " · NumCtx oltre il limite" : "");
        }
        else
        {
            _lblModelLimits.Text = $"Contesto {ProviderCatalog.Tokens(window)}" +
                                   (info?.MaxOutputTokens > 0 ? $" · risposta {ProviderCatalog.Tokens(info.MaxOutputTokens)}" : "");
        }
        // Hidden when empty: the auto-sized row collapses instead of leaving a gap.
        _lblModelLimits.Visible = _lblModelLimits.Text.Length > 0;

        RefreshCapabilityRows();
        _chkDisableThinking.Enabled = !_isLocked && caps.SupportsThinkToggle && CurrentCapabilities()?.Reasoning != CapabilityState.Unsupported;
    }

    /// <summary>Catalog preset matching the profile values, else custom (Ollama; the list is empty for cloud).</summary>
    private void SelectMatchingPreset()
    {
        if (_lstProfiles.SelectedItem is not ProfileItem item) return;
        var match = ProviderCatalog.MatchPreset(item.Profile);
        _cmbPreset.SelectedItem = _cmbPreset.Items.Contains(match) ? match : null;
    }

    /// <summary>
    /// Reads limits and capabilities from the provider's model endpoint: no text generation and no cost.
    /// No-op without a model or for connectors that publish nothing.
    /// </summary>
    /// <returns>Why nothing was read, for «Aggiorna capacità»; null when read (or nothing to read).</returns>
    private Task<string?> LoadModelLimitsAsync(LlmProfileConfig p) => LoadModelLimitsCoreAsync(p, default);

    private async Task<string?> LoadModelLimitsCoreAsync(LlmProfileConfig p, System.Threading.CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(p.Model)) return "Inserisci il modello.";
        var caps = ProviderCatalog.Get(p.Type);
        if (caps.Capabilities == CapabilitySource.None) return null;
        string key = LimitsKey(p);
        string? secret = _inMemorySecrets.TryGetValue(p.Id, out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
        if (caps.RequiresSecret && secret == null && !string.Equals(p.AuthType, "none", StringComparison.OrdinalIgnoreCase)) return "Inserisci la chiave di accesso.";

        ModelInfo? info = null;
        try
        {
            var client = (ConnectionClientFactory ?? ((profile, key) => LlmClientFactory.CreateClient(profile, key)))(p, secret);
            try
            {
                using var cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _archiveLifetime.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                info = await client.GetModelInfoAsync(p.Model, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
            }
            finally
            {
                (client as IDisposable)?.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (IsDisposed || _archiveLifetime.IsCancellationRequested) return null;
            if (!IsDisposed) LogActivity($"Capacità del modello '{p.Model}' non lette: {ex.Message}");
            return "Capacità non lette: " + ErrorText.For(ex);
        }

        // Closed during the 10 s wait: nothing left to show.
        if (IsDisposed || _lstProfiles.SelectedItem is not ProfileItem selected || selected.Profile != p || LimitsKey(p) != key) return null;
        _modelInfo = info;
        _modelLimitsKey = key;
        // Kept with the profile for the next session, with the probes of the same model; saved with the next Salva, not on its own.
        if (info != null)
        {
            var cache = CacheFor(p);
            cache.CheckedUtc = DateTime.UtcNow;
            cache.ContextWindow = info.ContextWindow;
            cache.Capabilities = info.Capabilities;
        }
        RefillPresets();
        ShowModelLimits();
        ResetWebSupport();
        return info == null ? "Il servizio non pubblica le capacità di questo modello: usa «Prova funzioni»." : null;
    }

    private void LstProfiles_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_refreshingList || _lstProfiles.SelectedItem is not ProfileItem item) return;
        _probeRun?.Cancel();
        _connectionCheck?.Cancel();
        _capabilityRefresh?.Cancel();
        SetProbeStatus(ProbeIdle, Tone.Muted);

        bool wasLoading = _isLoading;
        _isLoading = true;
        try
        {
            var p = item.Profile;
            var caps = ProviderCatalog.Get(p.Type);
            _txtProfileId.Text = p.Id;
            _txtDisplayName.Text = p.DisplayName;
            _cmbProvider.Items.Clear();
            _cmbProvider.Items.AddRange(ProviderCatalog.All.Cast<object>().ToArray());
            if (ProviderCatalog.IsLegacy(p.Type)) _cmbProvider.Items.Add(caps);
            _cmbProvider.SelectedItem = caps;
            _txtEndpoint.Text = p.BaseUrl;
            _txtModel.Text = p.Model;
            _txtToken.Text = _inMemorySecrets.TryGetValue(p.Id, out var secret) ? secret : string.Empty;
            _txtWebSearchKey.Text = _inMemoryWebSecrets.TryGetValue(p.Id, out var webSecret) ? webSecret : string.Empty;
            _txtWebSearchKey.Enabled = p.Type == ProviderCatalog.Ollama;
            SetRowVisible(_txtWebSearchKey, caps.WebSearch == WebSearchMode.OllamaCom);

            FillProviderLists(caps);
            _cmbAuthType.SelectedItem = caps.AuthTypes.FirstOrDefault(a => a.Equals(p.AuthType, StringComparison.OrdinalIgnoreCase)) ?? caps.AuthTypes[0];

            SetNumeric(_numNumCtx, p.NumCtx);
            SetNumeric(_numMaxTokens, p.MaxOutputTokens);
            SetNumeric(_numBudget, p.ContextBudgetTokens);
            SetNumeric(_numTemp, (decimal)p.Temperature);
            _txtKeepAlive.Text = p.KeepAlive;
            _chkDisableThinking.Checked = p.DisableThinking;
            SetNumeric(_numFirstTokenTimeout, p.FirstTokenTimeoutSeconds);
            SetNumeric(_numIdleTimeout, p.IdleTimeoutSeconds);
            _chkUseProxy.Checked = p.UseSystemProxy;
            _chkExternal.Checked = p.IsExternal;
            foreach (CheckBox box in _webActions.Controls)
                box.Checked = (p.WebSearchActions ?? new List<string>()).Contains((string)box.Tag!, StringComparer.OrdinalIgnoreCase);
            _chkAllowAttachments.Checked = p.AllowAttachmentUpload;
            _chkPseudonymizeAmounts.Checked = p.IsExternal || p.PseudonymizeAmounts;
            _chkPseudonymizeDates.Checked = p.IsExternal || p.PseudonymizeDates;
            _cmbExposure.SelectedItem = _cmbExposure.Items.Cast<ExposureItem>().First(i => i.Level == p.Exposure);
            _cmbRegion.SelectedItem = _cmbRegion.Items.Cast<RegionItem>().First(i => i.Region == p.ServiceRegion);
            ShowExposure();
            SelectMatchingPreset();

            SetTestResult("", Tone.Normal);
        }
        finally
        {
            _isLoading = wasLoading;
        }

        ShowModelLimits();
        ResetWebSupport();
        _ = LoadModelLimitsAsync(item.Profile);
        ApplyCapabilities();
        UpdateBudgetHint();
    }

    private void OnProfileFieldChanged()
    {
        if (_isLoading) return;
        _connectionCheck?.Cancel();
        _capabilityRefresh?.Cancel();
        UpdateSelectedProfileFromUi();
        MarkDirty();
    }

    /// <summary>Provider switch: propose its defaults where the field still holds the old provider's default.</summary>
    private void OnProviderChanged()
    {
        if (_isLoading || _lstProfiles.SelectedItem is not ProfileItem item) return;

        var oldCaps = ProviderCatalog.Get(item.Profile.Type);
        var caps = CurrentCaps();
        if (oldCaps == caps) return;

        _isLoading = true;
        try
        {
            if (string.IsNullOrWhiteSpace(_txtEndpoint.Text) || _txtEndpoint.Text.Trim() == oldCaps.DefaultBaseUrl)
            {
                _txtEndpoint.Text = caps.DefaultBaseUrl;
            }
            if (string.IsNullOrWhiteSpace(_txtModel.Text) || _txtModel.Text.Trim() == oldCaps.DefaultModel)
            {
                _txtModel.Text = caps.DefaultModel;
            }

            foreach (var legacy in _cmbProvider.Items.Cast<ProviderCapabilities>().Where(p => ProviderCatalog.IsLegacy(p.Type)).ToArray())
                _cmbProvider.Items.Remove(legacy);
            var auth = _cmbAuthType.SelectedItem as string;
            FillProviderLists(caps);
            _cmbAuthType.SelectedItem = auth != null && caps.AuthTypes.Contains(auth) ? auth : caps.AuthTypes[0];

            if (caps.IsExternal) _chkExternal.Checked = true;
            else if (oldCaps.IsExternal)
            {
                _chkExternal.Checked = false;
                item.Profile.External = false;
            }

            if (caps.SupportsNumCtx) ApplyPreset(ProviderCatalog.Presets.First(p => p.ContextWindow == 16384));
            item.Profile.ModelCapabilities = null;
            _chkWebSearch.Checked = false;
            item.Profile.WebSearchEnabled = false;
        }
        finally
        {
            _isLoading = false;
        }

        UpdateSelectedProfileFromUi();
        ApplyCapabilities();
        UpdateBudgetHint();
        MarkDirty();
        InvalidateWebSupport();
    }

    private void FillProviderLists(ProviderCapabilities caps)
    {
        _cmbAuthType.Items.Clear();
        _cmbAuthType.Items.AddRange(caps.AuthTypes.Cast<object>().ToArray());

        RefillPresets(caps);
        Tip(_cmbProvider, caps.DisplayName + (caps.IsExternal ? " (cloud)" : caps.Type == ProviderCatalog.OpenAICompatible ? " (destinazione dall'indirizzo)" : " (locale)") +
            (caps.Type == ProviderCatalog.OpenAICompatible ? "\nInserisci l'indirizzo. Lettura delle capacità specifiche e ricerca web non disponibili." : "") +
            "\nSi applica con Salva. Documentazione: " + caps.DocsUrl);
    }

    /// <summary>Presets only for a fixed client-side window, and only those within the model's declared maximum.</summary>
    private void RefillPresets(ProviderCapabilities? caps = null)
    {
        caps ??= CurrentCaps();
        var selected = _cmbPreset.SelectedItem;
        bool syncing = _syncingContext;
        _syncingContext = true;
        try
        {
            _cmbPreset.Items.Clear();
            if (caps.SupportsNumCtx)
            {
                int window = CurrentModelInfo()?.ContextWindow ?? 0;
                _cmbPreset.Items.AddRange(ProviderCatalog.PresetsFor(window).Cast<object>().ToArray());
            }
            if (selected != null && _cmbPreset.Items.Contains(selected)) _cmbPreset.SelectedItem = selected;
            else SelectMatchingPreset();
        }
        finally
        {
            _syncingContext = syncing;
        }
    }

    /// <summary>Enables only the fields the provider actually uses.</summary>
    private void ApplyCapabilities()
    {
        var caps = CurrentCaps();
        _cmbPreset.Enabled = _numNumCtx.Enabled = _numMaxTokens.Enabled = _numBudget.Enabled = caps.SupportsNumCtx;
        _txtKeepAlive.Enabled = caps.SupportsKeepAlive;
        _chkDisableThinking.Enabled = caps.SupportsThinkToggle && CurrentCapabilities()?.Reasoning != CapabilityState.Unsupported;
        _cmbAuthType.Enabled = caps.AuthTypes.Count > 1;
        bool usesSecret = !string.Equals(_cmbAuthType.SelectedItem as string, "none", StringComparison.OrdinalIgnoreCase);
        _txtToken.Enabled = usesSecret;
        _chkShowToken.Enabled = usesSecret;
        // No key to type with «none»: the row goes away instead of showing a disabled field.
        SetRowVisible(_tokenRow, usesSecret);
        bool remote = Uri.TryCreate(_txtEndpoint.Text.Trim(), UriKind.Absolute, out var endpoint) && !ConfigurationValidator.IsLoopback(endpoint);
        if (_lstProfiles.SelectedItem is ProfileItem selected)
        {
            bool loading = _isLoading;
            _isLoading = true;
            try { _chkExternal.Checked = selected.Profile.IsExternal; }
            finally { _isLoading = loading; }
        }
        _chkExternal.Enabled = !caps.IsExternal && !remote;
        _cmbExposure.Enabled = _chkExternal.Checked;
        _cmbRegion.Enabled = _chkExternal.Checked;
        _chkPseudonymizeAmounts.Enabled = _chkExternal.Checked;
        _chkPseudonymizeDates.Enabled = _chkExternal.Checked;
        _chkAllowAttachments.Enabled = _chkExternal.Checked && ExposurePolicy.For(SelectedExposure).Attachments;
        ShowExposure();
        _txtWebSearchKey.Enabled = caps.WebSearch == WebSearchMode.OllamaCom;
        SetRowVisible(_txtWebSearchKey, caps.WebSearch == WebSearchMode.OllamaCom);
        if (_isLocked)
        {
            // Another profile shown while locked: unlocking must restore the states of this profile, not of the one locked first.
            foreach (var control in new Control[] { _cmbPreset, _numNumCtx, _numMaxTokens, _numBudget, _txtKeepAlive, _chkDisableThinking,
                         _cmbAuthType, _txtToken, _chkShowToken, _chkExternal, _cmbExposure, _cmbRegion, _chkAllowAttachments, _chkPseudonymizeAmounts, _chkPseudonymizeDates, _txtWebSearchKey })
                _lockedControls[control] = control.Enabled;
            foreach (var page in _pages) LockInputs(page);
        }
    }

    /// <summary>Caller must hold _isLoading or _syncingContext.</summary>
    private void ApplyPreset(ContextPreset preset)
    {
        _cmbPreset.SelectedItem = preset;
        if (preset.IsCustom) return;

        SetNumeric(_numNumCtx, preset.ContextWindow);
        SetNumeric(_numMaxTokens, preset.MaxOutputTokens);
        SetNumeric(_numBudget, preset.Budget);
    }

    private void OnPresetChanged()
    {
        if (_isLoading || _syncingContext || _cmbPreset.SelectedItem is not ContextPreset preset || preset.IsCustom) return;

        _syncingContext = true;
        try
        {
            ApplyPreset(preset);
        }
        finally
        {
            _syncingContext = false;
        }

        UpdateSelectedProfileFromUi();
        UpdateBudgetHint();
        MarkDirty();
    }

    /// <summary>Manual edit: Ollama budget follows num_ctx − output − margin; preset falls back to custom when no longer matching.</summary>
    private void OnContextValueChanged(bool recomputeBudget)
    {
        if (_isLoading || _syncingContext) return;

        _syncingContext = true;
        try
        {
            if (recomputeBudget && CurrentCaps().SupportsNumCtx)
            {
                SetNumeric(_numBudget, ProviderCatalog.ComputeBudget((int)_numNumCtx.Value, (int)_numMaxTokens.Value));
            }
            UpdateSelectedProfileFromUi();
            SelectMatchingPreset();
        }
        finally
        {
            _syncingContext = false;
        }

        UpdateBudgetHint();
        MarkDirty();
    }

    private void UpdateBudgetHint()
    {
        if (!CurrentCaps().SupportsNumCtx)
        {
            _lblBudgetHint.Visible = false;
            return;
        }

        int numCtx = (int)_numNumCtx.Value;
        int output = (int)_numMaxTokens.Value;
        int suggested = ProviderCatalog.ComputeBudget(numCtx, output);
        bool overflows = (int)_numBudget.Value + output > numCtx;
        Tip(_numBudget, $"Solo Ollama: consigliati {suggested} token. Email e allegati devono entrare interi; altrimenti il riquadro si ferma. Si applica con Salva.");
        _lblBudgetHint.Visible = overflows;
        if (overflows)
        {
            Theme.SetTone(_lblBudgetHint, Tone.Danger);
            _lblBudgetHint.Text = $"Budget + output superano NumCtx (consigliato {suggested})";
        }
    }

    private void UpdateSelectedProfileFromUi()
    {
        // While the editor is being filled from a profile, field change events must not write
        // half-populated UI values back into it (this used to wipe BaseUrl/Model).
        if (_isLoading) return;

        if (_lstProfiles.SelectedItem is ProfileItem item)
        {
            var caps = CurrentCaps();
            var p = item.Profile;
            string? previousPolicy = _connectionCheck != null || _capabilityRefresh != null ? ConversationPolicy.Key(p) : null;
            p.DisplayName = _txtDisplayName.Text.Trim();
            p.Type = caps.Type;
            p.BaseUrl = _txtEndpoint.Text.Trim();
            p.Model = _txtModel.Text.Trim();
            p.AuthType = _cmbAuthType.SelectedItem as string ?? caps.AuthTypes[0];
            p.ContextBudgetTokens = (int)_numBudget.Value;
            p.NumCtx = (int)_numNumCtx.Value;
            p.MaxOutputTokens = (int)_numMaxTokens.Value;
            p.Temperature = (double)_numTemp.Value;
            p.KeepAlive = _txtKeepAlive.Text.Trim();
            p.DisableThinking = _chkDisableThinking.Checked;
            p.FirstTokenTimeoutSeconds = (int)_numFirstTokenTimeout.Value;
            p.IdleTimeoutSeconds = (int)_numIdleTimeout.Value;
            p.UseSystemProxy = _chkUseProxy.Checked;
            p.AllowAttachmentUpload = p.IsExternal && _chkAllowAttachments.Checked;
            p.Exposure = SelectedExposure;
            p.ServiceRegion = SelectedRegion;
            p.PseudonymizeAmounts = _chkPseudonymizeAmounts.Checked;
            p.PseudonymizeDates = _chkPseudonymizeDates.Checked;
            if (_chkWebSearch.Enabled) p.WebSearchEnabled = _chkWebSearch.Checked;
            p.WebSearchActions = _webActions.Controls.Cast<CheckBox>().Where(b => b.Checked).Select(b => (string)b.Tag!).ToList();
            if (previousPolicy != null && previousPolicy != ConversationPolicy.Key(p))
            {
                _connectionCheck?.Cancel();
                _capabilityRefresh?.Cancel();
            }
        }
    }

    private void BtnAddProfile_Click(object? sender, EventArgs e)
    {
        int n = _config.Profiles.Count + 1;
        while (_config.Profiles.Any(p => string.Equals(p.Id, "profile-" + n, StringComparison.OrdinalIgnoreCase))) n++;
        string newId = "profile-" + n;
        // LlmProfileConfig defaults = local Ollama from ProviderCatalog.
        var newProfile = new LlmProfileConfig { Id = newId, DisplayName = "Nuovo Profilo (" + newId + ")" };
        _config.Profiles.Add(newProfile);

        var item = new ProfileItem(newProfile, IsDefaultProfile);
        _lstProfiles.Items.Add(item);
        _lstProfiles.SelectedItem = item;
        RefreshProfileNames();
        LogActivity($"Aggiunto nuovo profilo '{newId}'.");
        MarkDirty();
    }

    private void BtnRemoveProfile_Click(object? sender, EventArgs e)
    {
        if (_lstProfiles.SelectedItem is ProfileItem item)
        {
            if (_config.Profiles.Count <= 1)
            {
                CopyableMessageBox.Show(this, "È necessario mantenere almeno un profilo configurato.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (CopyableMessageBox.Show(this, $"Rimuovere il profilo «{item.Name}»? Le sue chiavi vengono eliminate dal vault al prossimo Salva; senza salvare non cambia nulla.",
                    "Rimuovi profilo", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            _config.Profiles.Remove(item.Profile);
            _inMemorySecrets.Remove(item.Profile.Id);
            _inMemoryWebSecrets.Remove(item.Profile.Id);
            // The default must always point to an existing profile.
            if (IsDefaultProfile(item.Profile)) _config.DefaultProfile = _config.Profiles[0].Id;

            int idx = _lstProfiles.SelectedIndex;
            _lstProfiles.Items.Remove(item);
            if (_lstProfiles.Items.Count > 0)
            {
                _lstProfiles.SelectedIndex = Math.Min(idx, _lstProfiles.Items.Count - 1);
            }
            RefreshProfileNames();

            LogActivity($"Rimosso profilo '{item.Profile.Id}'.");
            MarkDirty();
        }
    }

    // async void handlers (the menu pick too): nothing may escape, or the ConfigTool process ends.
    private async void BtnDetectModels_Click(object? sender, EventArgs e)
    {
        if (_lstProfiles.SelectedItem is not ProfileItem item) return;

        _btnDetectModels.Enabled = false;
        _btnDetectModels.Text = "Rilevamento...";
        try
        {
            UpdateSelectedProfileFromUi();
            var p = item.Profile;
            string? secret = _inMemorySecrets.TryGetValue(p.Id, out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
            await ModelPicker.PickAsync(_btnDetectModels, p, secret, async m =>
            {
                try
                {
                    _txtModel.Text = m;
                    await LoadModelLimitsAsync(p);
                }
                catch (Exception ex) { if (!IsDisposed) LogActivity($"Modello '{m}' non applicato: {ex.Message}"); }
            });
        }
        catch (Exception ex) { if (!IsDisposed) LogActivity($"Rilevamento dei modelli non riuscito: {ex.Message}"); }
        finally
        {
            if (!IsDisposed)
            {
                if (_isLocked) _lockedControls[_btnDetectModels] = true;
                _btnDetectModels.Enabled = !_isLocked;
                _btnDetectModels.Text = DetectModelsText;
            }
        }
    }

    /// <summary>
    /// Offline: whether Ricerca web can be ticked. A capability the provider declares (Anthropic server tools,
    /// Ollama tool calling with an ollama.com key) enables it at once; otherwise only the real check does.
    /// </summary>
    private void ResetWebSupport()
    {
        _webSupportCheck?.Cancel();
        if (_lstProfiles.SelectedItem is not ProfileItem item) return;
        var caps = CurrentCaps();
        bool saved = item.Profile.WebSearchEnabled;
        bool loading = _isLoading;
        _isLoading = true;
        _chkWebSearch.Checked = saved;
        _isLoading = loading;

        var declared = CurrentCapabilities()?.WebSearch ?? CapabilityState.Unknown;
        if (caps.WebSearch == WebSearchMode.OllamaCom && CurrentCapabilities()?.Tools == CapabilityState.Supported)
            declared = _inMemoryWebSecrets.TryGetValue(item.Profile.Id, out var key) && !string.IsNullOrWhiteSpace(key) ? CapabilityState.Supported : CapabilityState.Unknown;
        // A real check of this model, endpoint and key (Verifica Ricerca web or Prova funzioni) wins over the declaration.
        var verified = item.Profile.ModelCapabilities is { } cache && cache.Matches(item.Profile) ? cache.Probe(ProbeKind.Web) : null;
        if (verified?.Outcome == ProbeOutcome.Supported) declared = CapabilityState.Supported;
        else if (verified?.Outcome == ProbeOutcome.Unsupported) declared = CapabilityState.Unsupported;
        bool available = caps.WebSearch != WebSearchMode.None;
        bool usable = available && (saved || declared == CapabilityState.Supported);

        if (_isLocked) { _lockedControls[_chkWebSearch] = usable; _lockedControls[_btnWebRetry] = available; }
        else { _chkWebSearch.Enabled = usable; _btnWebRetry.Enabled = available; }
        _lblWebSupport.Text = !available ? "Non disponibile con questo connettore"
            : declared == CapabilityState.Unsupported ? "Non supportato dal modello"
            : saved ? "Supportato"
            : declared == CapabilityState.Supported ? (verified != null ? "Supportato (verificato il " + verified.CheckedUtc.ToLocalTime().ToString("dd/MM/yyyy") + ")" : "Supportato (dichiarato dal provider)")
            : WebNotChecked;
        Theme.SetTone(_lblWebSupport, usable ? Tone.Success : declared == CapabilityState.Unsupported || !available ? Tone.Muted : Tone.Warning);
        Tip(_lblWebSupport, available ? WebSearchService.Disclosure(item.Profile) : "Il servizio scelto non espone una ricerca web utilizzabile dall'add-in.");
        UpdateWebActionsEnabled();
        _txtWebSearchKey.Enabled = !_isLocked && caps.WebSearch == WebSearchMode.OllamaCom;
    }

    private void UpdateWebActionsEnabled()
    {
        bool on = _chkWebSearch.Checked && (_isLocked ? _lockedControls.TryGetValue(_chkWebSearch, out var enabled) && enabled : _chkWebSearch.Enabled);
        // An external profile searches only for the actions its exposure level permits.
        var policy = _chkExternal.Checked ? ExposurePolicy.For(SelectedExposure) : ExposurePolicy.Local;
        foreach (Control box in _webActions.Controls)
        {
            bool allowed = on && policy.AllowsWeb((string)box.Tag!);
            if (_isLocked) _lockedControls[box] = allowed;
            else box.Enabled = allowed;
        }
    }

    /// <summary>Model, endpoint or credentials changed: an earlier check no longer applies.</summary>
    private void InvalidateWebSupport()
    {
        if (_isLoading) return;
        if (_lstProfiles.SelectedItem is ProfileItem item) item.Profile.WebSearchEnabled = false;
        ResetWebSupport();
    }

    private async System.Threading.Tasks.Task CheckWebSupportAsync()
    {
        if (_lstProfiles.SelectedItem is not ProfileItem item || _isLoading) return;
        UpdateSelectedProfileFromUi();
        var profile = item.Profile;
        var model = profile.Model;
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(profile.BaseUrl))
        {
            CopyableMessageBox.Show(this, "Inserisci endpoint e modello prima della verifica.", "Verifica Ricerca web", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var modelSecret = _inMemorySecrets.TryGetValue(profile.Id, out var token) && !string.IsNullOrWhiteSpace(token) ? token : null;
        var webSecret = _inMemoryWebSecrets.TryGetValue(profile.Id, out var key) && !string.IsNullOrWhiteSpace(key) ? key : null;
        string identity = CapabilityKey(profile);
        _webSupportCheck?.Cancel();
        var cts = new System.Threading.CancellationTokenSource(WebSearchService.CheckTimeout + TimeSpan.FromSeconds(5));
        _webSupportCheck = cts;
        _btnWebRetry.Enabled = false;
        _lblWebSupport.Text = "Verifica in corso…";
        Theme.SetTone(_lblWebSupport, Tone.Warning);
        try
        {
            var (supported, detail) = await WebProbeAsync(profile, modelSecret, webSecret, cts.Token);
            if (cts.IsCancellationRequested || IsDisposed || Disposing || _webSupportCheck != cts || _lstProfiles.SelectedItem != item || CapabilityKey(profile) != identity) return;
            var outcome = supported switch { true => ProbeOutcome.Supported, false => ProbeOutcome.Unsupported, _ => ProbeOutcome.Inconclusive };
            // The same check as «Prova funzioni»: its outcome is shown in Avanzate too.
            CacheFor(profile).Record(new CapabilityProbeResult { Kind = ProbeKind.Web, Outcome = outcome, Detail = detail, CheckedUtc = DateTime.UtcNow });
            MarkDirty();
            RefreshCapabilityRows();
            ShowWebSupport(profile, outcome, detail);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !Disposing && _webSupportCheck == cts)
            {
                _lblWebSupport.Text = "Errore di verifica: tempo di attesa scaduto";
                LogActivity($"[WEB SEARCH] Profilo '{profile.Id}', modello '{model}': {_lblWebSupport.Text}");
            }
        }
        finally
        {
            if (_webSupportCheck == cts) _webSupportCheck = null;
            cts.Dispose();
            if (!IsDisposed)
            {
                if (_isLocked) _lockedControls[_btnWebRetry] = true;
                else _btnWebRetry.Enabled = true;
            }
        }
    }

    /// <summary>Outcome of a real web check in Autorizzazioni: only a verified search allows ticking Ricerca web.</summary>
    private void ShowWebSupport(LlmProfileConfig profile, ProbeOutcome outcome, string detail)
    {
        _lblWebSupport.Text = outcome == ProbeOutcome.Supported ? "Supportato" : outcome == ProbeOutcome.Unsupported ? "Non supportato" : "Errore di verifica: " + detail;
        Theme.SetTone(_lblWebSupport, outcome == ProbeOutcome.Supported ? Tone.Success : outcome == ProbeOutcome.Unsupported ? Tone.Muted : Tone.Danger);
        LogActivity($"[WEB SEARCH] Profilo '{profile.Id}', modello '{profile.Model}': {_lblWebSupport.Text}");
        _lblWebSupport.MaximumSize = new Size(Ui.Px(_lblWebSupport, 480), 0);
        bool supported = outcome == ProbeOutcome.Supported;
        if (_isLocked) _lockedControls[_chkWebSearch] = supported;
        else _chkWebSearch.Enabled = supported;
        if (!supported && profile.WebSearchEnabled)
        {
            profile.WebSearchEnabled = false;
            bool loading = _isLoading;
            _isLoading = true;
            _chkWebSearch.Checked = false;
            _isLoading = loading;
            MarkDirty();
        }
        UpdateWebActionsEnabled();
    }

    private async void BtnTestProfile_Click(object? sender, EventArgs e)
    {
        if (_connectionCheck != null) { _connectionCheck.Cancel(); return; }
        if (_lstProfiles.SelectedItem is not ProfileItem item) return;
        UpdateSelectedProfileFromUi();
        var p = item.Profile;
        string identity = ConversationPolicy.Key(p);
        string? secret = _inMemorySecrets.TryGetValue(p.Id, out var s) ? s : null;
        using var run = new InteractiveRequest();
        _connectionCheck = run;
        string label = _btnTestProfile.Text;
        _btnTestProfile.Text = "Annulla verifica";
        SetTestResult("Verifica in corso (massimo 10 secondi)…", Tone.Warning, StatusLight.Working);
        bool Current() => !IsDisposed && ReferenceEquals(_lstProfiles.SelectedItem, item) && identity == ConversationPolicy.Key(p);
        try
        {
            var client = (ConnectionClientFactory ?? ((profile, key) => LlmClientFactory.CreateClient(profile, secret: key)))(p, secret);
            try
            {
                var sw = Stopwatch.StartNew();
                var health = await client.CheckHealthAsync(run.Token);
                run.Token.ThrowIfCancellationRequested();
                if (!Current()) return;
                if (health.IsHealthy)
                {
                    SetTestResult($"OK ({sw.ElapsedMilliseconds} ms, {health.AvailableModels.Count} modelli)", Tone.Success);
                    await LoadModelLimitsCoreAsync(p, run.Token);
                    run.Token.ThrowIfCancellationRequested();
                }
                else SetTestResult("Fallito: " + health.ErrorMessage, Tone.Danger);
            }
            finally { (client as IDisposable)?.Dispose(); }
        }
        catch (OperationCanceledException)
        {
            if (Current()) SetTestResult(run.TimedOut ? "Tempo massimo di 10 secondi superato." : "Verifica annullata.", Tone.Warning);
        }
        catch (Exception ex)
        {
            if (Current()) SetTestResult("Errore: " + ErrorText.For(ex), Tone.Danger);
            if (!IsDisposed) LogActivity("[TEST ERROR] " + ex.Message);
        }
        finally
        {
            _connectionCheck = null;
            if (!IsDisposed) { _btnTestProfile.Text = label; _btnTestProfile.Enabled = !_isLocked; }
        }
    }
}
