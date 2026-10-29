using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Core.Agents;

/// <summary>Contact fields the flow reads from emails and writes to Outlook; keys are the pane's and the model's names.</summary>
public static class ContactFields
{
    public const string FullName = "fullName";
    public const string Company = "company";
    public const string JobTitle = "jobTitle";
    public const string BusinessPhone = "businessPhone";
    public const string MobilePhone = "mobilePhone";
    public const string BusinessAddress = "businessAddress";
    public const string WebPage = "webPage";

    public static readonly IReadOnlyList<string> All = new[] { FullName, Company, JobTitle, BusinessPhone, MobilePhone, BusinessAddress, WebPage };

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [FullName] = "Nome", [Company] = "Azienda", [JobTitle] = "Ruolo", [BusinessPhone] = "Telefono ufficio",
        [MobilePhone] = "Cellulare", [BusinessAddress] = "Indirizzo ufficio", [WebPage] = "Sito web"
    };

    public const int MaxLength = 255;

    /// <summary>Only known keys, trimmed and limited; empty values dropped. Used for model output and pane input alike.</summary>
    public static Dictionary<string, string> Clean(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            if (!All.Contains(pair.Key)) continue;
            var value = Regex.Replace(pair.Value ?? string.Empty, @"[\u0000-\u0009\u000B-\u001F]", " ").Trim();
            if (pair.Key != BusinessAddress) value = Regex.Replace(value, @"\s+", " ");
            // Signatures label numbers ("Tel. ", "Cell.: "): the field keeps the number only.
            if (pair.Key is BusinessPhone or MobilePhone) value = Regex.Replace(value, @"^[^\d+]+", string.Empty).Trim();
            if (value.Length == 0) continue;
            result[pair.Key] = value.Length > MaxLength ? value.Substring(0, MaxLength) : value;
        }
        return result;
    }

    public static Dictionary<string, string> FromJson(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return new Dictionary<string, string>();
        return Clean(e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
            .Select(p => new KeyValuePair<string, string?>(p.Name, p.Value.GetString())));
    }
}

/// <summary>Which archive addresses may become contacts.</summary>
public static class ContactCandidateFilter
{
    private static readonly Regex Automatic = new(
        @"^(no[-_.]?reply|do[-_.]?not[-_.]?reply|donotreply|mailer[-_.]?daemon|postmaster|bounces?|notifications?|notifiche|notifica|alert|alerts|newsletter|news|mailing|listserv|unsubscribe|daemon|system|automat\w*|posta[-_.]?certificata|comunicazioni[-_.]?pec)([-_.+].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Role = new(
        @"^(info|informazioni|amministrazione|admin|ufficio|segreteria|contabilita|commerciale|vendite|sales|ordini|orders|acquisti|fatture|fatturazione|billing|supporto|support|assistenza|helpdesk|help|hr|personale|marketing|contatti|contact|contacts|office|reception|tecnico|logistica|spedizioni|magazzino|pec|legale|direzione)([-_.+].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Only people the user corresponds with (two-way), with a real SMTP address; automatic senders never.
    /// <paramref name="ownDomains"/> excludes colleagues when the user asks for it.
    /// </summary>
    public static bool Accepts(string? address, bool twoWay, IReadOnlyCollection<string>? ownDomains = null)
    {
        if (!twoWay) return false;
        var addr = CounterpartyResolver.Normalize(address);
        int at = addr.IndexOf('@');
        if (at <= 0 || at != addr.LastIndexOf('@') || at == addr.Length - 1 || addr.IndexOf('.', at) < 0 || addr.IndexOf(' ') >= 0) return false;
        if (Automatic.IsMatch(addr.Substring(0, at))) return false;
        var domain = addr.Substring(at + 1);
        return ownDomains == null || !ownDomains.Any(d => domain.Equals(d, StringComparison.OrdinalIgnoreCase) || domain.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A shared mailbox (info@, amministrazione@): a company contact, not a person.</summary>
    public static bool IsRole(string? address)
    {
        var addr = CounterpartyResolver.Normalize(address);
        int at = addr.IndexOf('@');
        return at > 0 && Role.IsMatch(addr.Substring(0, at));
    }
}

/// <summary>Display names as mail clients write them, turned into a contact name.</summary>
public static class ContactNameParser
{
    private static readonly Regex OnBehalf = new(@"\s+(per conto di|on behalf of|im auftrag von|de la part de)\s+.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Via = new(@"\s+(via|tramite)\s+\S+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Bracketed = new(@"\s*[\[(<][^\])>]*[\])>]\s*", RegexOptions.CultureInvariant);

    /// <summary>"Rossi, Mario" → "Mario Rossi", "MARIO ROSSI" → "Mario Rossi"; empty when the name is only the address.</summary>
    public static string Clean(string? displayName, string? address)
    {
        var name = (displayName ?? string.Empty).Trim().Trim('"', '\'', ' ');
        name = OnBehalf.Replace(name, string.Empty);
        name = Via.Replace(name, string.Empty);
        name = Bracketed.Replace(name, " ").Trim().Trim('"', '\'', ' ', '-', '|');
        name = Regex.Replace(name, @"\s+", " ");
        if (name.Length == 0 || name.IndexOf('@') >= 0 || !name.Any(char.IsLetter)) return string.Empty;
        if (string.Equals(name, CounterpartyResolver.Normalize(address), StringComparison.OrdinalIgnoreCase)) return string.Empty;

        var comma = name.Split(',');
        if (comma.Length == 2 && comma[0].Trim().Length > 0 && comma[1].Trim().Length > 0 && comma[0].Trim().Split(' ').Length <= 2 && comma[1].Trim().Split(' ').Length <= 2)
            name = comma[1].Trim() + " " + comma[0].Trim();

        bool upper = name.Any(char.IsLetter) && name.Where(char.IsLetter).All(char.IsUpper);
        bool lower = name.Where(char.IsLetter).All(char.IsLower);
        if (upper || lower) name = CultureInfo.GetCultureInfo("it-IT").TextInfo.ToTitleCase(name.ToLowerInvariant());
        return name;
    }
}

public sealed class ContactChange
{
    public string Field { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Old { get; set; } = string.Empty;
    public string New { get; set; } = string.Empty;
}

/// <summary>What the newest email changes in an existing contact. A field is never emptied.</summary>
public static class ContactDiff
{
    public static List<ContactChange> Compute(IReadOnlyDictionary<string, string> existing, IReadOnlyDictionary<string, string> proposed)
    {
        var changes = new List<ContactChange>();
        foreach (var field in ContactFields.All)
        {
            if (!proposed.TryGetValue(field, out var value) || value.Length == 0) continue;
            existing.TryGetValue(field, out var old);
            old ??= string.Empty;
            if (SameValue(field, old, value)) continue;
            changes.Add(new ContactChange { Field = field, Label = ContactFields.Labels[field], Old = old, New = value });
        }
        return changes;
    }

    public static bool SameValue(string field, string a, string b) => field switch
    {
        ContactFields.BusinessPhone or ContactFields.MobilePhone => PhoneKey(a) == PhoneKey(b),
        ContactFields.WebPage => WebKey(a) == WebKey(b),
        _ => TextKey(a) == TextKey(b)
    };

    /// <summary>Digits without an international prefix (+39, 0039): the same number however it is written.</summary>
    public static string PhoneKey(string? phone)
    {
        var text = (phone ?? string.Empty).Trim();
        bool international = text.StartsWith("+", StringComparison.Ordinal) || text.StartsWith("00", StringComparison.Ordinal);
        var digits = new string(text.Where(c => c >= '0' && c <= '9').ToArray());
        if (text.StartsWith("00", StringComparison.Ordinal) && digits.StartsWith("00", StringComparison.Ordinal)) digits = digits.Substring(2);
        if (international && digits.StartsWith("39", StringComparison.Ordinal)) digits = digits.Substring(2);
        return digits;
    }

    public static string WebKey(string? url)
    {
        var text = (url ?? string.Empty).Trim().ToLowerInvariant();
        text = Regex.Replace(text, @"^https?://", string.Empty);
        if (text.StartsWith("www.", StringComparison.Ordinal)) text = text.Substring(4);
        return text.TrimEnd('/');
    }

    public static string TextKey(string? text) => Regex.Replace((text ?? string.Empty).ToLowerInvariant(), @"[\s,;]+", " ").Trim();
}

/// <summary>The model's answer for one sender: kept only where the email text says it.</summary>
public static class ContactExtraction
{
    /// <summary>Fields of the indicated sender; values not found in <paramref name="emailText"/> are dropped as invented.</summary>
    public static Dictionary<string, string> Parse(string json, string emailText)
    {
        using var doc = ModelOutput.ParseObject(json, "contacts");
        var first = doc.RootElement.GetProperty("contacts").EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
        var fields = ContactFields.FromJson(first);
        foreach (var key in fields.Keys.ToList())
            if (!Grounded(key, fields[key], emailText)) fields.Remove(key);
        return fields;
    }

    public static bool Grounded(string field, string value, string text)
    {
        if (field is ContactFields.BusinessPhone or ContactFields.MobilePhone)
        {
            var key = ContactDiff.PhoneKey(value);
            if (key.Length < 6) return false;
            var digits = new string((text ?? string.Empty).Where(c => c >= '0' && c <= '9').ToArray());
            return digits.Contains(key);
        }
        if (field == ContactFields.WebPage) return ContactDiff.WebKey(text).Contains(ContactDiff.WebKey(value));
        var haystack = ContactDiff.TextKey(RemoveDiacritics(text));
        if (field == ContactFields.FullName)
        {
            // The model may reorder "Rossi, Mario": every word must be there.
            var words = ContactDiff.TextKey(RemoveDiacritics(value)).Split(' ').Where(w => w.Length > 1).ToList();
            return words.Count > 0 && words.All(w => haystack.Contains(w));
        }
        return haystack.Contains(ContactDiff.TextKey(RemoveDiacritics(value)));
    }

    private static string RemoveDiacritics(string? text)
    {
        var d = (text ?? string.Empty).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Automatic replies and delivery reports carry someone else's details or none.</summary>
    public static bool IsAutomatic(string? messageClass, string? subject)
    {
        var mc = messageClass ?? string.Empty;
        if (!mc.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase)) return true;
        if (mc.StartsWith("IPM.Note.Rules.", StringComparison.OrdinalIgnoreCase) || mc.IndexOf(".SMIME", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return Regex.IsMatch(subject ?? string.Empty, @"^\s*(risposta automatica|automatic reply|auto[- ]?reply|out of office|fuori sede|assente|undeliverable|non recapitabile|delivery status)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
