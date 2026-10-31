using System.Text.Json;
using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

/// <summary>A page loaded again sees the running work; the other windows' panes list it and can stop it.</summary>
public sealed class PaneJobsTests
{
    private static string Json(object? value) => JsonSerializer.Serialize(value);

    [Fact]
    public void Replay_RebuildsTheRunningTurnInOrder_AndForgetsItWhenItEnds()
    {
        var replay = new PaneJobReplay();
        Assert.Null(replay.Restore());

        replay.Record(new { type = "stream_start", turnId = "1:7", kind = "action", action = "Riepilogo", prompt = "p" });
        replay.Record(new { type = "stream_context", entryId = "e1", subject = "Offerta" });
        replay.Record(new { type = "cloud_used" });
        replay.Record(new { type = "stream_phase", message = "Lettura", done = 2, total = 5 });
        replay.Record(new { type = "stream_chunk", chunk = "Prima " });
        replay.Record(new { type = "stream_chunk", chunk = "parte" });

        Assert.True(replay.Running);
        Assert.Equal(("Riepilogo", 2, 5), (replay.Action, replay.Done, replay.Total));
        var restore = Json(replay.Restore());
        Assert.Contains("\"type\":\"job_restore\"", restore);
        Assert.Contains("\"turnId\":\"1:7\"", restore);
        Assert.True(restore.IndexOf("stream_start") < restore.IndexOf("stream_context"));
        Assert.True(restore.IndexOf("stream_phase") < restore.IndexOf("Prima parte"));

        replay.Record(new Dictionary<string, object?> { ["type"] = "stream_end", ["latencyMs"] = 10L });
        Assert.False(replay.Running);
        Assert.Null(replay.Restore());
    }

    [Fact]
    public void Replay_KeepsOnlyTheLatestSecurityReport_BeforeTheText()
    {
        var replay = new PaneJobReplay();
        replay.Record(new { type = "security_report", report = new { outcome = "orphan" } });
        replay.Record(new { type = "stream_start", turnId = "1:9", kind = "action", action = "Verifica sicurezza" });
        replay.Record(new { type = "security_report", report = new { state = "running", outcome = "review" } });
        replay.Record(new { type = "stream_chunk", chunk = "Riferimenti web" });
        replay.Record(new { type = "security_report", report = new { state = "done", outcome = "suspicious" } });

        var restore = Json(replay.Restore());
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(restore, "security_report"));
        Assert.Contains("suspicious", restore);
        Assert.DoesNotContain("orphan", restore);
        Assert.True(restore.IndexOf("security_report") < restore.IndexOf("Riferimenti web"));

        replay.Record(new { type = "stream_cancelled", message = "Verifica interrotta" });
        Assert.Null(replay.Restore());
    }

    [Fact]
    public void Registry_ListsTheOtherWindowsWork_AndStopsIt()
    {
        var mainReplay = new PaneJobReplay();
        var emailReplay = new PaneJobReplay();
        int stopped = 0;
        int main = PaneJobs.Register("finestra principale", () => mainReplay, () => { });
        int email = PaneJobs.Register("finestra di un'email", () => emailReplay, () => stopped++);
        try
        {
            Assert.DoesNotContain(PaneJobs.Others(main), j => j.Id == email);
            emailReplay.Record(new { type = "stream_start", kind = "action", action = "Verifica sicurezza" });
            var job = Assert.Single(PaneJobs.Others(main), j => j.Id == email);
            Assert.Equal(("finestra di un'email", "Verifica sicurezza"), (job.Window, job.Action));
            Assert.DoesNotContain(PaneJobs.Others(email), j => j.Id == email);

            Assert.True(PaneJobs.Stop(email));
            Assert.Equal(1, stopped);
            Assert.True(PaneJobs.StopOthers(main) >= 1);
            Assert.Equal(2, stopped);
        }
        finally
        {
            PaneJobs.Unregister(main);
            PaneJobs.Unregister(email);
        }
        Assert.False(PaneJobs.Stop(email));
    }
}
