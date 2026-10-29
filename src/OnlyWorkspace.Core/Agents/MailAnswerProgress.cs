using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OnlyWorkspace.Core.Agents;

/// <summary>
/// Line shown while a structured answer streams in: characters received, time, and the titles of the sections already
/// written. Only titles: the points are shown once the whole answer has arrived and its values have been checked.
/// </summary>
public static class MailAnswerProgress
{
    private static readonly Regex Title = new("\"title\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\")", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Titles complete in <paramref name="partial"/> (their closing quote arrived), in order.</summary>
    public static IReadOnlyList<string> SectionTitles(string partial)
    {
        var titles = new List<string>();
        try
        {
            foreach (Match match in Title.Matches(partial))
            {
                try
                {
                    if (JsonSerializer.Deserialize<string>(match.Groups[1].Value) is { Length: > 0 } title) titles.Add(title.Trim());
                }
                catch (JsonException) { } // a title cut by the stream
            }
        }
        catch (RegexMatchTimeoutException) { } // informative only
        return titles;
    }

    public static string Describe(string partial, TimeSpan elapsed)
    {
        var it = CultureInfo.GetCultureInfo("it-IT");
        string line = $"Risposta in arrivo: {partial.Length.ToString("N0", it)} caratteri · {(int)elapsed.TotalSeconds} s";
        var titles = SectionTitles(partial);
        if (titles.Count > 0) line += " · " + string.Join(" · ", titles);
        return line;
    }
}
