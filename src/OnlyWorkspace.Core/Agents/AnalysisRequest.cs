using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Core.Agents;

/// <summary>What the "Cerca e analizza" form asks: where to look, the filters and the goal.</summary>
public sealed class AnalysisRequest
{
    /// <summary>thread | folders | accounts | archive</summary>
    public string Kind { get; set; } = "folders";
    public List<MailFolderRef> Folders { get; set; } = new();
    /// <summary>Accounts scope: store id and display name; empty = every open store.</summary>
    public List<KeyValuePair<string, string>> Stores { get; set; } = new();
    /// <summary>Accounts scope: the whole mailbox instead of the Inbox.</summary>
    public bool WholeMailbox { get; set; }
    public bool Subfolders { get; set; }
    /// <summary>Name or address, wildcards allowed; PersonRole says where it must appear.</summary>
    public string Person { get; set; } = string.Empty;
    /// <summary>sender | recipient | any</summary>
    public string PersonRole { get; set; } = "sender";
    public string Subject { get; set; } = string.Empty;
    /// <summary>Words the subject or the text must contain.</summary>
    public string Text { get; set; } = string.Empty;
    public string FactText { get; set; } = string.Empty;
    public List<ArchiveFactFilter> FactFilters { get; set; } = new();
    /// <summary>0 = no date limit.</summary>
    public int Days { get; set; }
    public bool Attachments { get; set; }
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public long ArchiveId { get; set; }
    public AnalysisGoal Goal { get; set; } = new();
    public int RequestId { get; set; }

    public bool IsAccounts => Kind is "accounts" or "inboxes";

    /// <summary>
    /// The request arrives as an object from the form, or as its JSON text when Rigenera/Riprova re-posts the op.
    /// <paramref name="raw"/> is the text to re-post; null when there is no scope or the text is not JSON.
    /// </summary>
    public static AnalysisRequest? Parse(JsonElement message, out string raw)
    {
        raw = string.Empty;
        JsonDocument? owned = null;
        try
        {
            JsonElement root = message;
            if (message.TryGetProperty("request", out var packed) && packed.ValueKind == JsonValueKind.String)
            {
                owned = JsonDocument.Parse(packed.GetString() ?? "{}");
                root = owned.RootElement;
            }
            if (!root.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object) return null;

            if ((Str(scope, "factText") ?? "").Length > 500) return null;
            var request = new AnalysisRequest
            {
                RequestId = root.TryGetProperty("requestId", out var req) && req.ValueKind == JsonValueKind.Number && req.TryGetInt32(out var requestId) ? requestId : 0,
                Kind = Str(scope, "kind") ?? "folders",
                WholeMailbox = scope.TryGetProperty("whole", out var whole) && whole.ValueKind == JsonValueKind.True,
                Subfolders = scope.TryGetProperty("subfolders", out var sub) && sub.ValueKind == JsonValueKind.True,
                Person = Truncate((Str(scope, "person") ?? string.Empty).Trim(), 200),
                PersonRole = Str(scope, "role") is "recipient" or "any" ? Str(scope, "role")! : "sender",
                Subject = Truncate((Str(scope, "subject") ?? string.Empty).Trim(), 200),
                FactText = (Str(scope, "factText") ?? string.Empty).Trim(),
                Text = Truncate((Str(scope, "text") ?? string.Empty).Trim(), 200),
                Days = scope.TryGetProperty("days", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out int days) ? Math.Max(0, Math.Min(days, 3650)) : 0,
                Attachments = scope.TryGetProperty("attachments", out var att) && att.ValueKind == JsonValueKind.True,
                EntryId = Str(scope, "entryId") ?? string.Empty,
                StoreId = Str(scope, "storeId") ?? string.Empty,
                ArchiveId = scope.TryGetProperty("id", out var flowId) && flowId.ValueKind == JsonValueKind.Number && flowId.TryGetInt64(out var archiveId) && archiveId > 0 ? archiveId : 0
            };
            if (scope.TryGetProperty("factFilters", out var filters) && filters.ValueKind == JsonValueKind.Array)
            {
                var parsed = JsonSerializer.Deserialize<List<ArchiveFactFilter>>(filters.GetRawText());
                if (parsed != null && parsed.Count <= 20) request.FactFilters = parsed;
                else throw new JsonException("Troppi filtri sui fatti.");
            }
            if (request.FactText.Length > 0)
            {
                if (request.Kind != "archive") return null;
                string unresolved = ArchiveSearchText.Parse(request.FactText, request.FactFilters);
                if (unresolved.Length > 0) return null; // Explicit filter input must never degrade to an unfiltered search.
            }
            if (scope.TryGetProperty("folders", out var folders) && folders.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in folders.EnumerateArray())
                {
                    var entryId = f.ValueKind == JsonValueKind.Object ? Str(f, "entryId") : null;
                    if (!string.IsNullOrEmpty(entryId))
                    {
                        request.Folders.Add(new MailFolderRef { EntryId = entryId!, StoreId = Str(f, "storeId") ?? string.Empty, Path = Str(f, "path") ?? string.Empty });
                    }
                }
            }
            if (scope.TryGetProperty("stores", out var stores) && stores.ValueKind == JsonValueKind.Array)
            {
                foreach (var st in stores.EnumerateArray())
                {
                    var id = st.ValueKind == JsonValueKind.Object ? Str(st, "id") : null;
                    if (!string.IsNullOrEmpty(id)) request.Stores.Add(new KeyValuePair<string, string>(id!, Str(st, "name") ?? string.Empty));
                }
            }
            if (root.TryGetProperty("goal", out var goal) && goal.ValueKind == JsonValueKind.Object)
            {
                var preset = Str(goal, "preset");
                request.Goal.Preset = preset is AnalysisGoal.Reconstruct or AnalysisGoal.OpenItems or AnalysisGoal.Table or AnalysisGoal.Calendar or AnalysisGoal.FollowUps or AnalysisGoal.Custom ? preset : AnalysisGoal.Summary;
                request.Goal.Instruction = Truncate((Str(goal, "instruction") ?? string.Empty).Trim(), 1000);
                // A custom search is its text: without it, a summary.
                if (request.Goal.Preset == AnalysisGoal.Custom && request.Goal.Instruction.Length == 0) request.Goal.Preset = AnalysisGoal.Summary;
            }

            if (request.Goal.IsReconstruction && (request.Kind != "flow" || request.EntryId.Length == 0 || request.StoreId.Length == 0)) return null;

            var packedRequest = new Dictionary<string, object> { ["scope"] = scope };
            if (root.TryGetProperty("goal", out var goalElement)) packedRequest["goal"] = goalElement;

            raw = JsonSerializer.Serialize(packedRequest);
            return request;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>Inbox / Sent Items / whole mailbox of the chosen accounts.</summary>
    public string AccountsLabel(string folderName)
    {
        string what = WholeMailbox ? "intera cassetta" : folderName;
        return Stores.Count switch
        {
            0 => WholeMailbox ? "tutte le cassette" : what + " di tutti gli account",
            1 => what + " di «" + Stores[0].Value + "»",
            _ => what + " di " + Stores.Count + " account"
        };
    }

    public string FiltersLabel()
    {
        var sb = new StringBuilder();
        if (Person.Length > 0)
        {
            sb.Append(PersonRole switch { "recipient" => ", destinatario «", "any" => ", da o a «", _ => ", mittente «" }).Append(Person).Append('»');
        }
        if (Subject.Length > 0) sb.Append(", oggetto «").Append(Subject).Append('»');
        if (FactFilters.Count > 0) sb.Append(", ").Append(FactFilters.Count).Append(" filtri sui fatti");
        if (Text.Length > 0) sb.Append(", testo «").Append(Text).Append('»');
        return sb.ToString();
    }

    /// <summary>Where and with which filters, as shown above the result.</summary>
    public string ScopeLabel()
    {
        var sb = new StringBuilder(Kind switch
        {
            "thread" => "conversazione selezionata",
            "flow" => "pratica dell'email scelta",
            "accounts" or "inboxes" => AccountsLabel("Posta in arrivo"),
            "archive" => "tutto l'archivio",
            _ => Folders.Count == 1 ? "cartella «" + LastSegment(Folders[0].Path) + "»" : Folders.Count + " cartelle"
        });
        if (Kind is not ("thread" or "flow"))
        {
            if (Subfolders && Kind != "archive" && !(IsAccounts && WholeMailbox)) sb.Append(" e sottocartelle");
            sb.Append(FiltersLabel());
            sb.Append(Days > 0 ? $", ultimi {Days} giorni" : ", senza limite di data");
        }
        return sb.ToString();
    }

    /// <summary>Filters of the form as a mailbox query (person on sender, recipients or either).</summary>
    public EmailSearchQuery ToQuery()
    {
        var query = new EmailSearchQuery
        {
            StartDate = Days > 0 ? DateTime.Today.AddDays(-Days) : (DateTime?)null,
            Subject = Subject.Length > 0 ? Subject : null,
            Keywords = Text.Length > 0 ? Text : null,
            EitherPerson = PersonRole == "any"
        };
        if (Person.Length > 0)
        {
            if (PersonRole != "recipient") query.Sender = Person;
            if (PersonRole != "sender") query.Recipient = Person;
        }
        return query;
    }

    private static string LastSegment(string path)
    {
        int cut = path.LastIndexOf('\\');
        return cut >= 0 && cut < path.Length - 1 ? path.Substring(cut + 1) : path;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string text, int max) =>
        text.Length > max ? text.Substring(0, max - 1).TrimEnd() + "…" : text;
}
