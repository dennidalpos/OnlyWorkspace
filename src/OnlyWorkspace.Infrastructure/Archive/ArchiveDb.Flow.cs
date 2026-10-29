using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Infrastructure.Archive;

public sealed partial class ArchiveDb
{
    private const int MaxFlowEmails = 20000;
    private const string FlowProjection = "e.id,e.entry_id,f.store_id,e.message_id,e.in_reply_to,e.conv_id,e.subject,coalesce(nullif(e.sender_name,''),a.addr,''),e.received,e.body_state," +
        "(select json_group_array(name) from attachments where email_id=e.id)," +
        "e.sent_utc,e.internet_references,e.thread_metadata_complete,f.path";
    private const string FlowFactSource = "not exists(select 1 from attachments receipt where receipt.email_id=x.email_id and receipt.state='facts-only' " +
        "and ((x.source_id<>'' and receipt.source_id=x.source_id) or (coalesce(receipt.source_id,'')='' and receipt.name=x.source_name)))";
    private const string FlowFrom = " from emails e join folders f on f.id=e.folder_id left join addresses a on a.id=e.sender_id ";

    public ArchiveFlow GetFlow(ArchiveFlowSeed seed, int max = MaxFlowEmails)
    {
        if (seed == null) throw new ArgumentNullException(nameof(seed));
        max = Math.Max(1, Math.Min(int.MaxValue - 1, max));
        lock (_gate)
        {
            long id = seed.Id;
            if (id == 0 && seed.EntryId.Length > 0)
                id = Scalar<long?>("select e.id from emails e join folders f on f.id=e.folder_id where e.entry_id=$e and f.store_id=$s and e.body_state<>3 limit 1", null,
                    ("$e", seed.EntryId), ("$s", seed.StoreId)) ?? 0;
            var args = new List<(string, object?)> { ("$seed", id) };
            string scope = "";
            if (seed.AllowedStoreIds != null)
            {
                var stores = seed.AllowedStoreIds.Select((store, i) => { args.Add(("$store" + i, store)); return "$store" + i; }).ToArray();
                scope = " and f.store_id in (" + (stores.Length == 0 ? "null" : string.Join(",", stores)) + ")";
            }
            string facts = "select x.email_id,x.kind+100 kind,x.value from archive_facts x join emails e on e.id=x.email_id join archive_fact_jobs j on j.email_id=e.id " +
                "join eligible allowed on allowed.id=e.id where e.body_state=1 and e.present=1 and x.body_version=e.body_version and j.body_version=e.body_version and x.revision=" + ArchiveFactExtractor.Revision +
                " and j.revision=x.revision and j.error is null and x.resolved=1 and x.kind in (3,4,5) and " + FlowFactSource +
                (seed.IncludeAttachmentText && _indexAttachmentText != false ? "" : " and x.source_id=''");
            var anchors = new List<string> { "select kind,value from links where email_id=$seed" };
            foreach (var key in new[] { seed.MessageId, seed.InReplyTo }.Where(v => v.Length > 0).Concat(ArchiveSearchText.MessageIds(seed.References)).Distinct(StringComparer.Ordinal))
            {
                string parameter = "$anchor" + args.Count; args.Add((parameter, key)); anchors.Add("select 0," + parameter);
            }
            if (seed.ConversationId.Length > 0) { args.Add(("$conv", seed.ConversationId)); anchors.Add("select 1,$conv"); }
            // Traverse identifier hubs once, rather than every pair in a large conversation.
            string cte = "with recursive eligible as materialized (select e.id from emails e join folders f on f.id=e.folder_id where e.body_state<>3" + scope + ")," +
                "links as materialized (select r.email_id,r.kind,r.value from archive_message_refs r join eligible allowed on allowed.id=r.email_id union " + facts + ")," +
                "nodes(kind,value) as (" + string.Join(" union ", anchors) + " union select next.kind,next.value from nodes n join links here on here.kind=n.kind and here.value=n.value " +
                "join links next on next.email_id=here.email_id), connected(id) as (select id from eligible where id=$seed union select l.email_id from nodes n join links l on l.kind=n.kind and l.value=n.value) ";
            var flow = new ArchiveFlow();
            var physical = new List<ArchiveFlowMail>();
            using (var command = Command(cte + "select " + FlowProjection + FlowFrom + "where e.id in (select id from connected) order by coalesce(e.sent_utc,e.received),e.id", null, args.ToArray()))
            using (var rows = command.ExecuteReader()) while (rows.Read()) physical.Add(ReadFlowMail(rows));
            // Keep the reason and grounded occurrence distinct from a reply-parent relation.
            var connectedIds = physical.Select(m => m.Id).ToHashSet();
            using (var command = Command(cte + "select x.email_id,x.kind,x.value,x.source_id,x.source_name,x.context,x.start,x.length,x.raw,x.body_version " +
                "from archive_facts x join connected k on k.id=x.email_id join links l on l.email_id=x.email_id and l.kind=x.kind+100 and l.value=x.value " +
                "where " + FlowFactSource + " and x.kind in (3,4,5) and exists(select 1 from links other where other.kind=l.kind and other.value=l.value and other.email_id<>l.email_id)" +
                (seed.IncludeAttachmentText && _indexAttachmentText != false ? "" : " and x.source_id=''"), null, args.ToArray()))
            using (var rows = command.ExecuteReader())
            {
                var mails = physical.ToDictionary(m => m.Id);
                while (rows.Read())
                {
                    if (!mails.TryGetValue(rows.GetInt64(0), out var mail)) continue;
                    var fact = new ArchiveFact { EmailId = mail.Id, Kind = (ArchiveFactKind)rows.GetInt32(1), Value = rows.GetString(2), SourceId = rows.GetString(3), SourceName = rows.GetString(4),
                        Context = rows.GetString(5), Start = rows.GetInt32(6), Length = rows.GetInt32(7), Raw = rows.GetString(8), BodyVersion = rows.GetString(9), Resolved = true };
                    mail.LinkFacts.Add(fact); mail.LinkReasons.Add("Riferimento condiviso: " + (fact.Kind == ArchiveFactKind.Order ? "ordine" : fact.Kind == ArchiveFactKind.Invoice ? "fattura" : "protocollo") + " " + fact.Value);
                }
            }
            foreach (var mail in physical)
                if (mail.LinkReasons.Count == 0 && mail.Id != id) mail.LinkReasons.Add("Identificativi della conversazione");
            flow.MetadataIncomplete = physical.Any(m => !m.ThreadMetadataComplete);
            flow.FactsIncomplete = physical.Any(m => m.BodyState != ArchiveBodyState.Indexed) || FactsIncomplete(physical.Select(m => m.Id));
            var all = ArchiveFlowRules.Consolidate(physical, seed.StoreId, seed.EntryId);
            flow.LimitReached = all.Count > max; flow.Timeline = all.Take(max).ToList();
            if (physical.FirstOrDefault(m => m.Id == id) is { } origin)
            { seed.Subject = origin.Subject; seed.ReceivedUtc = origin.ReceivedUtc; seed.SenderAddress = Scalar<string?>("select a.addr from emails e left join addresses a on a.id=e.sender_id where e.id=$id", null, ("$id", id)) ?? ""; }
            var subject = ArchiveText.NormalizeSubject(seed.Subject); var sender = CounterpartyResolver.Normalize(seed.SenderAddress);
            if ((subject.Length > 0 || sender.Length > 0) && !flow.LimitReached)
            {
                var possibleArgs = args.Where(a => a.Item1.StartsWith("$store", StringComparison.Ordinal)).ToList();
                possibleArgs.AddRange(new (string, object?)[] { ("$subject", subject), ("$sender", sender), ("$from", seed.ReceivedUtc.HasValue ? ToUnix(seed.ReceivedUtc.Value.AddDays(-30)) : long.MinValue),
                    ("$to", seed.ReceivedUtc.HasValue ? ToUnix(seed.ReceivedUtc.Value.AddDays(30)) : long.MaxValue) });
                using var command = Command("select " + FlowProjection + FlowFrom + "where e.body_state<>3" + scope +
                    " and ((e.subject_norm=$subject and $subject<>'') or (a.addr=$sender and $sender<>'')) and e.received between $from and $to order by e.received,e.id", null, possibleArgs.ToArray());
                using var rows = command.ExecuteReader(); var possible = new List<ArchiveFlowMail>();
                var knownMessages = new HashSet<string>(physical.Where(m => m.MessageId.Length > 0).Select(m => m.MessageId), StringComparer.Ordinal);
                while (rows.Read()) { var mail = ReadFlowMail(rows); if (!connectedIds.Contains(mail.Id) && (mail.MessageId.Length == 0 || !knownMessages.Contains(mail.MessageId))) possible.Add(mail); }
                flow.PossibleLinks = ArchiveFlowRules.Consolidate(possible).Take(20).ToList();
            }
            LinkFlowParents(flow.Timeline); return flow;
        }
    }

    private static ArchiveFlowMail ReadFlowMail(Microsoft.Data.Sqlite.SqliteDataReader row)
    {
        var mail = new ArchiveFlowMail
        {
            Id = row.GetInt64(0), EntryId = row.GetString(1), StoreId = row.GetString(2), MessageId = row.IsDBNull(3) ? "" : row.GetString(3),
            InReplyTo = row.IsDBNull(4) ? "" : row.GetString(4), ConversationId = row.IsDBNull(5) ? "" : row.GetString(5), Subject = row.IsDBNull(6) ? "" : row.GetString(6),
            Sender = row.IsDBNull(7) ? "" : row.GetString(7), ReceivedUtc = FromUnix(row.GetInt64(8)), BodyState = (ArchiveBodyState)row.GetInt32(9),
            SentUtc = row.IsDBNull(11) ? null : FromUnix(row.GetInt64(11)), References = row.IsDBNull(12) ? "" : row.GetString(12), ThreadMetadataComplete = row.GetInt32(13) != 0, FolderPath = row.GetString(14)
        };
        if (!row.IsDBNull(10)) mail.Attachments.AddRange(JsonSerializer.Deserialize<string[]>(row.GetString(10)) ?? Array.Empty<string>());
        return mail;
    }

    public static void LinkFlowParents(IReadOnlyList<ArchiveFlowMail> emails)
    {
        var byMessage = emails.Where(e => e.MessageId.Length > 0).GroupBy(e => e.MessageId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var mail in emails)
        {
            mail.ParentEntryId = ""; mail.ParentStoreId = "";
            var identifiers = new[] { mail.InReplyTo }.Concat(ArchiveSearchText.MessageIds(mail.References).AsEnumerable().Reverse());
            foreach (string key in identifiers)
                if (key.Length > 0 && byMessage.TryGetValue(key, out var parent) && !ReferenceEquals(parent, mail))
                { mail.ParentEntryId = parent.EntryId; mail.ParentStoreId = parent.StoreId; break; }
        }
    }

    /// <summary>Outlook folder paths read \\Mailbox\Folder: the first segment names the mailbox.</summary>
    internal static string StoreNameOf(string? path)
    {
        var trimmed = (path ?? string.Empty).TrimStart('\\');
        int slash = trimmed.IndexOf('\\');
        return slash < 0 ? trimmed : trimmed.Substring(0, slash);
    }

    public IReadOnlyList<ArchiveStoreCoverage> GetStoreCoverage()
    {
        lock (_gate)
        {
            var result = new List<ArchiveStoreCoverage>();
            using var command = Command(
                "select f.store_id,sum(case when f.listed=1 then 1 else 0 end),sum(case when f.listed=1 and f.done=1 and f.error is null then 1 else 0 end)," +
                "case when sum(case when f.listed=1 and f.verified_utc is null then 1 else 0 end)>0 then null else min(case when f.listed=1 then f.verified_utc end) end," +
                "null," + // folder errors are projected with their own paths below
                "case when sum(case when f.listed=1 then 1 else 0 end)=0 then 0 else min(case when f.listed=1 then f.absence_verified else 1 end) end," +
                "(select count(*) from emails e join folders f2 on f2.id=e.folder_id where f2.store_id=f.store_id and e.body_state<>3)," +
                "(select count(*) from emails e join folders f2 on f2.id=e.folder_id where f2.store_id=f.store_id and e.body_state=1)," +
                "(select count(*) from attachments a join emails e on e.id=a.email_id join folders f2 on f2.id=e.folder_id where f2.store_id=f.store_id and a.state='indexed'),min(f.path)," +
                "(select count(*) from emails e join folders f2 on f2.id=e.folder_id where f2.store_id=f.store_id and e.body_state in (0,4))," +
                "(select count(*) from attachments a join emails e on e.id=a.email_id join folders f2 on f2.id=e.folder_id where f2.store_id=f.store_id and e.body_state<>3)," +
                "sum(case when f.listed=2 then 1 else 0 end)," +
                "min(case when f.listed=1 then (case when f.verified=1 and f.pass=$p then 1 else 0 end) else 1 end) " +
                "from folders f group by f.store_id order by f.store_id", null, ("$p", _pass));
            // A cache store is checked once a closed full check has inventoried every folder and asked Outlook about each unseen email.
            bool checkClosed = GetMeta(MetaCheckOpen) != "1" && GetMeta(MetaCheckTime) != null;
            using (var reader = command.ExecuteReader())
            while (reader.Read()) result.Add(new ArchiveStoreCoverage
            {
                StoreId = reader.GetString(0), Folders = reader.GetInt32(1), FoldersDone = reader.GetInt32(2),
                LastInventoryUtc = reader.IsDBNull(3) ? null : FromUnix(reader.GetInt64(3)),
                Error = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                AbsenceVerified = reader.GetInt32(5) != 0,
                Emails = reader.GetInt32(6), BodiesIndexed = reader.GetInt32(7), AttachmentsIndexed = reader.GetInt32(8),
                DisplayName = StoreNameOf(reader.IsDBNull(9) ? null : reader.GetString(9)),
                BodiesPending = reader.GetInt32(10), AttachmentsTotal = reader.GetInt32(11),
                Closed = reader.GetInt32(1) == 0 && reader.GetInt32(12) > 0,
                CacheChecked = checkClosed && reader.GetInt32(1) > 0 && reader.GetInt32(5) == 0 && reader.GetInt32(13) == 1
            });
            // Why a mailbox's folder list stopped short (folder limit, depth, not reachable) comes before its folder errors.
            using (var errors = Command("select store_id,path,error from folders where listed=1 and error is not null and length(error)>0 order by store_id,path", null))
            using (var rows = errors.ExecuteReader())
            {
                var byStore = result.ToDictionary(s => s.StoreId, StringComparer.Ordinal);
                while (rows.Read())
                {
                    if (!byStore.TryGetValue(rows.GetString(0), out var state)) continue;
                    var folder = new ArchiveFolder { Path = rows.IsDBNull(1) ? string.Empty : rows.GetString(1) };
                    state.Error += (state.Error.Length == 0 ? string.Empty : " ") + folder.DescribeError(rows.GetString(2));
                }
            }
            var issues = GetMeta("folder_issues") is { } json ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) : null;
            foreach (var issue in issues ?? new Dictionary<string, string>())
            {
                var state = result.FirstOrDefault(s => string.Equals(s.StoreId, issue.Key, StringComparison.OrdinalIgnoreCase));
                if (state == null) result.Add(state = new ArchiveStoreCoverage { StoreId = issue.Key });
                state.Error = state.Error.Length > 0 ? issue.Value + " " + state.Error : issue.Value;
            }
            foreach (var state in result)
                state.State = state.Closed ? "chiusa" : state.Error.Length > 0 ? "errore" : state.Folders == 0 ? "in attesa"
                    : state.FoldersDone < state.Folders ? "inventario in corso"
                    : state.AbsenceVerified ? "inventario verificato" : state.CacheChecked ? "cache verificata" : "cache da controllare";
            return result;
        }
    }
}
