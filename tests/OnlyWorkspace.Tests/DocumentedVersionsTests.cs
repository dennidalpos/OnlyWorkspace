using System.Reflection;
using System.Text.RegularExpressions;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Archive;

namespace OnlyWorkspace.Tests;

/// <summary>
/// #205: the guides quote the notice, conversation policy, pipe and archive format numbers. They stay true only while a test
/// compares them with the code: «informativa 7» outlived notice 8 in eight guides before this test existed.
/// </summary>
public sealed class DocumentedVersionsTests
{
    // Current guides. verifica.md keeps commands and state, so its «Archivio» section is checked too; retired material is not.
    private static readonly string[] Guides =
    {
        "README.md", "docs/OVERVIEW.it.md", "docs/user-guide.md", "docs/admin-guide.md", "docs/developer-guide.md",
        "docs/admin/configtool.md", "docs/admin/configurazione.md", "docs/admin/conformita-ue.md", "docs/admin/installazione.md",
        "docs/admin/sicurezza.md", "docs/tecnico/allegati.md", "docs/tecnico/architettura.md", "docs/tecnico/archivio-motore.md",
        "docs/tecnico/pseudonimizzazione.md", "docs/tecnico/verifica.md", "docs/utente/archivio.md", "docs/utente/modelli-web.md"
    };

    // verifica.md keeps lot-by-lot sections of past candidates («Archivio e prove dei candidati precedenti», «Discrepanze aperte»):
    // only its current sections are compared.
    private static readonly string[] VerificaCurrent = { "## Stato corrente", "## Comandi", "## Non qualificato", "## Copertura" };

    private static IEnumerable<(string File, int Line, string Text)> Lines() =>
        Guides.SelectMany(file =>
        {
            bool current = !file.EndsWith("verifica.md", StringComparison.Ordinal);
            return File.ReadAllLines(Path.Combine(TestBuildPaths.RepositoryRoot, file.Replace('/', Path.DirectorySeparatorChar)))
                .Select((text, index) =>
                {
                    if (text.StartsWith("## ", StringComparison.Ordinal) && file.EndsWith("verifica.md", StringComparison.Ordinal))
                        current = VerificaCurrent.Any(h => text.StartsWith(h, StringComparison.Ordinal));
                    return (file, index + 1, text, current);
                })
                .Where(l => l.current)
                .Select(l => (l.file, l.Item2, l.text));
        });

    private static List<string> Mismatches(string pattern, int expected, Func<string, bool>? skip = null) =>
        Lines().SelectMany(l => Regex.Matches(l.Text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Cast<Match>()
                .Where(m => skip?.Invoke(l.Text) != true && int.Parse(m.Groups[1].Value) != expected)
                .Select(m => $"{l.File}:{l.Line} «{m.Value}» (codice: {expected})"))
            .ToList();

    [Fact]
    public void NoticeNumber_InTheGuides_IsTheCurrentNotice()
    {
        // «informativa v2» (with v) names a signed historical text, never the current notice.
        Assert.Empty(Mismatches(@"informativa (\d+)\b", DataEgressNotice.Version));
        Assert.Empty(Mismatches(@"\bnotice (\d+)\b", DataEgressNotice.Version));
        Assert.Contains(Lines(), l => l.Text.Contains("informativa " + DataEgressNotice.Version));
    }

    [Fact]
    public void ConsentScope_InTheGuides_IsPerService_AsInTheNoticeText()
    {
        // #215: since notice 8 a grant covers every profile and model of the same service; a guide saying «per profilo» is stale.
        Assert.Contains("L'autorizzazione vale per servizio", DataEgressNotice.Text);
        var stale = Lines().Where(l => Regex.IsMatch(l.Text, @"(?:autorizzazion\w*|authorization|grants?)[^.|]{0,60}(?:per profilo|for profile\b|bind\w* grants to profile)", RegexOptions.IgnoreCase))
            .Select(l => $"{l.File}:{l.Line}").ToList();
        Assert.Empty(stale);
        Assert.Contains(Lines(), l => l.Text.Contains("per servizio", StringComparison.OrdinalIgnoreCase) || l.Text.Contains("per service", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConversationPolicyRevision_InTheGuides_IsTheCurrentOne()
    {
        string source = File.ReadAllText(Path.Combine(TestBuildPaths.RepositoryRoot, "src", "OnlyWorkspace.Core", "Privacy", "ConversationPolicy.cs"));
        int revision = int.Parse(Regex.Match(source, @"Version = (\d+),").Groups[1].Value);
        Assert.Empty(Mismatches(@"policy (?:conversazione|dello storico)[^.]{0,30}?revisione (\d+)", revision));
        Assert.Contains(Lines(), l => Regex.IsMatch(l.Text, @"policy (?:conversazione|dello storico)[^.]{0,30}?revisione " + revision));
    }

    [Fact]
    public void ArchivePipeAndFormat_InTheGuides_AreTheCurrentOnes()
    {
        int pipe = ArchivePipe.Version;
        // A retired field «ritirati dalla pipe v14» states when it went away, not the current pipe.
        Assert.Empty(Mismatches(@"pipe v(\d+)", pipe, text => text.Contains("ritirat")));
        Assert.Contains(Lines(), l => l.Text.Contains("pipe v" + pipe));

        var schema = typeof(ArchivePipe).Assembly.GetType("OnlyWorkspace.Infrastructure.Archive.ArchiveDb")!
            .GetField("SchemaVersion", BindingFlags.NonPublic | BindingFlags.Static)!;
        int format = (int)schema.GetRawConstantValue()!;
        // Singular «formato N» is the current one; «formati 11 e 12» (plural) are the migrated ones.
        Assert.Empty(Mismatches(@"formato (\d+)\b", format));
        Assert.Contains(Lines(), l => l.Text.Contains("formato " + format));
    }

    [Fact]
    public void ExtendedNumber_InTheGuides_IsTheEnumValue()
    {
        Assert.Empty(Mismatches(@"valore enum (\d+)", (int)ExposureLevel.Extended));
        Assert.Empty(Mismatches(@"\*\*(\d+) · Esteso\*\*", (int)ExposureLevel.Extended));
    }

    [Fact]
    public void VerificaKeepsCommandsStateAndWhatIsNotQualified_AndNoDatedChronicle()
    {
        string path = Path.Combine(TestBuildPaths.RepositoryRoot, "docs", "tecnico", "verifica.md");
        string text = File.ReadAllText(path);
        foreach (string heading in new[] { "## Stato corrente", "## Comandi", "## Non qualificato" })
            Assert.Contains(heading, text);
        // The chronicle of closed lots lives in the dossier history, with its hash.
        Assert.DoesNotMatch(new Regex(@"^## .+— \d+ novembre 2026$", RegexOptions.Multiline), text);
        Assert.Contains("01-projects/OnlyWorkspace/history/2026-11-11-verifica-cronaca/report.md", text);
        Assert.Matches(@"SHA-256 `[0-9A-F]{64}`", text);
        // The tracker moved from 02-areas to 01-projects.
        Assert.DoesNotContain("02-areas", text);
    }

    [Fact]
    public void RelativeLinksOfTheGuides_PointToExistingFilesAndHeadings()
    {
        var broken = new List<string>();
        foreach (string guide in Guides)
        {
            string dir = Path.GetDirectoryName(Path.Combine(TestBuildPaths.RepositoryRoot, guide.Replace('/', Path.DirectorySeparatorChar)))!;
            foreach (Match link in Regex.Matches(File.ReadAllText(Path.Combine(TestBuildPaths.RepositoryRoot, guide.Replace('/', Path.DirectorySeparatorChar))), @"\]\((?!https?:|mailto:|#)([^)\s#]+)(?:#([^)\s]+))?\)"))
            {
                string target = Path.GetFullPath(Path.Combine(dir, link.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(target) && !Directory.Exists(target)) { broken.Add($"{guide}: {link.Groups[1].Value} non esiste"); continue; }
                if (link.Groups[2].Success && target.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !Anchors(target).Contains(link.Groups[2].Value))
                    broken.Add($"{guide}: {link.Value} senza intestazione");
            }
        }
        Assert.Empty(broken);
    }

    // GitHub-style anchors: lower case, punctuation dropped, spaces to hyphens.
    private static HashSet<string> Anchors(string markdown) =>
        File.ReadAllLines(markdown).Where(l => l.StartsWith("#"))
            .Select(l => Regex.Replace(Regex.Replace(l.TrimStart('#').Trim().ToLowerInvariant(), @"[^\p{L}\p{N}\s-]", string.Empty), @"\s", "-"))
            .ToHashSet();
}
