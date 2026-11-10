using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.Core.Prompts;

public sealed class PromptTemplate
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("system")]
    public string System { get; set; } = string.Empty;

    [JsonPropertyName("userTemplate")]
    public string UserTemplate { get; set; } = string.Empty;

    [JsonPropertyName("maxWords")]
    public int MaxWords { get; set; }

    /// <summary>Only in fragments.json: shared text the other templates include as {{name}}.</summary>
    [JsonPropertyName("fragments")]
    public Dictionary<string, string>? Fragments { get; set; }
}

/// <summary>
/// Prompts sent to the models. The text lives only in prompts\*.json, shipped with the add-in (adapted CO-STAR:
/// context, task, rules, style and audience where text is written for people, response format; data in the user
/// message and the task again at its end). A missing template is an error, never a copy in code.
/// </summary>
public static class PromptCatalog
{
    // Approval is tied to the shipped resource, never to a mutable prompt loaded from a directory.
    internal static IEnumerable<ChatMessage> CreateCloudFactsPrompt(string facts)
    {
        using var resource = typeof(PromptCatalog).Assembly.GetManifestResourceStream("OnlyWorkspace.Core.Prompts.cloud.facts.json")
            ?? throw new InvalidOperationException("Template cloud protetto non disponibile: nessun invio consentito.");
        using var reader = new StreamReader(resource);
        var template = JsonSerializer.Deserialize<PromptTemplate>(reader.ReadToEnd());
        if (template?.Id != "cloud.facts" || template.Version != 1)
            throw new InvalidOperationException("Template cloud protetto non valido: nessun invio consentito.");
        return new[] { ChatMessage.System(template.System), ChatMessage.User(template.UserTemplate.Replace("{facts}", facts)) };
    }
    private static readonly ConcurrentDictionary<string, PromptTemplate> _templates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> _fragments = new(StringComparer.Ordinal);

    static PromptCatalog()
    {
        TryAutoLoadTemplates();
    }

    public static void LoadTemplatesFromDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath)) return;
        foreach (var file in Directory.GetFiles(directoryPath, "*.json"))
        {
            try
            {
                var tmpl = JsonSerializer.Deserialize<PromptTemplate>(File.ReadAllText(file));
                if (tmpl == null || string.IsNullOrWhiteSpace(tmpl.Id)) continue;
                if (tmpl.Fragments != null)
                    foreach (var pair in tmpl.Fragments) _fragments[pair.Key] = pair.Value;
                else
                    _templates[tmpl.Id] = tmpl;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // An unreadable file leaves its prompt missing: the action using it reports that.
            }
        }
    }

    // Next to this assembly: inside Outlook BaseDirectory is the Office folder. Tests copy prompts\ next to it too.
    private static void TryAutoLoadTemplates()
    {
        foreach (var dir in new[] { Path.GetDirectoryName(typeof(PromptCatalog).Assembly.Location), AppDomain.CurrentDomain.BaseDirectory })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var prompts = Path.Combine(dir, "prompts");
            if (!Directory.Exists(prompts)) continue;
            LoadTemplatesFromDirectory(prompts);
            return;
        }
    }

    private static readonly Regex DataTagClose =
        new(@"</\s*(email|agenda|contatti|bozza|allegati|testo|note|mittente|precedenti|web|richiesta|contesto|fonte)\s*>", RegexOptions.IgnoreCase);

    /// <summary>
    /// Untrusted text for a data block (&lt;email&gt;, &lt;note&gt;…): a closing tag written in it would end the block
    /// and let what follows pass as an instruction.
    /// </summary>
    internal static string AsData(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : DataTagClose.Replace(text, m => "[/" + m.Groups[1].Value + "]");

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    // The model has no clock: without today's date it judges "this week" or "future" from its training data.
    private static string Today() => DateTime.Today.ToString("dddd d MMMM yyyy", Italian);

    private static readonly Regex FragmentRef = new(@"\{\{([\w.]+)\}\}", RegexOptions.CultureInvariant);
    private static readonly Regex Placeholder = new(@"\{(\w+)\}", RegexOptions.CultureInvariant);

    private static PromptTemplate Template(string id) =>
        _templates.TryGetValue(id, out var tmpl) ? tmpl
            : throw new InvalidOperationException($"Modello di prompt «{id}» mancante nella cartella prompts dell'installazione.");

    private static string Fragment(string name) =>
        _fragments.TryGetValue(name, out var text) ? text
            : throw new InvalidOperationException($"Frammento di prompt «{name}» mancante in prompts\\fragments.json.");

    /// <summary>
    /// Fragments first, then every {key} in one pass: a value is never searched again, so an email that contains
    /// "{instruction}" stays as it is. {max_words} and {oggi} are always available; unknown keys stay unchanged.
    /// </summary>
    private static string Fill(string text, PromptTemplate tmpl, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = FragmentRef.Replace(text, m => Fragment(m.Groups[1].Value));
        return Placeholder.Replace(text, m =>
        {
            var key = m.Groups[1].Value;
            if (values.TryGetValue(key, out var value)) return value;
            if (key == "max_words") return tmpl.MaxWords.ToString(CultureInfo.InvariantCulture);
            if (key == "oggi") return Today();
            return m.Value;
        });
    }

    /// <summary>System and user message of a template; a template with no user text gives the system message only.</summary>
    private static IEnumerable<ChatMessage> Render(string id, params (string Key, string Value)[] values)
    {
        var tmpl = Template(id);
        var map = values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        var list = new List<ChatMessage>();
        var system = Fill(tmpl.System, tmpl, map);
        if (system.Length > 0) list.Add(ChatMessage.System(system));
        var user = Fill(tmpl.UserTemplate, tmpl, map);
        if (user.Length > 0) list.Add(ChatMessage.User(user));
        return list;
    }

    /// <summary>Every template rendered with placeholder values: tests use it to find unfilled keys or missing fragments.</summary>
    internal static IEnumerable<(string Id, IEnumerable<ChatMessage> Messages)> RenderAllForTest(IReadOnlyDictionary<string, string> values) =>
        _templates.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(id => (id, Render(id, values.Select(v => (v.Key, v.Value)).ToArray())));

    public static IEnumerable<ChatMessage> CreateSummaryPrompt(string emailBody) =>
        Render("summary", ("email_body", AsData(emailBody)));

    public static IEnumerable<ChatMessage> CreateAcceptReplyPrompt(string emailBody) =>
        Render("reply.accept", ("email_body", AsData(emailBody)));

    public static IEnumerable<ChatMessage> CreateDeclineReplyPrompt(string emailBody) =>
        Render("reply.decline", ("email_body", AsData(emailBody)));

    public static IEnumerable<ChatMessage> CreateCustomReplyPrompt(string emailBody, string userInstruction) =>
        Render("reply.custom", ("email_body", AsData(emailBody)), ("instruction", userInstruction));

    /// <summary>Rewrites an existing draft following a new instruction; what the instruction does not touch stays.</summary>
    public static IEnumerable<ChatMessage> CreateRefineReplyPrompt(string emailBody, string draft, string userInstruction) =>
        Render("reply.refine", ("email_body", AsData(emailBody)), ("draft", AsData(draft)), ("instruction", userInstruction));

    /// <summary>Polite reminder for an email the user sent that got no answer.</summary>
    public static IEnumerable<ChatMessage> CreateFollowUpPrompt(string sentEmailBody, string recipients = "") =>
        Render("reply.followup", ("email_body", AsData(sentEmailBody)),
            ("recipients", string.IsNullOrWhiteSpace(recipients) ? "(non indicati)" : AsData(recipients)));

    /// <summary>A chat turn with no mail, agenda or contact data attached.</summary>
    public static IEnumerable<ChatMessage> CreateChatPrompt(string question) =>
        Render("chat.general", ("question", question));

    public static IEnumerable<ChatMessage> CreateAnswerPrompt(string userQuestion, string contextData, string contextTag = "email") =>
        Render("answer", ("context_tag", contextTag), ("context_data", AsData(contextData)), ("user_question", userQuestion));

    /// <summary>
    /// A text written from the user's instruction: the email being composed (what is written so far and the quoted
    /// message it answers) or new content with no context at all.
    /// </summary>
    public static IEnumerable<ChatMessage> CreateWritePrompt(string instruction, ComposeDraft? draft = null, string? emailBody = null, string? attachments = null)
    {
        var context = new System.Text.StringBuilder();
        if (draft != null)
        {
            context.Append("Email in scrittura").Append(draft.Kind switch { "reply" => " (risposta)", "forward" => " (inoltro)", _ => "" }).Append(":\n");
            context.Append("Oggetto: ").Append(string.IsNullOrWhiteSpace(draft.Subject) ? "(vuoto)" : draft.Subject).Append('\n');
            if (!string.IsNullOrWhiteSpace(draft.To)) context.Append("A: ").Append(draft.To).Append('\n');
            if (!string.IsNullOrWhiteSpace(draft.Cc)) context.Append("Cc: ").Append(draft.Cc).Append('\n');
            context.Append("<bozza>\n").Append(AsData(draft.OwnText)).Append("\n</bozza>\n");
            if (!string.IsNullOrWhiteSpace(draft.Quoted)) context.Append("<email>\n").Append(AsData(draft.Quoted)).Append("\n</email>\n");
            if (!string.IsNullOrWhiteSpace(attachments)) context.Append("<allegati>\n").Append(AsData(attachments)).Append("\n</allegati>\n");
            context.Append('\n');
        }
        else if (!string.IsNullOrWhiteSpace(emailBody))
        {
            context.Append("Email di riferimento:\n<email>\n").Append(AsData(emailBody)).Append("\n</email>\n\n");
        }
        return Render("compose.write", ("context", context.ToString()), ("instruction", instruction));
    }

    /// <summary>Rewrites offered by Migliora; the text of each is the fragment migliora.&lt;key&gt;.</summary>
    public static readonly IReadOnlyList<string> ImproveStyles = new[] { "correct", "formal", "friendly", "shorter", "clearer" };

    /// <summary>Languages offered by Traduci: key → name used in the prompt.</summary>
    public static readonly IReadOnlyDictionary<string, string> TranslateLanguages = new Dictionary<string, string>
    {
        ["it"] = "italiano",
        ["en"] = "inglese",
        ["fr"] = "francese",
        ["de"] = "tedesco",
        ["es"] = "spagnolo"
    };

    public static IEnumerable<ChatMessage> CreateImprovePrompt(string text, string style) =>
        Render("compose.improve", ("text", AsData(text)), ("style", Fragment("migliora." + (ImproveStyles.Contains(style) ? style : "correct"))));

    public static IEnumerable<ChatMessage> CreateTranslatePrompt(string text, string language) =>
        Render("compose.translate", ("text", AsData(text)), ("language", TranslateLanguages.TryGetValue(language, out var name) ? name : TranslateLanguages["en"]));

    public static IEnumerable<ChatMessage> CreateSubjectPrompt(string text) =>
        Render("compose.subject", ("text", AsData(text)));

    public const string ActionItemsJsonSchema =
        "{\n" +
        "  \"type\": \"object\",\n" +
        "  \"properties\": {\n" +
        "    \"items\": { \"type\": \"array\", \"items\": { \"type\": \"object\", \"properties\": {\n" +
        "      \"type\": { \"type\": \"string\", \"enum\": [\"task\", \"appointment\"] },\n" +
        "      \"title\": { \"type\": \"string\" },\n" +
        "      \"due\": { \"type\": \"string\" },\n" +
        "      \"start\": { \"type\": \"string\" },\n" +
        "      \"end\": { \"type\": \"string\" },\n" +
        "      \"location\": { \"type\": \"string\" },\n" +
        "      \"notes\": { \"type\": \"string\" }\n" +
        "    }, \"required\": [\"type\", \"title\"] } }\n" +
        "  },\n" +
        "  \"required\": [\"items\"]\n" +
        "}";

    /// <summary>
    /// Tasks and appointments explicitly requested or agreed in the email (and its attachments), as JSON.
    /// <paramref name="emailDate"/> resolves relative dates.
    /// </summary>
    public static IEnumerable<ChatMessage> CreateActionItemsPrompt(string emailBody, DateTime emailDate) =>
        Render("action.items", ("email_body", AsData(emailBody)), ("reference_date", emailDate.ToString("yyyy-MM-dd (dddd)", Italian)));

    /// <summary>Calendar items of a scan pack: each cites its email [Mn] and the date as written there.</summary>
    public const string CalendarScanJsonSchema =
        "{\n" +
        "  \"type\": \"object\",\n" +
        "  \"properties\": {\n" +
        "    \"items\": { \"type\": \"array\", \"items\": { \"type\": \"object\", \"properties\": {\n" +
        "      \"source\": { \"type\": \"string\" },\n" +
        "      \"type\": { \"type\": \"string\", \"enum\": [\"task\", \"appointment\"] },\n" +
        "      \"title\": { \"type\": \"string\" },\n" +
        "      \"date_text\": { \"type\": \"string\" },\n" +
        "      \"due\": { \"type\": \"string\" },\n" +
        "      \"start\": { \"type\": \"string\" },\n" +
        "      \"end\": { \"type\": \"string\" },\n" +
        "      \"location\": { \"type\": \"string\" },\n" +
        "      \"notes\": { \"type\": \"string\" }\n" +
        "    }, \"required\": [\"source\", \"type\", \"title\", \"date_text\"] } }\n" +
        "  },\n" +
        "  \"required\": [\"items\"]\n" +
        "}";

    /// <summary>Appuntamenti e scadenze (A109): one request on the candidate emails [M1]…[Mn], read whole, oldest first.</summary>
    public static IEnumerable<ChatMessage> CreateCalendarScanPrompt(string emails) =>
        Render("calendar.scan", ("emails", AsData(emails)));

    public const string BriefingJsonSchema =
        "{\n" +
        "  \"type\": \"object\",\n" +
        "  \"properties\": {\n" +
        "    \"summary\": { \"type\": \"string\" },\n" +
        "    \"emails\": { \"type\": \"array\", \"items\": { \"type\": \"object\", \"properties\": {\n" +
        "      \"id\": { \"type\": \"integer\" },\n" +
        "      \"priority\": { \"type\": \"string\", \"enum\": [\"alta\", \"media\", \"bassa\"] },\n" +
        "      \"category\": { \"type\": \"string\" },\n" +
        "      \"reason\": { \"type\": \"string\" }\n" +
        "    }, \"required\": [\"id\", \"priority\"] } }\n" +
        "  },\n" +
        "  \"required\": [\"summary\", \"emails\"]\n" +
        "}";

    /// <summary>Daily briefing: short summary plus triage of unread emails (priority, existing category, reason).</summary>
    public static IEnumerable<ChatMessage> CreateBriefingPrompt(string agenda, string unreadEmails, IReadOnlyCollection<string> categories, DateTime referenceDate) =>
        Render("briefing", ("agenda", AsData(agenda)), ("unread_emails", AsData(unreadEmails)),
            ("categories", categories.Count > 0 ? AsData(string.Join(", ", categories)) : "(nessuna: lascia category vuota)"),
            ("reference_date", referenceDate.ToString("dddd d MMMM yyyy", Italian)));

    public const string StructuredDataJsonSchema =
        "{\n" +
        "  \"type\": \"object\",\n" +
        "  \"properties\": {\n" +
        "    \"type\": { \"type\": \"string\" },\n" +
        "    \"fields\": { \"type\": \"array\", \"items\": { \"type\": \"object\", \"properties\": { \"name\": { \"type\": \"string\" }, \"value\": { \"type\": \"string\" } }, \"required\": [\"name\", \"value\"] } },\n" +
        "    \"lines\": { \"type\": \"array\", \"items\": { \"type\": \"object\", \"properties\": {\n" +
        "      \"description\": { \"type\": \"string\" }, \"quantity\": { \"type\": \"string\" }, \"unit_price\": { \"type\": \"string\" }, \"amount\": { \"type\": \"string\" } } } }\n" +
        "  },\n" +
        "  \"required\": [\"type\", \"fields\", \"lines\"]\n" +
        "}";

    /// <summary>Order/invoice/quote data as JSON fields and line items (values copied verbatim).</summary>
    public static IEnumerable<ChatMessage> CreateStructuredDataPrompt(string emailBody) =>
        Render("data.extract", ("email_body", AsData(emailBody)));

    // ---- One answer on a set of emails (E7: one request, cited and checked) ----

    private const string PointSchema =
        "{ \"type\": \"object\", \"properties\": { \"text\": { \"type\": \"string\" }, \"sources\": { \"type\": \"array\", \"items\": { \"type\": \"string\" } } }, \"required\": [\"text\", \"sources\"] }";

    /// <summary>Sections of points, each citing its emails [Mn], and what the emails leave open.</summary>
    public const string MailAnswerJsonSchema =
        "{ \"type\": \"object\", \"properties\": { \"sections\": { \"type\": \"array\", \"items\": { \"type\": \"object\", \"properties\": { " +
        "\"title\": { \"type\": \"string\" }, \"points\": { \"type\": \"array\", \"items\": " + PointSchema + " } }, \"required\": [\"title\", \"points\"] } }, " +
        "\"gaps\": { \"type\": \"array\", \"items\": { \"type\": \"string\" } } }, \"required\": [\"sections\", \"gaps\"] }";

    public const string MailTableJsonSchema =
        "{ \"type\": \"object\", \"properties\": { \"columns\": { \"type\": \"array\", \"items\": { \"type\": \"string\" } }, " +
        "\"rows\": { \"type\": \"array\", \"items\": { \"type\": \"object\", \"properties\": { \"source\": { \"type\": \"string\" }, " +
        "\"cells\": { \"type\": \"array\", \"items\": { \"type\": \"string\" } } }, \"required\": [\"source\", \"cells\"] } } }, \"required\": [\"columns\", \"rows\"] }";

    /// <summary>The single request on the emails [M1]…[Mn], oldest first: cards, or whole originals for the decisive ones.</summary>
    public static IEnumerable<ChatMessage> CreateMailAnswerPrompt(string goal, string emails) =>
        Render("mail.answer", ("goal", goal), ("emails", AsData(emails)));

    /// <summary>The table preset in one request: columns chosen and rows filled, each row citing its email.</summary>
    public static IEnumerable<ChatMessage> CreateMailTablePrompt(string goal, string emails) =>
        Render("mail.table", ("goal", goal), ("emails", AsData(emails)));

    /// <summary>Status of an email's conversation: precedents and, last, the email itself, numbered oldest first.</summary>
    public static IEnumerable<ChatMessage> CreateFollowUpStatusPrompt(string emails, int target) =>
        Render("followup.status", ("emails", AsData(emails)), ("target", target.ToString(CultureInfo.InvariantCulture)));

    public static IEnumerable<ChatMessage> CreateWebSearchPrompt(string action, string request) =>
        Render("web.search", ("action", AsData(action)), ("request", AsData(request)));

    /// <summary>Web search result added to a prompt, kept apart from the Outlook data and marked as data.</summary>
    public static ChatMessage CreateWebEvidenceMessage(string text, string sources) =>
        Render("web.evidence", ("text", AsData(text)), ("sources", AsData(sources))).Single();

    /// <summary>Note for the model when the web search failed: the answer rests on Outlook data and says so.</summary>
    public static ChatMessage CreateWebFailedMessage(string error) =>
        Render("web.failed", ("error", error)).Single();

    /// <summary>Categories the model may propose; the app decides what each means for the badge.</summary>
    public static readonly string[] SecurityCategories = { "credentials", "payment", "identity", "link", "pressure", "other" };

    public const string SecurityJsonSchema =
        "{\"type\":\"object\",\"properties\":{\"findings\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"category\":{\"type\":\"string\",\"enum\":[\"credentials\",\"payment\",\"identity\",\"link\",\"pressure\",\"other\"]}," +
        "\"reason\":{\"type\":\"string\"},\"sourceId\":{\"type\":\"string\"},\"quote\":{\"type\":\"string\"}}," +
        "\"required\":[\"category\",\"reason\",\"sourceId\",\"quote\"],\"additionalProperties\":false}}}," +
        "\"required\":[\"findings\"],\"additionalProperties\":false}";

    /// <summary>Quoted observations on the citable sources; the local context informs but is never a source.</summary>
    public static IEnumerable<ChatMessage> CreateSecurityPrompt(IEnumerable<SecuritySource> sources, string context) =>
        Render("security", ("sources", SecuritySources(sources)), ("context", AsData(context)));

    private static readonly Regex AttributeBreak = new("[\"<>\\r\\n]", RegexOptions.CultureInvariant);

    // Raw text, not JSON: escaped newlines, plus signs and accented letters would hide personal data from the dispatcher's mask.
    private static string SecuritySources(IEnumerable<SecuritySource> sources) =>
        string.Join("\n\n", sources.Select(s => "<fonte id=\"" + AttributeBreak.Replace(s.Id, " ") + "\" etichetta=\"" +
            AttributeBreak.Replace(AsData(s.Label), " ") + "\">\n" + AsData(s.Text) + "\n</fonte>"));
}
