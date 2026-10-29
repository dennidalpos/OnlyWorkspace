using System.Text.Json;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Utils;
using Xunit;

namespace OnlyWorkspace.Tests;

public sealed class GeneratedTextMarkupTests
{
    [Theory]
    [InlineData("Ciao **Giulia**\r\n\r\n- uno\r\n- <due> 😀")]
    [InlineData("## Oggetto\n1. Primo\n2. Secondo\n\n`codice` e *corsivo*")]
    [InlineData("é e\u0301\n中文\nالعربية\n\"citazione\"")]
    public void MarkingPreservesTheExactRenderedFragmentAndPlainText(string draft)
    {
        var original = DraftFormatter.ToHtml(draft);
        var marked = DraftFormatter.ToGeneratedHtml(draft);
        Assert.Equal("<!--OnlyWorkspace:generated-text:v1:begin-->" + original
            + "<!--OnlyWorkspace:generated-text:v1:end-->", marked);
        Assert.Equal("declared", GeneratedTextMarkup.InspectHtml(marked).Status);
        Assert.Equal(1, GeneratedTextMarkup.InspectHtml(marked).DeclaredFragments);
        Assert.DoesNotContain("OnlyWorkspace:", DraftFormatter.ToPlainText(draft));
    }

    [Fact]
    public void TheSignatureAndQuotedOriginalAreOutsideEachGeneratedFragment()
    {
        const string original = "<p>Firma umana</p><blockquote>Originale ricevuto</blockquote>";
        var first = DraftFormatter.ToGeneratedHtml("Prima bozza");
        var second = DraftFormatter.ToGeneratedHtml("Seconda bozza");
        var html = "<html><body>" + second + first + original + "</body></html>";
        var result = GeneratedTextMarkup.InspectHtml(html);
        Assert.Equal(2, result.DeclaredFragments);
        Assert.Equal("declared", result.Status);
        Assert.EndsWith("<!--OnlyWorkspace:generated-text:v1:end-->" + original + "</body></html>", html);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public void EmptyDraftsDoNotDeclareGeneratedContent(string draft)
        => Assert.Equal("not-found", GeneratedTextMarkup.InspectHtml(DraftFormatter.ToGeneratedHtml(draft)).Status);

    [Theory]
    [InlineData("<p>&lt;!--OnlyWorkspace:generated-text:v1:begin--&gt;</p>")]
    [InlineData("<div title='<!--OnlyWorkspace:generated-text:v1:begin-->'>Testo</div>")]
    [InlineData("<script>var example='<!--OnlyWorkspace:generated-text:v1:begin-->';</script>")]
    public void ExamplesInTextAttributesOrScriptAreNotMetadata(string html)
        => Assert.Equal("not-found", GeneratedTextMarkup.InspectHtml(html).Status);

    [Theory]
    [InlineData("<!--OnlyWorkspace:generated-text:v1:begin--><p>testo</p>")]
    [InlineData("<!--OnlyWorkspace:generated-text:v1:end-->")]
    [InlineData("<!--OnlyWorkspace:generated-text:v1:begin--><!--OnlyWorkspace:generated-text:v1:begin--><!--OnlyWorkspace:generated-text:v1:end-->")]
    public void BrokenOrNestedScopesAreNotPresentedAsValidDeclarations(string html)
        => Assert.Equal("malformed", GeneratedTextMarkup.InspectHtml(html).Status);

    [Fact]
    public void FutureVersionsAreNotSilentlyAccepted()
    {
        var result = GeneratedTextMarkup.InspectHtml("<!--OnlyWorkspace:generated-text:v2:begin--><!--OnlyWorkspace:generated-text:v2:end-->");
        Assert.Equal("unsupported", result.Status);
        Assert.Equal(0, result.DeclaredFragments);
        Assert.Equal(2, result.UnsupportedMarkers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("--!")]
    [InlineData("--!>")]
    public void IncompleteOrNonCanonicalCommentClosingNeverCompletesADeclaration(string suffix)
    {
        var html = "<!--OnlyWorkspace:generated-text:v1:begin--><p>bozza</p><!--OnlyWorkspace:generated-text:v1:end" + suffix;
        Assert.Equal("malformed", GeneratedTextMarkup.InspectHtml(html).Status);
        Assert.Equal(0, GeneratedTextMarkup.InspectHtml(html).DeclaredFragments);
    }

    [Fact]
    public void InvalidUtf16IsRejectedRatherThanSilentlyRepaired()
    {
        var path = Path.Combine(Path.GetTempPath(), "ow-markup-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            var prefix = System.Text.Encoding.Unicode.GetPreamble();
            var html = System.Text.Encoding.Unicode.GetBytes(DraftFormatter.ToGeneratedHtml("bozza"));
            File.WriteAllBytes(path, prefix.Concat(html).Concat(new byte[] { 0x00, 0xD8 }).ToArray());
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(2, GeneratedTextInspectionCommand.Run(new[] { "inspect-ai-html", path }, output, error));
            Assert.Equal("", output.ToString());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void CompleteCommentsAreAcceptedAtExactSourceBoundariesAcrossLineEndings(string newline)
    {
        var html = "<p>😀</p>" + newline + "<!--" + newline
            + "OnlyWorkspace:generated-text:v1:begin " + newline + "-->" + newline + "<p>bozza</p>" + newline
            + "<!-- " + newline + "OnlyWorkspace:generated-text:v1:end" + newline + "--><p>firma</p>";
        Assert.Equal("declared", GeneratedTextMarkup.InspectHtml(html).Status);
        Assert.Equal(1, GeneratedTextMarkup.InspectHtml(html).DeclaredFragments);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le")]
    [InlineData("utf32be")]
    public void ValidUnicodeEncodingsPreserveTheSameMarkup(string format)
    {
        System.Text.Encoding encoding = format switch
        {
            "utf16le" => new System.Text.UnicodeEncoding(false, true, true),
            "utf16be" => new System.Text.UnicodeEncoding(true, true, true),
            "utf32le" => new System.Text.UTF32Encoding(false, true, true),
            "utf32be" => new System.Text.UTF32Encoding(true, true, true),
            _ => new System.Text.UTF8Encoding(true, true)
        };
        var html = DraftFormatter.ToGeneratedHtml("caffè Ω 😀");
        using var input = new MemoryStream(encoding.GetPreamble().Concat(encoding.GetBytes(html)).ToArray());
        Assert.Equal(html, GeneratedTextInspectionCommand.ReadHtml(input));
    }

    [Fact]
    public void ReadLimitAppliesEvenWhenAStreamDoesNotReportItsLength()
    {
        using var input = new UnseekableInput(GeneratedTextMarkup.MaxHtmlCharacters + 100);
        Assert.Throws<ArgumentException>(() => GeneratedTextInspectionCommand.ReadHtml(input));
        Assert.Equal(GeneratedTextMarkup.MaxHtmlCharacters + 1, input.BytesRead);
    }

    private sealed class UnseekableInput : Stream
    {
        private readonly int _size;
        public UnseekableInput(int size) => _size = size;
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = Math.Min(count, _size - BytesRead);
            for (int i = 0; i < available; i++) buffer[offset + i] = (byte)'x';
            BytesRead += available;
            return available;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void InspectionRefusesOversizedInputWithoutTruncation()
        => Assert.Throws<ArgumentException>(() => GeneratedTextMarkup.InspectHtml(new string('x', GeneratedTextMarkup.MaxHtmlCharacters + 1)));

    [Fact]
    public void LocalCommandReportsOnlyMetadataAndNeverClaimsAuthentication()
    {
        var path = Path.Combine(Path.GetTempPath(), "ow-markup-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            File.WriteAllText(path, DraftFormatter.ToGeneratedHtml("contenuto-sintetico-riservato"));
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, GeneratedTextInspectionCommand.Run(new[] { "inspect-ai-html", path }, output, error));
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal("declared", json.RootElement.GetProperty("status").GetString());
            Assert.False(json.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.DoesNotContain("contenuto-sintetico-riservato", output.ToString());
            Assert.DoesNotContain(path, output.ToString());
            Assert.Equal("", error.ToString());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("synthetic.txt", 1)]
    [InlineData(@"\\synthetic.invalid\share\test.html", 2)]
    [InlineData("invalid\0.html", 2)]
    public void LocalCommandRejectsUnsupportedContainersAndNetworkPaths(string path, int expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(expected, GeneratedTextInspectionCommand.Run(new[] { "inspect-ai-html", path }, output, error));
        Assert.Equal("", output.ToString());
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public void LocalCommandRefusesOversizedFilesWithoutReturningContent()
    {
        var path = Path.Combine(Path.GetTempPath(), "ow-markup-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            using (var file = File.Create(path)) file.SetLength(GeneratedTextMarkup.MaxHtmlCharacters + 1);
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(2, GeneratedTextInspectionCommand.Run(new[] { "inspect-ai-html", path }, output, error));
            Assert.Equal("", output.ToString());
            Assert.DoesNotContain(path, error.ToString());
        }
        finally { File.Delete(path); }
    }
}
