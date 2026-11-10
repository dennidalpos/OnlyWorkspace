using System.Windows.Forms;

namespace OnlyWorkspace.ConfigTool.Services;

public enum CloseAction { Block, AskUnsaved, HideToTray, Close }

/// <summary>What closing the main window does, decided apart from the form so it can be tested without one.</summary>
public static class ClosePolicy
{
    /// <param name="busy">An elevated operation or a save-before-close is already running.</param>
    /// <param name="dirty">Edits are not saved yet.</param>
    public static CloseAction Decide(bool busy, bool dirty, bool hasTray, bool closingForExit, CloseReason reason)
    {
        if (busy) return CloseAction.Block;
        if (dirty) return CloseAction.AskUnsaved;
        if (hasTray && reason == CloseReason.UserClosing && !closingForExit) return CloseAction.HideToTray;
        return CloseAction.Close;
    }
}
