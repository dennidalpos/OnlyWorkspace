using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Archive;

namespace NerBench;

/// <summary>
/// #316 (from #310): how much of the company names that the NER proxy finds in real mails is already in the local archive index.
/// Privacy rule: the report holds counts only; no name, subject, address or text is printed, logged or written, and failures
/// are reported as a reason class, never as the message of the exception.
/// </summary>
public static class RealCoverage
{
    public sealed record Options(string? Profile, string? Directory, int Sample, int OriginalTimeoutSeconds);

    public static Options Parse(string[] args)
    {
        string? profile = null, directory = null; int sample = 300, timeout = 30;
        for (int i = 1; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--profile": profile = Next(); break;
                case "--directory": directory = Next(); break;
                case "--sample": sample = int.Parse(Next() ?? "300"); break;
                case "--timeout": timeout = int.Parse(Next() ?? "30"); break;
            }
        }
        return new Options(profile, directory, Math.Clamp(sample, 1, 2000), Math.Clamp(timeout, 1, 300));
    }

    private static string Key(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public static async Task<string> RunAsync(NerModel model, PseudonymizationCatalog catalog, Options options)
    {
        string? directory = options.Directory;
        string? profile = options.Profile;
        if (profile == null)
        {
            var profiles = await new ArchiveClient(string.Empty, directory).ProfilesAsync();
            if (profiles.Count != 1) throw new InvalidOperationException($"{profiles.Count} archive profiles found: pass --profile <name>.");
            profile = profiles[0].Profile;
        }
        var client = new ArchiveClient(profile, directory);

        // Index entities, by category, exactly as the task pane reads them.
        var index = new List<PrivacyEntity>(); long cursor = 0;
        while (true)
        {
            var page = await client.GetPrivacyEntitiesAsync(cursor, StoreScope.All(), 1000);
            if (page.Count == 0) break;
            index.AddRange(page); cursor = page.Max(e => e.Id);
        }
        var indexCompanies = index.Where(e => e.Category == "AZIENDA").Select(e => Key(e.Value)).Where(k => k.Length >= 3).ToHashSet();

        var hits = await client.SearchAsync(new ArchiveQuery { Max = options.Sample, Stores = StoreScope.All(), IncludeFacts = false, IncludeAttachmentText = false });
        var failures = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Fail(string reason) => failures[reason] = failures.GetValueOrDefault(reason) + 1;
        var names = new HashSet<string>(); int readable = 0, mentions = 0;

        foreach (var hit in hits)
        {
            string? text = null;
            try
            {
                var local = await client.GetOriginalAsync(hit.EntryId, hit.StoreId);
                if (local is { Unavailable: false } && !string.IsNullOrWhiteSpace(local.Text)) text = local.Text;
                else
                {
                    string request = await client.RequestOriginalAsync(hit.EntryId, hit.StoreId);
                    var deadline = DateTime.UtcNow.AddSeconds(options.OriginalTimeoutSeconds);
                    while (true)
                    {
                        var result = await client.OriginalResultAsync(request);
                        if (result.Ready)
                        {
                            if (result.Error != null) Fail("original-source-error");
                            else if (string.IsNullOrWhiteSpace(result.Text)) Fail("original-empty");
                            else text = result.Text;
                            break;
                        }
                        if (DateTime.UtcNow > deadline) { await client.CancelOriginalAsync(request); Fail("original-timeout"); break; }
                        await Task.Delay(300);
                    }
                }
            }
            catch (TimeoutException) { Fail("engine-timeout"); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException) { Fail("engine-error"); }
            if (text == null) continue;
            readable++;
            foreach (var span in model.Spans(text, l => catalog.CategoryOfNerLabel(l)))
            {
                if (span.Category != "AZIENDA") continue;
                mentions++;
                var key = Key(text[span.Start..span.End]);
                if (key.Length >= 3) names.Add(key);
            }
        }

        int exact = names.Count(indexCompanies.Contains);
        int contained = names.Count(n => indexCompanies.Contains(n) || indexCompanies.Any(c => n.Contains(c, StringComparison.Ordinal) || c.Contains(n, StringComparison.Ordinal)));
        double Pct(int part) => names.Count == 0 ? 0 : 100.0 * part / names.Count;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# #316: real-archive AZIENDA coverage (counts only)\n");
        sb.AppendLine($"- Archive index: {index.Count} entities, {indexCompanies.Count} AZIENDA (distinct, normalized).");
        sb.AppendLine($"- Sample: {hits.Count} mails requested, {readable} readable, {hits.Count - readable} unreadable.");
        foreach (var f in failures) sb.AppendLine($"  - unreadable, reason class `{f.Key}`: {f.Value}");
        sb.AppendLine($"- NER ORG proxy: {mentions} mentions, {names.Count} distinct names.");
        sb.AppendLine($"- Exact match in the index: {exact}/{names.Count} ({Pct(exact):F0}%).");
        sb.AppendLine($"- Containment match (either name contains the other, 3+ characters): {contained}/{names.Count} ({Pct(contained):F0}%).");
        return sb.ToString();
    }
}
