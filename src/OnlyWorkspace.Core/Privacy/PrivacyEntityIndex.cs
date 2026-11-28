using System;
using System.Collections.Generic;
using System.IO;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>One literal trie per request, reused across messages. No regex is compiled per contact.</summary>
internal sealed class PrivacyEntityIndex
{
    private sealed class Node
    {
        internal readonly Dictionary<char, Node> Next = new();
        internal readonly List<string> Categories = new();
    }
    private readonly Node _root = new();
    internal PrivacyEntityIndex(IEnumerable<PrivacyIdentity> entities, Action check)
    {
        foreach (var entity in entities)
        {
            check();
            if (!PseudonymizationCatalog.ValidCategory(entity.Category)) throw new InvalidDataException("Invalid privacy entity.");
            string value = entity.Value.Trim();
            if (value.Length < 3) continue;
            var node = _root;
            foreach (char c in value)
            {
                check(); char key = char.ToUpperInvariant(c);
                if (!node.Next.TryGetValue(key, out var next)) node.Next[key] = next = new Node();
                node = next;
            }
            if (!node.Categories.Contains(entity.Category)) node.Categories.Add(entity.Category);
        }
    }
    internal IEnumerable<PseudonymMatch> Detect(string text, Action check)
    {
        for (int start = 0; start < text.Length; start++)
        {
            check();
            if (start > 0 && char.IsLetterOrDigit(text[start - 1])) continue;
            var node = _root;
            for (int end = start; end < text.Length; end++)
            {
                check();
                if (!node.Next.TryGetValue(char.ToUpperInvariant(text[end]), out var next)) break;
                node = next;
                if (end + 1 < text.Length && char.IsLetterOrDigit(text[end + 1])) continue;
                foreach (string category in node.Categories) yield return new PseudonymMatch(start, end - start + 1, category);
            }
        }
    }
}
