using System.Collections.Generic;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// Web sources [Wn] and the cut-input notice (A44) of the pane's running turn. A search started in parallel can
/// finish after its turn ended: its result must not show up in the next turn (A155). COM-free so tests can drive it.
/// </summary>
internal sealed class TurnWebSources
{
    private int _turn;

    public List<WebSource> Sources { get; private set; } = new();

    public bool InputCut { get; private set; }

    public void Begin(int turn)
    {
        _turn = turn;
        Sources = new List<WebSource>();
        InputCut = false;
    }

    /// <summary>False, and nothing changes, when the result belongs to a turn that already ended.</summary>
    public bool Take(int turn, List<WebSource> sources, bool inputCut = false)
    {
        if (turn != _turn) return false;
        Sources = sources;
        InputCut |= inputCut;
        return true;
    }
}
