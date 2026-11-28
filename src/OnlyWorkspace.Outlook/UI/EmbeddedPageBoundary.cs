using System;
using System.Text;

namespace OnlyWorkspace.Outlook.UI;

internal sealed class EmbeddedPageBoundary
{
    private const string EmbeddedPageUri = "about:blank";
    private const string EmbeddedDataPrefix = "data:text/html;charset=utf-8;base64,";
    private bool _navigationPending;
    private bool _embeddedPageActive;
    private string? _expectedDataUri;

    public void BeginNavigation(string html)
    {
        _embeddedPageActive = false;
        _navigationPending = true;
        // NavigateToString can raise NavigationStarting with this data URI, then commit as about:blank.
        _expectedDataUri = EmbeddedDataPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
    }

    public bool AllowNavigation(string? uri)
    {
        if (!_navigationPending ||
            (!string.Equals(uri, EmbeddedPageUri, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri, _expectedDataUri, StringComparison.Ordinal)))
        {
            return false;
        }

        _navigationPending = false;
        _expectedDataUri = null;
        _embeddedPageActive = true;
        return true;
    }

    public bool IsTrustedDocument(string? currentSource) =>
        _embeddedPageActive &&
        string.Equals(currentSource, EmbeddedPageUri, StringComparison.OrdinalIgnoreCase);

    public bool IsTrustedMessageSource(string? messageSource, string? currentSource) =>
        IsTrustedDocument(currentSource) &&
        string.Equals(messageSource, EmbeddedPageUri, StringComparison.OrdinalIgnoreCase);

    public void Reset()
    {
        _navigationPending = false;
        _embeddedPageActive = false;
        _expectedDataUri = null;
    }
}
