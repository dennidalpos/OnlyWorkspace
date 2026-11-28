using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OnlyWorkspace.Core.Models;

/// <summary>Only references actually cited in the displayed answer accompany it into history.</summary>
public static class WebCitations
{
    public static List<WebSource> Used(string text, IEnumerable<WebSource> sources)
    {
        var cited = new HashSet<int>(Regex.Matches(text ?? string.Empty, @"\[W([1-9][0-9]*)\]")
            .Cast<Match>().Select(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0));
        return sources.Select((source, index) => new WebSource
        {
            Title = source.Title,
            Url = source.Url,
            CitationNumber = source.CitationNumber > 0 ? source.CitationNumber : index + 1
        }).Where(s => cited.Contains(s.CitationNumber) && Uri.TryCreate(s.Url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)).ToList();
    }
}
