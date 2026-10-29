using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OnlyWorkspace.Core.Security;

/// <summary>
/// Static reading of PowerShell, JScript/JavaScript, VBScript and batch files: nothing is executed, nothing is
/// fetched. Comments are blanked and string literals masked first, so a command that is only mentioned (comment,
/// message, example text) is not an operation. Download and execution are linked only when the code shows the
/// link: the same statement, a variable or a file path that flows from one to the other.
/// </summary>
public static class ScriptStaticRules
{
    public enum Language { PowerShell, JScript, VBScript, Batch }

    public const int MaxChars = 2_000_000;
    public const int MaxFindings = 200;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static Language? LanguageOf(string extension) => extension.ToLowerInvariant() switch
    {
        "ps1" or "psm1" or "psd1" => Language.PowerShell,
        "js" or "jse" => Language.JScript,
        "vbs" or "vbe" => Language.VBScript,
        "bat" or "cmd" => Language.Batch,
        _ => null
    };

    private static Regex R(string pattern) => new(pattern, Options, Timeout);

    // ----- PowerShell -----
    private static readonly Regex PsDownload = R(@"\b(Invoke-WebRequest|Invoke-RestMethod|Start-BitsTransfer)\b|(?<![\w$.-])(iwr|irm|wget|curl)(?![\w-])|\.Download(String|File|Data)(Async)?\s*\(|\bGet(String|ByteArray|Stream)Async\s*\(");
    private static readonly Regex PsExec = R(@"(?<![\w$.-])(Invoke-Expression|iex|Start-Process|saps|Invoke-Item|ii)(?![\w-])|\[(System\.)?Diagnostics\.Process\]::Start|\[scriptblock\]::Create|(?:^|[\s;(|{=])&\s*[$(""'\w]|(?<![\w-])(rundll32|regsvr32|mshta|cmd(\.exe)?\s+/[ck]|powershell(\.exe)?|pwsh(\.exe)?|cscript|wscript)(?![\w-])");
    private static readonly Regex PsHidden = R(@"-w[a-z]*\s+(hidden|1)\b|WindowStyle\s*=?\s*['""]?Hidden|CreateNoWindow\s*=\s*\$true");
    private static readonly Regex PsEncoded = R(@"(?<![\w-])(powershell|pwsh)(\.exe)?\b[^\n]*?\s-e[a-z]*\s+['""]?[A-Za-z0-9+/=]{16,}");
    private static readonly Regex PsAssign = R(@"^\s*\$([\w:]+)\s*=");
    private static readonly Regex PsOutPath = R(@"-(OutFile|Destination)\s+(\S+)|\.DownloadFile(Async)?\s*\(\s*[^,]+,\s*([^)]+)\)");
    private static readonly Regex PsObfuscated = R(@"FromBase64String|-bxor|\[char\[\]\]|-join\s*\(|\[array\]::Reverse");
    private static readonly Regex CharCodes = R(@"\[char\]\s*0?x?[0-9a-f]+");

    // ----- Windows script host (JScript, VBScript) -----
    private static readonly Regex WshDownloadObject = R(@"(MSXML2\.(Server)?XMLHTTP|Microsoft\.XMLHTTP|WinHttp\.WinHttpRequest)");
    private static readonly Regex WshShellObject = R(@"WScript\.Shell|Shell\.Application");
    private static readonly Regex WshRun = R(@"\.(Run|Exec|ShellExecute)\b\s*\(?");
    private static readonly Regex WshHiddenRun = R(@"\.Run\b\s*\(?\s*[^,\n]+,\s*0\b");
    private static readonly Regex WshEval = R(@"(?<![\w.])(eval|Execute|ExecuteGlobal)\s*\(|(?<![\w.])(Execute|ExecuteGlobal)\s+\w|new\s+Function\s*\(");
    private static readonly Regex WshResponse = R(@"\.(responseText|responseBody)\b");
    private static readonly Regex WshSave = R(@"\.SaveToFile\s*\(?\s*([^,)\n]+)");
    private static readonly Regex JsObfuscated = R(@"String\.fromCharCode|(eval|Function)\s*\(\s*(atob|unescape|decodeURIComponent)\s*\(");
    private static readonly Regex VbsCharCodes = R(@"\bChrW?\s*\(\s*\d+\s*\)");
    private static readonly Regex HexEscapes = R(@"\\x[0-9a-f]{2}");

    // ----- Batch and command lines -----
    private static readonly Regex BatDownload = R(@"\bbitsadmin(\.exe)?\b[^\n]*/transfer\b|\bcertutil(\.exe)?\b[^\n]*-urlcache\b|(?<![\w-])curl(\.exe)?\s+[^\n]*https?://");
    private static readonly Regex BatExec = R(@"^\s*@?(start|call)\b|(?<![\w-])(rundll32|regsvr32|mshta|cscript|wscript|powershell|pwsh)(\.exe)?(?![\w-])");
    private static readonly Regex BatComment = R(@"^\s*@?(rem(\s|$)|::)");
    private static readonly Regex BatOutPath = R(@"\s(""[^""\n]+""|\S+\.(exe|dll|ps1|bat|cmd|vbs|vbe|js|jse|hta|scr|msi))\s*$");

    // ----- Shared -----
    private static readonly Regex DefenseChange = R(@"\bSet-MpPreference\b[^\n]*-Disable\w+|\bAdd-MpPreference\b[^\n]*-Exclusion\w+|\bvssadmin(\.exe)?\s+delete\s+shadows|\bwbadmin(\.exe)?\s+delete\b|\bbcdedit(\.exe)?\b[^\n]*recoveryenabled\s+no|\bnetsh(\.exe)?\s+(advfirewall|firewall)\s+set\b[^\n]*\b(state|opmode)\s+(off|disable)|\breg(\.exe)?\s+add\b[^\n]*Windows Defender[^\n]*Disable|\bSet-NetFirewallProfile\b[^\n]*-Enabled\s+(False|0)");

    /// <summary>Script Encoder output (.jse/.vbe) starts with this marker: the code is not readable as text.</summary>
    private const string EncodedMarker = "#@~^";

    public static void Inspect(string text, Language language, string file, AttachmentInspection result) =>
        Inspect(text, language, file, result, lineOffset: 0);

    internal static void Inspect(string text, Language language, string file, AttachmentInspection result, int lineOffset)
    {
        result.Inspected++;
        if (text.Length > MaxChars)
        {
            Limit(result, file, "text-limit");
            text = text.Substring(0, MaxChars);
        }
        if (text.Contains(EncodedMarker))
        {
            Limit(result, file, "encoded-script");
            Add(result, "script.obfuscated", file, text, text.IndexOf(EncodedMarker, StringComparison.Ordinal), lineOffset);
            return;
        }
        try
        {
            var code = CodeView.Build(text, language);
            var statements = code.Statements(language);
            switch (language)
            {
                case Language.PowerShell: PowerShell(code, statements, file, result, lineOffset); break;
                case Language.Batch: Batch(code, statements, file, result, lineOffset); break;
                default: ScriptHost(code, statements, language, file, result, lineOffset); break;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            Limit(result, file, "timeout");
        }
    }

    private static void PowerShell(CodeView code, IReadOnlyList<Statement> statements, string file, AttachmentInspection result, int lineOffset)
    {
        var downloads = new List<(int Statement, string? Variable, string? Path)>();
        for (int i = 0; i < statements.Count; i++)
        {
            var s = statements[i];
            string bare = code.Bare(s), whole = code.Whole(s);
            bool download = PsDownload.IsMatch(bare), exec = PsExec.IsMatch(bare);

            if (DefenseChange.IsMatch(whole)) Add(result, "script.defense-change", file, code.Text, s.Start, lineOffset);
            if (PsEncoded.IsMatch(whole)) Add(result, "script.encoded", file, code.Text, s.Start, lineOffset);
            if (PsHidden.IsMatch(whole) && exec) Add(result, "script.hidden-exec", file, code.Text, s.Start, lineOffset);
            if ((PsObfuscated.IsMatch(bare) && exec) || CharCodes.Matches(bare).Count >= 8)
            {
                Add(result, "script.obfuscated", file, code.Text, s.Start, lineOffset);
                Limit(result, file, "obfuscated");
            }

            if (download)
            {
                // iex (iwr …) or iwr … | iex: the same statement downloads and runs.
                if (exec && !OnlyDownloadsToFile(bare)) { Add(result, "script.download-exec", file, code.Text, s.Start, lineOffset); continue; }
                var assigned = PsAssign.Match(bare);
                var path = PsOutPath.Match(whole);
                downloads.Add((i, assigned.Success ? assigned.Groups[1].Value : null,
                    path.Success ? PathToken(path.Groups[2].Success ? path.Groups[2].Value : path.Groups[4].Value) : null));
                continue;
            }
            if (exec)
            {
                var source = downloads.FirstOrDefault(d => d.Statement < i &&
                    ((d.Variable != null && Regex.IsMatch(bare, @"\$" + Regex.Escape(d.Variable) + @"\b", Options, Timeout)) ||
                     (d.Path != null && whole.IndexOf(d.Path, StringComparison.OrdinalIgnoreCase) >= 0)));
                Add(result, source.Statement < i && (source.Variable != null || source.Path != null) ? "script.download-exec" : "script.exec",
                    file, code.Text, s.Start, lineOffset);
            }
        }
        foreach (var d in downloads.Where(d => !result.Findings.Any(f => f.Rule == "script.download-exec" && f.File == file)))
            Add(result, "script.download", file, code.Text, statements[d.Statement].Start, lineOffset);
    }

    // «iwr $url -OutFile x.exe»: the statement downloads, the execution (if any) comes later through the path.
    private static bool OnlyDownloadsToFile(string bare) =>
        Regex.IsMatch(bare, @"-OutFile\b|\.DownloadFile", Options, Timeout) && !Regex.IsMatch(bare, @"(?<![\w$.-])(Invoke-Expression|iex)(?![\w-])|\|\s*(iex|Invoke-Expression)\b", Options, Timeout);

    private static void Batch(CodeView code, IReadOnlyList<Statement> statements, string file, AttachmentInspection result, int lineOffset)
    {
        var paths = new List<(int Statement, string Path)>();
        for (int i = 0; i < statements.Count; i++)
        {
            var s = statements[i];
            string line = code.Whole(s);
            if (DefenseChange.IsMatch(line)) Add(result, "script.defense-change", file, code.Text, s.Start, lineOffset);
            if (Regex.IsMatch(line, @"(?<![\w-])(powershell|pwsh)(\.exe)?\b", Options, Timeout))
            {
                // The PowerShell command line inside the batch file, read with the PowerShell rules.
                CommandLine(line, file, code.Text, s.Start, result, lineOffset);
                continue;
            }
            bool download = BatDownload.IsMatch(line), exec = BatExec.IsMatch(line);
            if (download)
            {
                var path = BatOutPath.Match(line);
                if (exec) Add(result, "script.download-exec", file, code.Text, s.Start, lineOffset);
                else if (path.Success) paths.Add((i, PathToken(path.Groups[1].Value)));
                else Add(result, "script.download", file, code.Text, s.Start, lineOffset);
                continue;
            }
            var runs = paths.FirstOrDefault(p => p.Statement < i && line.IndexOf(p.Path, StringComparison.OrdinalIgnoreCase) >= 0);
            if (runs.Path != null) { paths.Remove(runs); Add(result, "script.download-exec", file, code.Text, s.Start, lineOffset); }
            else if (exec) Add(result, "script.exec", file, code.Text, s.Start, lineOffset);
        }
        foreach (var p in paths) Add(result, "script.download", file, code.Text, statements[p.Statement].Start, lineOffset);
    }

    private static void ScriptHost(CodeView code, IReadOnlyList<Statement> statements, Language language, string file, AttachmentInspection result, int lineOffset)
    {
        // ActiveX ProgIDs are strings: «new ActiveXObject("MSXML2.XMLHTTP")».
        bool downloader = WshDownloadObject.IsMatch(code.WholeText);
        bool shell = WshShellObject.IsMatch(code.WholeText);
        var saved = new List<string>();
        bool linked = false, downloadSeen = false;
        foreach (var s in statements)
        {
            string bare = code.Bare(s), whole = code.Whole(s);
            if (DefenseChange.IsMatch(whole)) Add(result, "script.defense-change", file, code.Text, s.Start, lineOffset);
            bool obfuscated = language == Language.JScript
                ? JsObfuscated.IsMatch(bare) || HexEscapes.Matches(whole).Count >= 40
                : VbsCharCodes.Matches(bare).Count >= 8;
            if (obfuscated)
            {
                Add(result, "script.obfuscated", file, code.Text, s.Start, lineOffset);
                Limit(result, file, "obfuscated");
            }
            if (downloader && Regex.IsMatch(bare, @"\.(open|send)\b", Options, Timeout)) downloadSeen = true;
            var save = WshSave.Match(bare);
            if (save.Success && downloader) saved.Add(PathToken(code.Whole(s).Substring(save.Groups[1].Index, save.Groups[1].Length)));

            bool run = shell && WshRun.IsMatch(bare);
            bool eval = WshEval.IsMatch(bare);
            if (!run && !eval) continue;

            if (eval && WshResponse.IsMatch(bare) && downloader) { linked = true; Add(result, "script.download-exec", file, code.Text, s.Start, lineOffset); continue; }
            if (run && saved.Any(p => whole.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)) { linked = true; Add(result, "script.download-exec", file, code.Text, s.Start, lineOffset); continue; }
            if (!run) continue; // eval of the page's own code is not a command
            if (WshHiddenRun.IsMatch(bare)) Add(result, "script.hidden-exec", file, code.Text, s.Start, lineOffset);
            // The command line passed to Run/Exec as a literal is code too.
            foreach (var literal in code.StringsIn(s)) CommandLine(literal, file, code.Text, s.Start, result, lineOffset);
            Add(result, "script.exec", file, code.Text, s.Start, lineOffset);
        }
        if (downloadSeen && !linked) Add(result, "script.download", file, code.Text, statements.FirstOrDefault().Start, lineOffset);
    }

    /// <summary>A command line given to cmd, powershell or WScript.Shell.Run: read with batch and PowerShell rules.</summary>
    private static void CommandLine(string commandLine, string file, string text, int at, AttachmentInspection result, int lineOffset)
    {
        bool powershell = Regex.IsMatch(commandLine, @"(?<![\w-])(powershell|pwsh)(\.exe)?\b", Options, Timeout);
        bool download = PsDownload.IsMatch(commandLine) || BatDownload.IsMatch(commandLine);
        bool exec = Regex.IsMatch(commandLine, @"(?<![\w$.-])(Invoke-Expression|iex|Start-Process|saps)(?![\w-])|\|\s*(iex|Invoke-Expression)\b|&\s*\(|\b(start|rundll32|regsvr32|mshta)\b", Options, Timeout);
        if (DefenseChange.IsMatch(commandLine)) Add(result, "script.defense-change", file, text, at, lineOffset);
        if (powershell && PsEncoded.IsMatch(commandLine)) Add(result, "script.encoded", file, text, at, lineOffset);
        if (powershell && PsHidden.IsMatch(commandLine)) Add(result, "script.hidden-exec", file, text, at, lineOffset);
        if (download && exec) Add(result, "script.download-exec", file, text, at, lineOffset);
        else if (download) Add(result, "script.download", file, text, at, lineOffset);
    }

    private static string PathToken(string value) => value.Trim().Trim('"', '\'', '(', ')', ' ').Trim();

    public static void Add(AttachmentInspection result, string rule, string file, string text, int index, int lineOffset)
    {
        if (result.Findings.Any(f => f.Rule == rule && f.File == file && f.Line == LineOf(text, index) + lineOffset)) return;
        if (result.Findings.Count >= MaxFindings)
        {
            Limit(result, file, "findings-limit");
            return;
        }
        index = Math.Max(0, Math.Min(index, text.Length));
        int lineStart = index == 0 ? 0 : text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        int lineEnd = text.IndexOf('\n', index);
        if (lineEnd < 0) lineEnd = text.Length;
        var snippet = text.Substring(lineStart, lineEnd - lineStart).Trim();
        result.Findings.Add(new InspectionFinding
        {
            Rule = rule,
            File = file,
            Line = LineOf(text, index) + lineOffset,
            Column = index - lineStart + 1,
            Snippet = snippet.Length > 200 ? snippet.Substring(0, 200) + "…" : snippet
        });
    }

    public static void Limit(AttachmentInspection result, string file, string reason)
    {
        if (!result.Limits.Any(l => l.File == file && l.Reason == reason)) result.Limits.Add(new InspectionLimit { File = file, Reason = reason });
    }

    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }

    internal readonly struct Statement
    {
        public Statement(int start, int end) { Start = start; End = end; }
        public int Start { get; }
        public int End { get; }
    }

    /// <summary>
    /// The script with comments blanked (Whole) and, in addition, string contents masked (Bare); same length as the
    /// text, so positions stay valid. Strings keep their quotes and are listed for command-line arguments.
    /// </summary>
    internal sealed class CodeView
    {
        private readonly char[] _whole;
        private readonly char[] _bare;
        private readonly List<(int Start, int End)> _strings = new();

        private CodeView(string text)
        {
            Text = text;
            _whole = text.ToCharArray();
            _bare = text.ToCharArray();
        }

        public string Text { get; }
        public string WholeText => new(_whole);

        public string Whole(Statement s) => new(_whole, s.Start, s.End - s.Start);
        public string Bare(Statement s) => new(_bare, s.Start, s.End - s.Start);

        public IEnumerable<string> StringsIn(Statement s) =>
            _strings.Where(r => r.Start >= s.Start && r.End <= s.End).Select(r => Text.Substring(r.Start, r.End - r.Start));

        public static CodeView Build(string text, Language language)
        {
            var view = new CodeView(text);
            if (language == Language.Batch)
            {
                int start = 0;
                while (start <= text.Length)
                {
                    int end = text.IndexOf('\n', start);
                    if (end < 0) end = text.Length;
                    if (BatComment.IsMatch(text.Substring(start, end - start))) view.Blank(start, end);
                    start = end + 1;
                }
                return view;
            }

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (language == Language.PowerShell)
                {
                    if (c == '<' && next == '#') { i = view.BlankUntil(i, "#>"); continue; }
                    if (c == '#') { i = view.BlankLine(i); continue; }
                    if (c == '@' && (next == '\'' || next == '"') && i + 2 < text.Length && (text[i + 2] == '\n' || text[i + 2] == '\r'))
                    { i = view.MaskUntil(i + 2, "\n" + next + "@"); continue; }
                    if (c == '\'') { i = view.MaskQuoted(i, '\'', doubled: true, escape: '\0', multiline: true); continue; }
                    if (c == '"') { i = view.MaskQuoted(i, '"', doubled: true, escape: '`', multiline: true); continue; }
                }
                else if (language == Language.JScript)
                {
                    if (c == '/' && next == '/') { i = view.BlankLine(i); continue; }
                    if (c == '/' && next == '*') { i = view.BlankUntil(i, "*/"); continue; }
                    if (c is '\'' or '"' or '`') { i = view.MaskQuoted(i, c, doubled: false, escape: '\\', multiline: c == '`'); continue; }
                }
                else
                {
                    if (c == '\'') { i = view.BlankLine(i); continue; }
                    if ((c == 'r' || c == 'R') && IsStatementStart(text, i) && i + 3 <= text.Length &&
                        string.Compare(text, i, "rem", 0, 3, StringComparison.OrdinalIgnoreCase) == 0 &&
                        (i + 3 == text.Length || char.IsWhiteSpace(text[i + 3])))
                    { i = view.BlankLine(i); continue; }
                    if (c == '"') { i = view.MaskQuoted(i, '"', doubled: true, escape: '\0', multiline: false); continue; }
                }
                i++;
            }
            return view;
        }

        private static bool IsStatementStart(string text, int i)
        {
            for (int j = i - 1; j >= 0; j--)
            {
                if (text[j] == '\n' || text[j] == ':') return true;
                if (!char.IsWhiteSpace(text[j])) return false;
            }
            return true;
        }

        public IReadOnlyList<Statement> Statements(Language language)
        {
            // Lines joined by their continuation character; PowerShell and JScript also split on ';', VBScript on ':'.
            char continuation = language switch { Language.PowerShell => '`', Language.VBScript => '_', Language.Batch => '^', _ => '\0' };
            char separator = language switch { Language.PowerShell or Language.JScript => ';', Language.VBScript => ':', _ => '\0' };
            var list = new List<Statement>();
            int start = 0;
            for (int i = 0; i <= _bare.Length; i++)
            {
                bool end = i == _bare.Length;
                if (!end && _bare[i] == '\n')
                {
                    int j = i - 1;
                    while (j >= start && (_bare[j] == '\r' || _bare[j] == ' ' || _bare[j] == '\t')) j--;
                    if (continuation != '\0' && j >= start && _bare[j] == continuation) continue;
                    end = true;
                }
                else if (!end && separator != '\0' && _bare[i] == separator) end = true;
                if (!end) continue;
                if (i > start && new string(_bare, start, i - start).Trim().Length > 0) list.Add(new Statement(start, i));
                start = i + 1;
            }
            return list;
        }

        private void Blank(int start, int end)
        {
            for (int k = start; k < end && k < _whole.Length; k++)
                if (_whole[k] != '\n') _whole[k] = _bare[k] = ' ';
        }

        private int BlankLine(int i)
        {
            int end = Text.IndexOf('\n', i);
            if (end < 0) end = Text.Length;
            Blank(i, end);
            return end;
        }

        private int BlankUntil(int i, string close)
        {
            int end = Text.IndexOf(close, i + 2, StringComparison.Ordinal);
            end = end < 0 ? Text.Length : end + close.Length;
            Blank(i, end);
            return end;
        }

        private int MaskUntil(int i, string close)
        {
            int end = Text.IndexOf(close, i, StringComparison.Ordinal);
            int contentEnd = end < 0 ? Text.Length : end + 1;
            Mask(i, contentEnd);
            return end < 0 ? Text.Length : end + close.Length;
        }

        private int MaskQuoted(int i, char quote, bool doubled, char escape, bool multiline)
        {
            int k = i + 1;
            while (k < Text.Length)
            {
                char c = Text[k];
                if (escape != '\0' && c == escape) { k += 2; continue; }
                if (c == quote)
                {
                    if (doubled && k + 1 < Text.Length && Text[k + 1] == quote) { k += 2; continue; }
                    break;
                }
                // JScript and VBScript strings end at the line: an unclosed quote must not swallow the file.
                if (c == '\n' && !multiline) break;
                k++;
            }
            Mask(i + 1, Math.Min(k, Text.Length));
            return Math.Min(k + 1, Text.Length);
        }

        private void Mask(int start, int end)
        {
            if (end <= start) return;
            _strings.Add((start, end));
            for (int k = start; k < end && k < _bare.Length; k++)
                if (_bare[k] != '\n') _bare[k] = '_';
        }
    }
}
