using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

/// <summary>
/// The evidence reading shared by the chat and Cerca e analizza (A150): an email past the whole-reading limit or
/// without its original stays out and is named, the others still answer.
/// </summary>
public sealed class MailEvidenceReaderTests
{
    private static ChatMailItem Mail(string subject, int day) =>
        new() { EntryId = subject, StoreId = "store", MessageId = "<" + subject + ">", Subject = subject, Received = new DateTime(2026, 10, day, 9, 0, 0) };

    [Fact]
    public async Task BodyPastTheLimit_StaysOutAndIsNamed_TheOthersAreRead()
    {
        var tracked = new List<string>();
        var progress = new List<(int Done, int Total)>();
        var reader = new MailEvidenceReader
        {
            Track = item => tracked.Add(item.Subject),
            Progress = (_, done, total) => progress.Add((done, total)),
            ReadBody = (item, _) => item.Subject switch
            {
                "Lunga" => throw new MailBodyTooLongException(),
                "Assente" => Task.FromResult<string?>(null),
                _ => Task.FromResult<string?>("Testo di " + item.Subject)
            }
        };
        var items = new[] { Mail("Terza", 22), Mail("Lunga", 21), Mail("Assente", 23), Mail("Prima", 20) };

        var reading = await reader.ReadAsync(items, attachmentsAllowed: true, budget: 0, new AnalysisStats(), CancellationToken.None);

        Assert.False(reading.Stopped);
        Assert.Equal(new[] { "Prima", "Terza" }, reading.Pack.Items.Select(e => e.Mail.Subject));
        Assert.All(reading.Pack.Items, e => Assert.True(e.Whole));
        Assert.Equal("«Lunga» del 21/10/2026", Assert.Single(reading.TooLong));
        Assert.Equal("«Assente» del 23/10/2026 09:00", Assert.Single(reading.Missing));
        Assert.Equal(new[] { "Prima", "Lunga", "Terza", "Assente" }, tracked);
        // The pane's counters end at 4/4, not at the last email started.
        Assert.Equal((4, 4), progress[^1]);
    }

    [Fact]
    public async Task EmailOverTheBudget_LeavesWithItsDocuments()
    {
        var documents = new AnalysisStats();
        var reader = new MailEvidenceReader
        {
            ReadBody = (item, _) => Task.FromResult<string?>(item.Subject == "Grande" ? new string('x', 4000) : "breve"),
            ReadAttachments = (item, progress, _) =>
            {
                progress("allegato.pdf");
                documents.Documents.Add(new DocumentOutcome { Name = item.Subject + ".pdf", Status = DocumentStatus.Read });
                return Task.FromResult("\n[allegato]");
            }
        };

        var reading = await reader.ReadAsync(new[] { Mail("Piccola", 20), Mail("Grande", 21) }, true, budget: 200, documents, CancellationToken.None);

        Assert.Equal("Piccola", Assert.Single(reading.Pack.Items).Mail.Subject);
        Assert.EndsWith("[allegato]", reading.Pack.Items[0].Text);
        Assert.Equal(new[] { "Piccola.pdf", "Grande.pdf" }, documents.Documents.Select(d => d.Name));
        Assert.All(documents.Documents, d => Assert.False(d.IncludedInRequest));
        Assert.Equal("«Grande» del 21/10/2026", Assert.Single(reading.TooLong));
    }

    [Fact]
    public async Task StoppedReading_KeepsWhatWasRead()
    {
        using var cts = new CancellationTokenSource();
        var reader = new MailEvidenceReader
        {
            ReadBody = (item, _) =>
            {
                if (item.Subject == "Seconda") cts.Cancel();
                return Task.FromResult<string?>("testo");
            }
        };

        var reading = await reader.ReadAsync(new[] { Mail("Prima", 20), Mail("Seconda", 21), Mail("Terza", 22) }, true, 0, new AnalysisStats(), cts.Token);

        Assert.True(reading.Stopped);
        Assert.Equal(new[] { "Prima", "Seconda" }, reading.Pack.Items.Select(e => e.Mail.Subject));
    }
}
