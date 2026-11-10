using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OnlyWorkspace.Outlook.UI;
using Xunit;

namespace OnlyWorkspace.Tests;

public sealed class PageContentSecurityTests
{
    private static string Page() => File.ReadAllText(Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(SourceFile())!, "..", "..", "src", "OnlyWorkspace.Outlook", "UI", "TaskPane.html")));

    private static string Policy(string html) =>
        Regex.Match(html, "http-equiv=\"Content-Security-Policy\" content=\"([^\"]+)\"").Groups[1].Value;

    [Fact]
    public void Page_AllowsOnlyItsOwnScriptByHash()
    {
        string html = PageContentSecurity.Apply(Page());
        string script = Regex.Match(html, "<script>(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        using var sha = SHA256.Create();
        string hash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(script)));
        string policy = Policy(html);
        Assert.Contains("script-src 'sha256-" + hash + "'", policy);
        Assert.Contains("default-src 'none'", policy);
        Assert.Contains("base-uri 'none'", policy);
        Assert.Contains("form-action 'none'", policy);
        Assert.DoesNotContain("unsafe-eval", policy);
        Assert.DoesNotContain("script-src 'unsafe-inline'", policy);
    }

    [Fact]
    public void Page_DeclaresThePolicyBeforeAnyScriptOrStyle()
    {
        string html = PageContentSecurity.Apply(Page());
        int meta = html.IndexOf("Content-Security-Policy", StringComparison.Ordinal);
        Assert.InRange(meta, 1, html.IndexOf("<style>", StringComparison.Ordinal));
    }

    [Fact]
    public void Page_HasNoInlineHandlersOrExternalResourcesThatThePolicyWouldBreak()
    {
        string html = Page();
        Assert.DoesNotMatch(@"\son(click|load|error|change|input|submit|focus|blur|key\w+|mouse\w+)\s*=", html);
        Assert.DoesNotMatch(@"<(img|iframe|form|link|object|embed)\b", html);
    }

    [Fact]
    public void ASecondScript_IsRefused() =>
        Assert.Throws<InvalidOperationException>(() => PageContentSecurity.Apply("<meta charset=\"utf-8\"><script>a</script><script>b</script>"));

    [Fact]
    public void ScriptTagWithAttributes_IsRefused() =>
        Assert.Throws<InvalidOperationException>(() => PageContentSecurity.Apply("<meta charset=\"utf-8\"><script>a</script><script src=\"x\"></script>"));

    [Fact]
    public void LoadedPage_IsRoutedThroughThePolicy() =>
        Assert.Contains("PageContentSecurity.Apply(", File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(SourceFile())!, "..", "..", "src", "OnlyWorkspace.Outlook", "UI", "HtmlAssets.cs"))));

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
