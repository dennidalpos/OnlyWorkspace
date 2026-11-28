using System;
using System.IO;

namespace OnlyWorkspace.Outlook.UI;

public static class HtmlAssets
{
    private const string MainHtmlResource = "OnlyWorkspace.Outlook.UI.TaskPane.html";

    private static readonly Lazy<string> _mainHtml = new(LoadMainHtml);

    public static string GetMainHtml() => _mainHtml.Value;

    private static string LoadMainHtml()
    {
        using var stream = typeof(HtmlAssets).Assembly.GetManifestResourceStream(MainHtmlResource)
            ?? throw new InvalidOperationException($"Embedded resource '{MainHtmlResource}' not found.");
        using var reader = new StreamReader(stream);
        return PageContentSecurity.Apply(reader.ReadToEnd());
    }
}
