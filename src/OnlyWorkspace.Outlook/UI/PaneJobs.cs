using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// The pane's running turn as the page last saw it: a page loaded again (WebView2 restarted) gets these messages
/// back and shows the turn going on, instead of an empty conversation while the host keeps working.
/// COM-free, UI thread only, so tests can drive it.
/// </summary>
internal sealed class PaneJobReplay
{
    private object? _start;
    private object? _context;
    private object? _phase;
    private object? _security;
    private object? _interpretation;
    private bool _cloud;
    private readonly StringBuilder _text = new();

    public bool Running => _start != null;
    /// <summary>Name of the running action (stream_start «action», or «Chat»).</summary>
    public string Action { get; private set; } = string.Empty;
    public int Done { get; private set; }
    public int Total { get; private set; }

    public void Record(object message)
    {
        switch (Field(message, "type") as string)
        {
            case "stream_start":
                _start = message;
                _context = _phase = _security = _interpretation = null;
                _cloud = false;
                _text.Clear();
                Action = Field(message, "action") as string is { Length: > 0 } action ? action : Field(message, "kind") as string == "chat" ? "Chat" : "Operazione";
                Done = Total = 0;
                break;
            case "stream_context" when _start != null: _context = message; break;
            case "stream_interpretation" when _start != null: _interpretation = message; break;
            case "stream_phase" when _start != null:
                _phase = message;
                if (Field(message, "total") is int total) { Total = total; Done = Field(message, "done") as int? ?? 0; }
                break;
            case "stream_chunk" when _start != null: _text.Append(Field(message, "chunk") as string); break;
            // The latest security report replaces the provisional one: a reloaded page shows it once.
            case "security_report" when _start != null: _security = message; break;
            case "cloud_used" when _start != null: _cloud = true; break;
            case "stream_end":
            case "stream_error":
            case "stream_cancelled":
                ClearTurn();
                break;
        }
    }

    /// <summary>An operation that ended without a closing message (superseded, window closing) leaves nothing to replay.</summary>
    public void ClearTurn()
    {
        _start = _context = _phase = _security = _interpretation = null;
        _cloud = false;
        _text.Clear();
        Action = string.Empty;
        Done = Total = 0;
    }

    /// <summary>Messages that rebuild what runs, in their original order; null when nothing runs.</summary>
    public object? Restore()
    {
        var messages = new List<object>();
        if (_start != null)
        {
            messages.Add(_start);
            if (_context != null) messages.Add(_context);
            if (_interpretation != null) messages.Add(_interpretation);
            if (_cloud) messages.Add(new { type = "cloud_used" });
            if (_phase != null) messages.Add(_phase);
            if (_security != null) messages.Add(_security);
            if (_text.Length > 0) messages.Add(new { type = "stream_chunk", chunk = _text.ToString() });
        }
        return messages.Count == 0 ? null : new { type = "job_restore", turnId = _start == null ? null : Field(_start, "turnId"), messages };
    }

    private static object? Field(object message, string name) =>
        message is IDictionary<string, object?> map ? (map.TryGetValue(name, out var value) ? value : null)
            : message.GetType().GetProperty(name)?.GetValue(message);
}

/// <summary>One pane's running work, as other Outlook windows show it.</summary>
internal sealed class PaneJob
{
    public int Id { get; set; }
    /// <summary>«finestra principale» or «finestra di un'email».</summary>
    public string Window { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public int Done { get; set; }
    public int Total { get; set; }
}

/// <summary>
/// Work running in the panes of every Outlook window (one pane per Explorer or Inspector): each pane lists the
/// others' in its job bar and can stop them, so an action started in an email's window is not lost from sight.
/// UI thread only; COM-free.
/// </summary>
internal static class PaneJobs
{
    private sealed class Entry
    {
        public string Window = string.Empty;
        public Func<PaneJobReplay> Replay = null!;
        public Action Stop = null!;
    }

    private static readonly Dictionary<int, Entry> Entries = new();
    private static int _nextId;

    /// <summary>Raised when work starts, ends or advances in a pane: each pane redraws the others' work.</summary>
    public static event Action? Changed;

    public static int Register(string window, Func<PaneJobReplay> replay, Action stop)
    {
        int id = ++_nextId;
        Entries[id] = new Entry { Window = window, Replay = replay, Stop = stop };
        return id;
    }

    public static void Unregister(int id)
    {
        if (Entries.Remove(id)) Changed?.Invoke();
    }

    public static void Notify() => Changed?.Invoke();

    /// <summary>Work running in the other panes.</summary>
    public static IReadOnlyList<PaneJob> Others(int self) => Entries
        .Where(e => e.Key != self)
        .SelectMany(e =>
        {
            var replay = e.Value.Replay();
            var jobs = new List<PaneJob>();
            if (replay.Running) jobs.Add(new PaneJob { Id = e.Key, Window = e.Value.Window, Action = replay.Action, Done = replay.Done, Total = replay.Total });
            return jobs;
        }).ToList();

    public static bool Stop(int id)
    {
        if (!Entries.TryGetValue(id, out var entry)) return false;
        entry.Stop();
        return true;
    }

    /// <summary>Interrompi from a window with nothing running: the work of the other windows stops.</summary>
    public static int StopOthers(int self)
    {
        int stopped = 0;
        foreach (var job in Others(self).Select(j => j.Id).Distinct().ToList())
            if (Stop(job)) stopped++;
        return stopped;
    }
}
