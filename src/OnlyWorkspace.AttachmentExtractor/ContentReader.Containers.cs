using System;
using System.IO;
using System.IO.Compression;

namespace OnlyWorkspace.AttachmentExtractor;

internal static partial class ContentReader
{
    private static bool ReadZip(string path, LimitedText text, int depth)
    {
        bool macros = false;
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0) continue;
            // The text stops at its limit; the security mode still visits every entry within the size and depth bounds.
            if (text.Truncated && _inspection == null) break;
            macros |= ReadNestedFile(entry.Name, entry.Length, output =>
            {
                using var input = entry.Open();
                input.CopyTo(output);
            }, Path.GetDirectoryName(path)!, text, depth);
        }
        return macros;
    }

    // Count decoded bytes actually written, not only the untrusted ZIP/MIME length.
    private sealed class ExpansionStream : Stream
    {
        private readonly Stream _inner;
        public ExpansionStream(Stream inner) => _inner = inner;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > MaxNestedBytes - _expandedBytes) throw new InvalidDataException("Expanded attachment size limit.");
            _expandedBytes += count;
            _inner.Write(buffer, offset, count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
