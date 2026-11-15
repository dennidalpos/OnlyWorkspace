using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>What the data-egress notice flow needs from the configuration window (#177).</summary>
internal interface IDataEgressHost
{
    WorkspaceConfig Config { get; }
    string WindowsUser { get; }
    string Machine { get; }
    /// <summary>True while the window is locked, the configuration failed to load or an elevated save is running.</summary>
    bool IsBusy { get; }
    /// <summary>True while the window is locked or the configuration failed to load.</summary>
    bool IsLockedOrBlocked { get; }
    bool IsDirty { get; }
    string? ResolveSecret(string reference);
    /// <summary>Takes the profile being edited and its secrets into the configuration before the notice is shown.</summary>
    void PrepareProfiles();
    /// <summary>Shows the notice; null when declined, else the profiles whose unspecified region was confirmed.</summary>
    IReadOnlyList<string>? ShowNotice(IReadOnlyList<LlmProfileConfig> profiles);
    bool Confirm(string message, string title, string? yes = null, string? no = null);
    Task<bool> SaveAsync();
    void MarkDirty();
    void RestoreDirty(bool dirty);
    void RefreshStatus();
    void LogActivity(string message);
}

/// <summary>
/// Acceptance and revocation of the data-egress notice (#177): what is recorded, the confirmations asked, and the undo of the
/// in-memory change when the save does not go through.
/// </summary>
internal sealed class DataEgressFlow
{
    private readonly IDataEgressHost _host;

    public DataEgressFlow(IDataEgressHost host) => _host = host;

    /// <summary>Opens the notice; an acceptance is recorded in the configuration being edited (saved by the caller).</summary>
    public bool Ask()
    {
        if (_host.IsBusy) return false;
        _host.PrepareProfiles();
        var profiles = DataEgress.EgressProfiles(_host.Config).ToList();
        if (profiles.Count == 0) return false;
        var confirmed = _host.ShowNotice(profiles);
        if (confirmed == null) return false;
        var consent = DataEgress.Accept(_host.Config, _host.WindowsUser, _host.Machine, DateTime.UtcNow, _host.ResolveSecret, confirmed);
        _host.LogActivity($"Informativa accettata, in attesa di salvataggio, da {consent.Describe()} per: " +
            string.Join(", ", consent.Providers.Select(p => $"{p.Host} ({ExposurePolicy.Label(p.Level)})")) + ".");
        _host.MarkDirty();
        _host.RefreshStatus();
        return true;
    }

    public async Task<bool> AcceptAndSaveAsync()
    {
        var previous = _host.Config.DataEgress?.Consent;
        int events = _host.Config.DataEgress?.History.Count ?? 0;
        bool dirty = _host.IsDirty;
        if (dirty && !_host.Confirm("«Leggi e autorizza» salva anche tutte le altre modifiche non ancora salvate.\n\nProseguire?", "Modifiche non salvate")) return false;
        if (!Ask()) return false;
        if (await _host.SaveAsync())
        {
            _host.LogActivity("Autorizzazioni per profilo registrate e salvate.");
            return true;
        }
        Restore(previous, events, dirty);
        return false;
    }

    private void Restore(DataEgressConsent? previous, int events, bool dirty)
    {
        var egress = _host.Config.DataEgress;
        egress.Consent = previous;
        egress.History.RemoveRange(events, egress.History.Count - events);
        DataEgress.BindProfiles(_host.Config, _host.ResolveSecret);
        _host.RestoreDirty(dirty);
        _host.RefreshStatus();
    }

    public async Task RevokeAsync()
    {
        if (_host.IsLockedOrBlocked || _host.Config.DataEgress?.Consent == null) return;
        if (!_host.Confirm("Revocare l'autorizzazione all'uscita dei dati?\n\nDopo il salvataggio i profili cloud e la ricerca web esterna si bloccano per tutti gli utenti di questo PC. " +
                "Le richieste già ricevute dai fornitori non si possono ritirare.", "Uscita dei dati", "Revoca", "Annulla")) return;
        await ApplyRevocationAsync();
    }

    /// <summary>Revokes and saves; a save that does not go through (UAC refused, write error) leaves the notice as it was.</summary>
    public async Task<bool> ApplyRevocationAsync()
    {
        var consent = _host.Config.DataEgress!.Consent;
        int events = _host.Config.DataEgress.History.Count;
        bool dirty = _host.IsDirty;
        DataEgress.Revoke(_host.Config, _host.WindowsUser, _host.Machine, DateTime.UtcNow);
        _host.MarkDirty();
        bool saved = await _host.SaveAsync();
        if (saved) _host.LogActivity("Informativa sull'uscita dei dati revocata.");
        else Restore(consent, events, dirty);
        _host.RefreshStatus();
        return saved;
    }
}
