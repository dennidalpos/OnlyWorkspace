using OnlyWorkspace.Core.Utils;
using Xunit;

namespace OnlyWorkspace.Tests;

public class DraftFormatterTests
{
    private const string Draft = "Gentile **Marco**,\n\n- punto *uno*\n- punto <due>\n\nSaluti";

    [Fact]
    public void ToPlainText_StripsMarkdownMarkers()
    {
        var text = DraftFormatter.ToPlainText(Draft);

        Assert.Equal("Gentile Marco,\r\n\r\n- punto uno\r\n- punto <due>\r\n\r\nSaluti", text);
    }

    [Fact]
    public void ToHtml_RendersFormattingAndEscapesHtml()
    {
        var html = DraftFormatter.ToHtml(Draft);

        Assert.Contains("<p class=MsoNormal>Gentile <b>Marco</b>,</p>", html);
        Assert.Contains("<li class=MsoNormal>punto <i>uno</i></li>", html);
        Assert.Contains("punto &lt;due&gt;", html);
        Assert.DoesNotContain("**", html);
    }
}
