using System.IO.Compression;
using System.Text;
using OpenMcdf;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Archive;

namespace OnlyWorkspace.Tests;

public sealed class AttachmentContainerTests
{
    private static byte[] Zip(string name, byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var entry = zip.CreateEntry(name).Open()) entry.Write(bytes, 0, bytes.Length);
        return output.ToArray();
    }

    [Fact]
    public async Task NestedZipReadsWholeText_WithoutFollowingEntryPaths()
    {
        byte[] file = Zip("../../inner.zip", Zip("../../outside.txt", Encoding.UTF8.GetBytes("nested document marker")));
        var result = await AttachmentExtractionTests.ExtractAsync("outer.zip", file);
        Assert.False(result.Truncated);
        Assert.Null(result.Problem);
        Assert.Contains("nested document marker", result.Text);
    }

    [Fact]
    public async Task NestedDepthAndUnsupportedChildMarkEntireContainerPartial()
    {
        var deep = Zip("a.zip", Zip("b.zip", Zip("c.txt", Encoding.UTF8.GetBytes("must not reach a model"))));
        var result = await AttachmentExtractionTests.ExtractAsync("deep.zip", deep);
        Assert.True(result.Truncated);
        Assert.NotNull(result.Problem);
        result = await AttachmentExtractionTests.ExtractAsync("unknown.zip", Zip("data.bin", new byte[] { 0, 1, 2 }));
        Assert.True(result.Truncated);
        var outcome = new DocumentOutcome { Name = "unknown.zip" };
        outcome.ApplyExtraction(result);
        Assert.False(outcome.WasRead);
        Assert.Equal(DocumentStatus.Incomplete, outcome.Status);
        Assert.Contains("elementi non supportati", outcome.Reason);
        Assert.DoesNotContain("troppo grande", outcome.Reason);
        var body = new ArchiveBody { Text = "body" };
        body.Attachments.Add(new ArchiveAttachment { State = "limited", Text = result.Text });
        Assert.Throws<InvalidOperationException>(() => ArchiveAttachmentReader.WholeText(body));
    }

    [Fact]
    public async Task ZipExpansionIsBoundedByActualBytes()
    {
        var bomb = Zip("large.txt", new byte[30 * 1024 * 1024 + 1]);
        var result = await AttachmentExtractionTests.ExtractAsync("expansion.zip", bomb);
        Assert.True(result.Truncated);
        var outcome = new DocumentOutcome();
        outcome.ApplyExtraction(result);
        Assert.Equal(DocumentStatus.TooLarge, outcome.Status);
        Assert.False(outcome.WasRead);
        Assert.Empty(result.Text.Replace("[Allegato: large.txt — non letto: troppo grande]", "").Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BinaryWordReadsPieceTable_UnicodeAndCompressed(bool compressed)
    {
        var result = await AttachmentExtractionTests.ExtractAsync("original.doc", Word("Ordine 123\rSaldo 1.000 euro\r", compressed));
        Assert.Null(result.Problem);
        Assert.False(result.Truncated);
        Assert.Contains("Ordine 123", result.Text);
        Assert.Contains("Saldo 1.000 euro", result.Text);
    }

    [Fact]
    public async Task ProtectedBinaryWordIsNeverPresentedAsReadable()
    {
        var result = await AttachmentExtractionTests.ExtractAsync("protected.doc", Word("secret", false, true));
        Assert.Equal("protected", result.Problem);
        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task BinaryExcelReadsLegacyCells()
    {
        using var file = new MemoryStream();
        using (var writer = new BinaryWriter(file, Encoding.ASCII, true))
        {
            void Record(ushort type, byte[] data) { writer.Write(type); writer.Write((ushort)data.Length); writer.Write(data); }
            Record(0x0009, new byte[] { 2, 0, 0x10, 0 }); // BIFF2 worksheet BOF
            Record(0x0000, new byte[] { 0, 0, 1, 0, 0, 0, 1, 0 });
            var label = Encoding.ASCII.GetBytes("Binary invoice 123");
            Record(0x0004, new byte[7].Concat(new[] { (byte)label.Length }).Concat(label).ToArray());
            Record(0x000A, Array.Empty<byte>());
        }
        var result = await AttachmentExtractionTests.ExtractAsync("legacy.xls", file.ToArray());
        Assert.Null(result.Problem);
        Assert.False(result.Truncated);
        Assert.Contains("Binary invoice 123", result.Text);
    }

    private static byte[] Word(string text, bool compressed, bool encrypted = false)
    {
        var path = Path.Combine(Path.GetTempPath(), "ow-word-fixture-" + Guid.NewGuid().ToString("N") + ".doc");
        try
        {
            byte[] encoded = (compressed ? Encoding.ASCII : Encoding.Unicode).GetBytes(text);
            byte[] document = new byte[512 + encoded.Length];
            void W16(int offset, ushort value) => BitConverter.GetBytes(value).CopyTo(document, offset);
            void W32(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(document, offset);
            W16(0, 0xA5EC); W16(2, 0xC1); W16(10, encrypted ? (ushort)0x100 : (ushort)0);
            W16(32, 14); W16(62, 22); W32(76, (uint)text.Length); W16(152, 93);
            W32(154 + 33 * 8, 0); W32(154 + 33 * 8 + 4, 21);
            encoded.CopyTo(document, 512);
            byte[] table = new byte[21]; table[0] = 2;
            BitConverter.GetBytes(16u).CopyTo(table, 1);
            BitConverter.GetBytes((uint)text.Length).CopyTo(table, 9);
            BitConverter.GetBytes(compressed ? 0x40000400u : 512u).CopyTo(table, 15);
            using (var root = RootStorage.Create(path))
            {
                using (var stream = root.CreateStream("WordDocument")) stream.Write(document, 0, document.Length);
                using (var stream = root.CreateStream("0Table")) stream.Write(table, 0, table.Length);
            }
            return File.ReadAllBytes(path);
        }
        finally { TestArtifacts.DeleteFile(path); }
    }
}
