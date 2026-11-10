using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ExcelDataReader;
using OpenMcdf;

namespace OnlyWorkspace.AttachmentExtractor;

internal static partial class ContentReader
{
    private static string? ReadBinaryExcel(string path, LimitedText text)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var reader = ExcelReaderFactory.CreateBinaryReader(file);
            do
            {
                text.AppendLine("--- Foglio: " + reader.Name + " ---");
                if (reader.HeaderFooter is { } hf)
                {
                    text.AppendBlock(hf.OddHeader); text.AppendBlock(hf.EvenHeader); text.AppendBlock(hf.FirstHeader);
                }
                while (reader.Read())
                {
                    var row = new StringBuilder();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        if (i > 0) row.Append('\t');
                        var value = reader.GetValue(i);
                        row.Append(value is DateTime date ? date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture));
                    }
                    if (!text.AppendLine(row.ToString())) return null;
                }
                if (reader.HeaderFooter is { } footer)
                {
                    text.AppendBlock(footer.OddFooter); text.AppendBlock(footer.EvenFooter); text.AppendBlock(footer.FirstFooter);
                }
            } while (reader.NextResult());
            return null;
        }
        catch (ExcelDataReader.Exceptions.InvalidPasswordException) { return "protected"; }
    }

    // MS-DOC 2.4.1: FIB -> fcClx/lcbClx -> PlcPcd -> Unicode/compressed text pieces.
    // OpenMcdf handles CFB; never scan raw binary strings, execute fields, or automate Word.
    private static string? ReadBinaryWord(Storage root, LimitedText text)
    {
        using var document = root.OpenStream("WordDocument");
        byte[] data = ReadBytes(document, checked((int)MaxNestedBytes));
        if (data.Length != document.Length) return "size-limit";
        ushort U16(byte[] bytes, int at) { Bounds(bytes, at, 2); return BitConverter.ToUInt16(bytes, at); }
        uint U32(byte[] bytes, int at) { Bounds(bytes, at, 4); return BitConverter.ToUInt32(bytes, at); }
        if (U16(data, 0) != 0xA5EC || U16(data, 2) < 0xC1) return "unsupported-word-version";
        ushort flags = U16(data, 10);
        if ((flags & 0x8100) != 0) return "protected";
        int at = checked(34 + U16(data, 32) * 2);
        int lwCount = U16(data, at);
        at += 2;
        if (lwCount < 11) return "invalid-word-fib";
        Bounds(data, at, checked(lwCount * 4));
        // The story lengths also cover notes, headers, comments and text boxes.
        var counts = new[] { U32(data, at + 12), U32(data, at + 16), U32(data, at + 20), U32(data, at + 28), U32(data, at + 32), U32(data, at + 36), U32(data, at + 40) };
        at = checked(at + lwCount * 4);
        if (U16(data, at) <= 33) return "invalid-word-fib";
        at += 2;
        int fc = checked((int)U32(data, at + 33 * 8));
        int size = checked((int)U32(data, at + 33 * 8 + 4));
        string tableName = (flags & 0x200) != 0 ? "1Table" : "0Table";
        using var table = root.OpenStream(tableName);
        byte[] tables = ReadBytes(table, checked((int)MaxNestedBytes));
        Bounds(tables, fc, size);
        int end = checked(fc + size);
        while (fc < end && tables[fc] == 1) { Bounds(tables, fc, 3); fc = checked(fc + 3 + U16(tables, fc + 1)); }
        if (fc >= end || tables[fc] != 2) return "invalid-word-pieces";
        int length = checked((int)U32(tables, fc + 1));
        int start = checked(fc + 5);
        Bounds(tables, start, length);
        if (start + (long)length > end || length < 4 || (length - 4) % 12 != 0) return "invalid-word-pieces";
        int pieces = (length - 4) / 12;
        uint expected = 0;
        foreach (uint count in counts) expected = checked(expected + count);
        if (U32(tables, start) != 0 || U32(tables, start + pieces * 4) < expected) return "incomplete-word-pieces";
        var output = new StringBuilder();
        var fields = new Stack<bool>();
        for (int i = 0; i < pieces; i++)
        {
            uint first = U32(tables, start + i * 4), last = U32(tables, start + (i + 1) * 4);
            if (last < first) return "invalid-word-pieces";
            if (first >= expected) break;
            int chars = checked((int)(Math.Min(last, expected) - first));
            uint location = U32(tables, start + (pieces + 1) * 4 + i * 8 + 2);
            bool compressed = (location & 0x40000000) != 0;
            int offset = checked((int)(location & 0x3fffffff) / (compressed ? 2 : 1));
            int bytes = checked(chars * (compressed ? 1 : 2));
            Bounds(data, offset, bytes);
            if (chars > text.Remaining) { text.Incomplete("text-limit"); return null; }
            string piece = (compressed ? Encoding.GetEncoding(1252) : Encoding.Unicode).GetString(data, offset, bytes);
            foreach (char c in piece)
            {
                if (c == '\u0013') { fields.Push(false); continue; }
                if (c == '\u0014' && fields.Count > 0) { fields.Pop(); fields.Push(true); continue; }
                if (c == '\u0015' && fields.Count > 0) { fields.Pop(); continue; }
                if (fields.Contains(false)) continue;
                if (c is '\r' or '\v' or '\f') output.Append('\n');
                else if (c == '\u0007') output.Append('\t');
                else if (!char.IsControl(c) || c is '\n' or '\t') output.Append(c);
            }
            if (output.Length > text.Remaining) { text.Incomplete("text-limit"); return null; }
        }
        if (fields.Count != 0) return "invalid-word-fields";
        text.AppendBlock(output.ToString());
        return null;
    }

    private static void Bounds(byte[] bytes, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + (long)length > bytes.Length) throw new InvalidDataException("Invalid binary Office range.");
    }
}
