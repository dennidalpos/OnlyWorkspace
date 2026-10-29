using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

public sealed class EmbeddedPageBoundaryTests
{
    private const string EmbeddedHtml = "<html><body>Embedded</body></html>";

    [Fact]
    public void OnlyOneHostInitiatedEmbeddedNavigationCanOpenTheMessageChannel()
    {
        var boundary = new EmbeddedPageBoundary();
        Assert.False(boundary.AllowNavigation("about:blank"));
        Assert.False(boundary.IsTrustedMessageSource("about:blank", "about:blank"));

        boundary.BeginNavigation(EmbeddedHtml);
        Assert.False(boundary.AllowNavigation("data:text/html,<script>alert(1)</script>"));
        Assert.False(boundary.AllowNavigation("data:text/html;charset=utf-8;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("<html>Other</html>"))));
        Assert.False(boundary.AllowNavigation("about:srcdoc"));
        Assert.False(boundary.AllowNavigation("https://example.org/"));
        Assert.True(boundary.AllowNavigation("data:text/html;charset=utf-8;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(EmbeddedHtml))));
        Assert.False(boundary.AllowNavigation("about:blank"));
        Assert.True(boundary.IsTrustedMessageSource("about:blank", "about:blank"));
        Assert.True(boundary.IsTrustedDocument("about:blank"));
    }

    [Fact]
    public void UnexpectedOrStaleDocumentCannotSendMessagesEvenWhenThePageWasReady()
    {
        var boundary = new EmbeddedPageBoundary();
        boundary.BeginNavigation(EmbeddedHtml);
        Assert.True(boundary.AllowNavigation("about:blank"));

        Assert.False(boundary.IsTrustedMessageSource("data:text/html,spoof", "about:blank"));
        Assert.False(boundary.IsTrustedMessageSource("about:srcdoc", "about:blank"));
        Assert.False(boundary.IsTrustedMessageSource("about:blank", "https://example.org/"));
        Assert.False(boundary.IsTrustedDocument("https://example.org/"));
        Assert.False(boundary.IsTrustedMessageSource("", "about:blank"));

        boundary.BeginNavigation(EmbeddedHtml);
        Assert.False(boundary.IsTrustedMessageSource("about:blank", "about:blank"));
        Assert.True(boundary.AllowNavigation("about:blank"));
        Assert.True(boundary.IsTrustedMessageSource("about:blank", "about:blank"));
        boundary.Reset();
        Assert.False(boundary.IsTrustedMessageSource("about:blank", "about:blank"));
    }
}
