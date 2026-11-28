using System;
using System.Security.Cryptography;
using System.Text;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// Content-Security-Policy for the embedded pane page: a second layer behind escaping, so injected markup
/// from mail or model text cannot run script, load resources or submit forms. The page loads nothing
/// external, so everything not listed is denied. The script is allowed by its hash, computed at load time,
/// which keeps the policy valid after every edit of the page.
/// </summary>
public static class PageContentSecurity
{
    private const string ScriptOpen = "<script>";
    private const string ScriptClose = "</script>";
    private const string CharsetMeta = "<meta charset=\"utf-8\">";

    public static string Apply(string html)
    {
        int open = html.IndexOf(ScriptOpen, StringComparison.Ordinal);
        if (open < 0 || html.IndexOf("<script", open + ScriptOpen.Length, StringComparison.OrdinalIgnoreCase) >= 0 ||
            html.IndexOf("<script", StringComparison.OrdinalIgnoreCase) != open)
            throw new InvalidOperationException("The pane page must contain exactly one inline script.");
        int start = open + ScriptOpen.Length;
        int end = html.IndexOf(ScriptClose, start, StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException("The pane page script is not closed.");
        int meta = html.IndexOf(CharsetMeta, StringComparison.Ordinal);
        if (meta < 0) throw new InvalidOperationException("The pane page must declare its charset first.");

        string hash;
        using (var sha = SHA256.Create())
            hash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(html.Substring(start, end - start))));
        string policy = "default-src 'none'; script-src 'sha256-" + hash + "'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'";
        return html.Insert(meta + CharsetMeta.Length, "\n<meta http-equiv=\"Content-Security-Policy\" content=\"" + policy + "\">");
    }
}
