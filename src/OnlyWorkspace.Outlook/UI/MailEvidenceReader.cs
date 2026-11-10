using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>A body past the whole-reading limit: never cut, so the email stays out of the request and is named.</summary>
internal sealed class MailBodyTooLongException : InvalidOperationException
{
    public MailBodyTooLongException() : base("Il corpo supera il limite di lettura integrale; nessun testo è stato tagliato.") { }
}

/// <summary>What the evidence reading produced: the pack, plus what stayed out and why.</summary>
internal sealed class EvidenceReading
{
    public MailEvidencePack Pack = new();
    public List<string> TooLong = new();
    public List<string> Missing = new();
    /// <summary>Emails without usable facts when the profile does not permit body text.</summary>
    public List<string> NoFacts = new();
    /// <summary>Emails past the level's evidence budget.</summary>
    public List<string> OverBudget = new();
    public bool Stopped;
}

/// <summary>
/// Reads emails oldest first, from whole originals or permitted literal local facts. A source past a
/// per-email or cumulative budget stays out whole. No model-generated archive card is used as input.
/// COM-free: the pane supplies original and attachment reads on the Outlook UI thread.
/// </summary>
internal sealed class MailEvidenceReader
{
    /// <summary>The email becomes an input of the turn.</summary>
    public Action<ChatMailItem> Track = _ => { };

    /// <summary>Progress line, with the email's position.</summary>
    public Action<string, int, int> Progress = (_, _, _) => { };

    /// <summary>The whole body; null when the original is not available; <see cref="MailBodyTooLongException"/> past the limit.</summary>
    public Func<ChatMailItem, CancellationToken, Task<string?>> ReadBody = (_, _) => Task.FromResult<string?>(null);

    /// <summary>The readable attachments' text, appended to the body; null when attachments are not read.</summary>
    public Func<ChatMailItem, Action<string>, CancellationToken, Task<string>>? ReadAttachments;

    public IContextBuilder Context = new ContextBuilder();

    public EvidenceMode Mode = EvidenceMode.Original;

    /// <summary>Estimated tokens of all the evidence together; 0 = only the per-email budget.</summary>
    public int PackTokens;

    public async Task<EvidenceReading> ReadAsync(IReadOnlyList<ChatMailItem> items,
        bool attachmentsAllowed, int budget, AnalysisStats documents, CancellationToken ct)
    {
        var reading = new EvidenceReading();
        var read = new List<MailEvidence>();
        int used = 0;
        var ordered = items.OrderBy(i => i.Sent ?? i.Received).ToList();
        var slice = new UiSlice();
        try
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = ordered[i];
                Track(item);
                MailEvidence evidence;
                if (Mode == EvidenceMode.FactsOnly)
                {
                    var facts = ActionMailEvidence.Facts(item.Facts, attachmentsAllowed);
                    if (facts == null)
                    {
                        reading.NoFacts.Add($"«{Truncate(item.Subject, 60)}» del {item.Received:dd/MM/yyyy}");
                        continue;
                    }
                    evidence = new MailEvidence { Mail = item, Text = facts };
                }
                else
                {
                    Progress($"Lettura per intero: email {i + 1} di {ordered.Count} · «{Truncate(item.Subject, 40)}»", i, ordered.Count);
                    await slice.YieldIfDueAsync(ct);
                    string? body;
                    try { body = await ReadBody(item, ct); }
                    catch (MailBodyTooLongException)
                    {
                        // One email past the reading limit no longer fails the whole answer: it stays out and is named.
                        reading.TooLong.Add($"«{Truncate(item.Subject, 60)}» del {item.Received:dd/MM/yyyy}");
                        continue;
                    }
                    if (body == null)
                    {
                        reading.Missing.Add($"«{Truncate(item.Subject, 60)}» del {item.Received:dd/MM/yyyy HH:mm}");
                        continue;
                    }
                    int firstDocument = documents.Documents.Count;
                    int position = i;
                    var files = !attachmentsAllowed || ReadAttachments == null ? string.Empty
                        : await ReadAttachments(item, name => Progress($"Email {position + 1} di {ordered.Count} · allegato «{Truncate(name, 34)}»", position, ordered.Count), ct);
                    evidence = new MailEvidence { Mail = item, Text = Context.CleanEmailBody(body, item.Subject) + files, Whole = true };
                    // The email with its documents whole, or not at all: no reading in parts.
                    if (!Context.Fits(ChatMailDigest.Note(read.Count + 1, item, evidence.Text), budget))
                    {
                        foreach (var d in documents.Documents.Skip(firstDocument)) d.PreparedForRequest = false;
                        reading.TooLong.Add($"«{Truncate(item.Subject, 60)}» del {item.Received:dd/MM/yyyy}");
                        continue;
                    }
                }
                // A source past the cumulative budget stays out whole; never fall back to a paid card.
                int size = ChatMailDigest.Note(read.Count + 1, item, evidence.Text).Length;
                if (PackTokens > 0 && !Context.Fits(new string(' ', used + size), PackTokens))
                {
                    reading.OverBudget.Add($"«{Truncate(item.Subject, 60)}» del {item.Received:dd/MM/yyyy}");
                    continue;
                }
                used += size;
                read.Add(evidence);
            }
            // The counters show the reading done while the web search and the answer go on: they stayed at
            // «1/2 · 50%» for 48 s on 27/10/2026.
            if (ordered.Count > 0) Progress($"Email lette: {ordered.Count} di {ordered.Count}", ordered.Count, ordered.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reading.Stopped = true;
        }
        reading.Pack = MailEvidencePack.Chronological(read);
        return reading;
    }

    private static string Truncate(string text, int max) =>
        text.Length > max ? text.Substring(0, max - 1).TrimEnd() + "…" : text;
}
