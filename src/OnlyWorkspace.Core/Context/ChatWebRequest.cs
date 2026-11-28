using System.Text.RegularExpressions;

namespace OnlyWorkspace.Core.Context;

/// <summary>
/// Web search in the chat (Danny, 20/10/2026): only for general questions or when the question asks for the web.
/// A question about mail, agenda or contacts waited up to 180 s for a search it did not need.
/// </summary>
public static class ChatWebRequest
{
    private static readonly Regex Explicit = new(
        @"\b(sul\s+web|nel\s+web|online|on-line|in\s+rete|su\s+internet|internet|sito\s+web|notizie|news|cerca\s+in\s+rete|google)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsExplicit(string? question) => Explicit.IsMatch(question ?? string.Empty);

    /// <summary>The intent target decides: posta, agenda and contatti answer from Outlook unless the web is asked for.</summary>
    public static bool Wanted(string intentTarget, string? question) => intentTarget == "general" || IsExplicit(question);
}
