using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Services;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>What the administrator-password flow needs from the configuration window (#177).</summary>
internal interface IAdminPasswordHost
{
    string ConfigDirectory { get; }
    bool IsLocked { get; }
    /// <summary>Password prompt; null when the user cancels.</summary>
    string? AskPassword(string title, string label);
    void Warn(string message, string title);
    void ShowError(string message, string title);
    Task<ElevatedConfigWriter.ElevationResult> RunElevatedAsync(string arguments, string? resultDir);
    void SetLocked(bool locked);
    void SetStatus(string message, Tone tone);
    void LogActivity(string message);
}

/// <summary>
/// Unlock, change and reset of the administrator password of config.json: the dialogs' wording, the checks on the new
/// password and the write, direct when the folder is writable and through the elevated instance (UAC) otherwise.
/// </summary>
internal sealed class AdminPasswordFlow
{
    private readonly AdminPasswordService _service;
    private readonly IAdminPasswordHost _host;

    public AdminPasswordFlow(AdminPasswordService service, IAdminPasswordHost host)
    {
        _service = service;
        _host = host;
    }

    /// <summary>The password dialog of the configuration window.</summary>
    public static string? ShowPasswordDialog(IWin32Window owner, string title, string label)
    {
        using var dialog = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Font = new Font("Segoe UI", 9F),
            BackColor = Theme.Colors.Window,
            ForeColor = Theme.Colors.Text
        };
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(16, 14, 16, 12) };
        var caption = new SelectableLabel { Text = label, AutoSize = true, MaximumSize = new Size(Metrics.TextWidthCaption, 0), Margin = new Padding(0, 0, 0, 6) };
        var input = new ThemedTextBox { Width = 360, UseSystemPasswordChar = true };
        var ok = Metrics.DialogButton(new FlatButton { Text = "OK", Kind = ButtonKind.Primary, DialogResult = DialogResult.OK });
        var cancel = Metrics.DialogButton(new FlatButton { Text = "Annulla", DialogResult = DialogResult.Cancel });
        cancel.Margin = Padding.Empty;
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 12, 0, 0) };
        actions.Controls.AddRange(new Control[] { ok, cancel });
        // Laid out right to left: Tab follows the reading order instead.
        cancel.TabIndex = 0;
        ok.TabIndex = 1;
        layout.Controls.Add(caption, 0, 0);
        layout.Controls.Add(new InputFrame(input) { Margin = Padding.Empty }, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        Ui.ScaleFromDesignDpi(dialog);
        dialog.Shown += (s, e) => { Theme.Apply(dialog); Theme.ApplyWindowChrome(dialog); input.Focus(); };
        var result = dialog.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
        input.Clear();
        return result;
    }

    public void Unlock()
    {
        var password = _host.AskPassword("Sblocca configurazione", "Password amministrativa:");
        if (password == null) return;
        if (!_service.Verify(password))
        {
            _host.Warn("Password non corretta.", "Configurazione bloccata");
            return;
        }
        _host.SetLocked(false);
        _host.SetStatus("Configurazione sbloccata per questa finestra.", Tone.Success);
    }

    private string? AskNewPassword()
    {
        var next = _host.AskPassword("Password amministrativa", "Nuova password (vuota per disattivarla, minimo 8 caratteri):");
        if (next == null) return null;
        if (next.Length > 0 && next.Length < AdminPasswordService.MinLength)
        {
            _host.Warn("La password deve contenere almeno " + AdminPasswordService.MinLength + " caratteri.", "Password");
            return null;
        }
        var confirm = _host.AskPassword("Conferma password", "Ripeti la nuova password:");
        if (confirm == null) return null;
        if (next != confirm)
        {
            _host.Warn("Le password non coincidono.", "Password");
            return null;
        }
        return next;
    }

    public async Task ManageAsync()
    {
        if (_host.IsLocked) return;
        if (_service.Enabled)
        {
            var current = _host.AskPassword("Modifica password", "Password amministrativa corrente:");
            if (current == null) return;
            if (!_service.Verify(current))
            {
                _host.Warn("Password non corretta.", "Password");
                return;
            }
        }
        var next = AskNewPassword();
        if (next == null) return;
        await ApplyAsync(next);
    }

    public async Task ResetAsync()
    {
        var next = AskNewPassword();
        if (next == null) return;
        await ApplyAsync(next, requireUac: true);
    }

    private async Task ApplyAsync(string next, bool requireUac = false)
    {
        string? staging = null;
        try
        {
            byte[]? record = next.Length == 0 ? null : AdminPasswordService.CreateRecord(next);
            if (!requireUac && ElevatedConfigWriter.CanWriteConfig(_host.ConfigDirectory))
            {
                _service.ApplyRecord(record);
            }
            else
            {
                staging = ElevatedConfigWriter.CreateStagingDirectory();
                if (record != null) File.WriteAllBytes(Path.Combine(staging, "verifier.json"), record);
                var applied = await _host.RunElevatedAsync(
                    "admin-password-apply " + ElevatedConfigWriter.Quote(staging) + " " + ElevatedConfigWriter.Quote(_host.ConfigDirectory), staging);
                if (applied.Cancelled)
                {
                    _host.SetStatus("Password non modificata: autorizzazione amministratore non concessa.", Tone.Warning);
                    _host.LogActivity("Password amministrativa non applicata: autorizzazione annullata dall'utente (UAC).");
                    return;
                }
                if (!applied.Success) throw new InvalidOperationException(applied.Message);
            }
            _host.SetLocked(false);
            _host.SetStatus(_service.Enabled ? "Password amministrativa aggiornata." : "Password amministrativa disattivata.", Tone.Success);
        }
        catch (Exception ex)
        {
            _host.LogActivity("Password amministrativa non applicata: " + ex.Message);
            _host.ShowError(ErrorText.For(ex), "Password amministrativa");
        }
        finally { ElevatedConfigWriter.DeleteStagingDirectory(staging); }
    }
}
