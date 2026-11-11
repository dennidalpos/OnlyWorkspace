using System.Collections.Generic;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;

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

    /// <summary>A search was requested but the model answered without running it (#200).</summary>
    public bool Skipped { get; private set; }

    public void Begin(int turn)
    {
        _turn = turn;
        Sources = new List<WebSource>();
        InputCut = false;
        Skipped = false;
    }

    /// <summary>False, and nothing changes, when the turn already ended.</summary>
    public bool MarkSkipped(int turn)
    {
        if (turn != _turn) return false;
        Skipped = true;
        return true;
    }

    /// <summary>Note shown under the answer for this turn, or null.</summary>
    public string? Notice()
    {
        if (InputCut && Skipped) return WebSearchService.InputCutNotice + "\n" + WebSearchService.SkippedNotice;
        return InputCut ? WebSearchService.InputCutNotice : Skipped ? WebSearchService.SkippedNotice : null;
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
