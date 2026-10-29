using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Infrastructure.Archive;

public sealed partial class ArchiveDb
{
    internal sealed class FactWork
    {
        internal long EmailId;
        internal string BodyVersion = "", Stamp = "";
        internal bool AttachmentText;
        internal ArchiveBody Body = new();
    }

    private void InitializeFacts(SqliteTransaction tx)
    {
        bool recover = !HasTable("archive_facts", tx) || !HasTable("archive_fact_jobs", tx);
        Exec("create table if not exists archive_facts(email_id integer not null,body_version text not null,revision integer not null,source_id text not null,source_name text not null," +
             "kind integer not null,label text not null,raw text not null,value text,currency text,date_value text,start integer not null,length integer not null,context text not null,resolved integer not null)", tx);
        Exec("create index if not exists ix_facts_email on archive_facts(email_id)", tx);
        Exec("create index if not exists ix_facts_value on archive_facts(kind,value,email_id)", tx);
        Exec("create index if not exists ix_facts_date on archive_facts(kind,date_value,email_id)", tx);
        Exec("create table if not exists archive_fact_jobs(email_id integer primary key,body_version text not null,revision integer not null,error text)", tx);
        if (recover) Exec("delete from archive_fact_jobs", tx);
        Exec("create trigger if not exists facts_email_deleted after delete on emails begin delete from archive_facts where email_id=old.id; delete from archive_fact_jobs where email_id=old.id; end", tx);
        Exec("create trigger if not exists facts_email_changed after update of body_state,body_version on emails when new.body_state<>1 or old.body_version is not new.body_version " +
             "begin delete from archive_facts where email_id=old.id; delete from archive_fact_jobs where email_id=old.id; end", tx);
        foreach (string operation in new[] { "delete", "update" })
            Exec("create trigger if not exists facts_attachment_" + operation + " after " + operation + " on attachments begin " +
                 "delete from archive_facts where email_id=old.email_id; delete from archive_fact_jobs where email_id=old.email_id; end", tx);
        Exec("create trigger if not exists facts_content_changed after update on attachment_contents when old.text is not new.text or old.reader_revision is not new.reader_revision begin " +
             "delete from archive_facts where email_id in (select email_id from attachments where content_hash=old.hash); " +
             "delete from archive_fact_jobs where email_id in (select email_id from attachments where content_hash=old.hash); end", tx);
        // Adding ICS support does not change the completeness contract of any existing reader.
        // Retry only excluded direct calendars and incomplete containers, once, when Outlook can reread them.
        if (Scalar<string?>("select value from meta where key='facts_calendar_reader'", tx) != "1")
        {
            Exec("update emails set body_state=0 where body_state=1 and id in (select email_id from attachments where " +
                 "(lower(name) like '%.ics' and state='unsupported') or (lower(name)='daticert.xml' and problem='inline-or-receipt') or ((lower(name) like '%.zip' or lower(name) like '%.eml' or lower(name) like '%.msg' or lower(name) like '%.p7m') and state in ('limited','unsupported'))) ", tx);
            Exec("insert or replace into meta(key,value) values('facts_calendar_reader','1')", tx);
        }
        _db.CreateFunction<string?, string?, long>("ow_decimal_compare", (a, b) =>
            decimal.TryParse(a, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var left) &&
            decimal.TryParse(b, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var right)
                ? Math.Sign(decimal.Compare(left, right)) : -2, isDeterministic: true);
    }

    private static string FactStamp(ArchiveBody body, bool attachments)
    {
        using var hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { body, attachments }))));
    }

    internal FactWork? NextFactWork()
    {
        lock (_gate)
        {
            if (GetMeta(ArchiveScope.PausedKey) == "1") return null;
            using var command = Command("select e.id,e.entry_id,f.store_id,e.body_version from emails e join folders f on f.id=e.folder_id left join archive_fact_jobs j on j.email_id=e.id " +
                "where e.body_state=1 and e.present=1 and e.full_body is not null and e.body_version is not null and " +
                "(j.email_id is null or j.body_version<>e.body_version or j.revision<>$rev) order by e.received desc,e.id desc limit 1", null, ("$rev", ArchiveFactExtractor.Revision));
            using var rows = command.ExecuteReader();
            if (!rows.Read()) return null;
            long id = rows.GetInt64(0); string entry = rows.GetString(1), store = rows.GetString(2), version = rows.GetString(3);
            rows.Close();
            var body = ReadOriginal(entry, store, id); if (body == null) return null;
            bool include = _indexAttachmentText != false;
            return new FactWork { EmailId = id, BodyVersion = version, Body = body, AttachmentText = include, Stamp = FactStamp(body, include) };
        }
    }

    internal static List<ArchiveFact> ExtractFacts(FactWork work, CancellationToken ct)
    {
        var result = ArchiveFactExtractor.Extract(work.Body.Text, ct: ct);
        if (work.AttachmentText)
            for (int i = 0; i < work.Body.Attachments.Count; i++)
            {
                var attachment = work.Body.Attachments[i];
                if (attachment.State is not ("indexed" or "facts-only") || attachment.TextLimited || attachment.ReaderRevision != ArchiveAttachment.CurrentReaderRevision) continue;
                string source = attachment.SourceId.Length > 0 ? attachment.SourceId : "attachment:" + i.ToString(CultureInfo.InvariantCulture);
                result.AddRange(ArchiveFactExtractor.Extract(attachment.Text, source, attachment.Name, ct));
            }
        foreach (var fact in result) { fact.EmailId = work.EmailId; fact.BodyVersion = work.BodyVersion; }
        return result;
    }

    internal bool CommitFacts(FactWork work, IReadOnlyList<ArchiveFact> facts, string? error = null)
    {
        lock (_gate)
        {
            if (GetMeta(ArchiveScope.PausedKey) == "1") return false;
            using var command = Command("select e.entry_id,f.store_id from emails e join folders f on f.id=e.folder_id where e.id=$id and e.body_version=$v and e.body_state=1 and e.present=1", null,
                ("$id", work.EmailId), ("$v", work.BodyVersion));
            using var rows = command.ExecuteReader(); if (!rows.Read()) return false;
            string entry = rows.GetString(0), store = rows.GetString(1); rows.Close();
            var current = ReadOriginal(entry, store, work.EmailId);
            if (current == null || FactStamp(current, _indexAttachmentText != false) != work.Stamp) return false;
            RequireIndexCapacity();
            using var tx = _db.BeginTransaction();
            Exec("delete from archive_facts where email_id=$id", tx, ("$id", work.EmailId));
            if (error == null)
                foreach (var fact in facts)
                    Exec("insert into archive_facts(email_id,body_version,revision,source_id,source_name,kind,label,raw,value,currency,date_value,start,length,context,resolved) " +
                         "values($id,$v,$rev,$source,$name,$kind,$label,$raw,$value,$currency,$date,$start,$length,$context,$resolved)", tx,
                        ("$id", work.EmailId), ("$v", work.BodyVersion), ("$rev", ArchiveFactExtractor.Revision), ("$source", fact.SourceId), ("$name", fact.SourceName),
                        ("$kind", (int)fact.Kind), ("$label", fact.Label), ("$raw", fact.Raw), ("$value", fact.Value), ("$currency", fact.Currency), ("$date", fact.Date),
                        ("$start", fact.Start), ("$length", fact.Length), ("$context", fact.Context), ("$resolved", fact.Resolved ? 1 : 0));
            Exec("insert into archive_fact_jobs(email_id,body_version,revision,error) values($id,$v,$rev,$error) on conflict(email_id) do update set body_version=excluded.body_version,revision=excluded.revision,error=excluded.error", tx,
                ("$id", work.EmailId), ("$v", work.BodyVersion), ("$rev", ArchiveFactExtractor.Revision), ("$error", error));
            tx.Commit(); return true;
        }
    }

    /// <summary>
    /// Under _gate: whether the facts of these emails are pending, failed or of another revision or body version.
    /// A failed job elsewhere in the archive must not mark this flow: a failure is not retried at the same revision.
    /// </summary>
    private bool FactsIncomplete(IEnumerable<long> emailIds)
    {
        var ids = emailIds.ToList();
        return ids.Count > 0 && Scalar<long>("select exists(select 1 from emails e left join archive_fact_jobs j on j.email_id=e.id where e.id in (" + InlineIds(ids) + ") " +
            "and e.body_state=1 and e.present=1 and e.full_body is not null and (j.email_id is null or j.body_version<>e.body_version or j.revision<>$rev or j.error is not null))",
            null, ("$rev", ArchiveFactExtractor.Revision)) != 0;
    }

    public ArchiveFactProgress GetFactProgress()
    {
        lock (_gate)
        {
            var progress = new ArchiveFactProgress();
            using var command = Command("select case when j.email_id is null or j.body_version<>e.body_version or j.revision<>$rev then 0 when j.error is null then 1 else 2 end,count(*) " +
                "from emails e left join archive_fact_jobs j on j.email_id=e.id where e.body_state=1 and e.present=1 and e.full_body is not null group by 1", null, ("$rev", ArchiveFactExtractor.Revision));
            using var rows = command.ExecuteReader();
            while (rows.Read()) { int count = rows.GetInt32(1); switch (rows.GetInt32(0)) { case 0: progress.Pending = count; break; case 1: progress.Done = count; break; case 2: progress.Failed = count; break; } }
            return progress;
        }
    }

    public IReadOnlyList<ArchiveFact> GetFacts(long emailId, bool includeAttachmentText)
    {
        lock (_gate)
        {
            var result = new List<ArchiveFact>();
            using var command = Command("select x.body_version,x.source_id,x.source_name,x.kind,x.label,x.raw,x.value,x.currency,x.date_value,x.start,x.length,x.context,x.resolved " +
                "from archive_facts x join emails e on e.id=x.email_id join archive_fact_jobs j on j.email_id=e.id where x.email_id=$id and e.body_state=1 and e.present=1 " +
                "and x.body_version=e.body_version and x.revision=$rev and j.revision=$rev and j.body_version=e.body_version and j.error is null " +
                (includeAttachmentText && _indexAttachmentText != false ? "" : "and x.source_id='' ") + "order by x.source_id,x.start,x.kind", null, ("$id", emailId), ("$rev", ArchiveFactExtractor.Revision));
            using var r = command.ExecuteReader();
            while (r.Read()) result.Add(new ArchiveFact { EmailId = emailId, BodyVersion = r.GetString(0), SourceId = r.GetString(1), SourceName = r.GetString(2), Kind = (ArchiveFactKind)r.GetInt32(3),
                Label = r.GetString(4), Raw = r.GetString(5), Value = r.IsDBNull(6) ? null : r.GetString(6), Currency = r.IsDBNull(7) ? null : r.GetString(7), Date = r.IsDBNull(8) ? null : r.GetString(8),
                Start = r.GetInt32(9), Length = r.GetInt32(10), Context = r.GetString(11), Resolved = r.GetInt64(12) != 0 });
            return result;
        }
    }

    private void AppendFactFilters(StringBuilder sql, List<(string, object?)> args, ArchiveQuery query)
    {
        for (int i = 0; i < query.FactFilters.Count; i++)
        {
            var filter = query.FactFilters[i];
            string prefix = "$fact" + i.ToString(CultureInfo.InvariantCulture);
            void Add(string suffix, object? value) => args.Add((prefix + suffix, value));
            if (filter.AmountMin.HasValue && filter.AmountMax.HasValue && filter.AmountMin > filter.AmountMax) throw new ArgumentException("Intervallo importi non valido.");
            foreach (string? day in new[] { filter.DateFrom, filter.DateTo })
                if (day != null && !DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new ArgumentException("Data del fatto non valida.");
            if (filter.DateFrom != null && filter.DateTo != null && string.CompareOrdinal(filter.DateFrom, filter.DateTo) > 0) throw new ArgumentException("Intervallo scadenze non valido.");
            sql.Append("and exists(select 1 from archive_facts x join archive_fact_jobs j on j.email_id=x.email_id where x.email_id=e.id and e.body_state=1 and e.present=1 and x.body_version=e.body_version and x.revision=")
                .Append(ArchiveFactExtractor.Revision).Append(" and j.revision=x.revision and j.body_version=e.body_version and j.error is null and x.resolved=1 ");
            if (!query.IncludeAttachmentText || _indexAttachmentText == false) sql.Append("and x.source_id='' ");
            if (filter.Kind.HasValue) { sql.Append("and x.kind=").Append(prefix).Append("kind "); Add("kind", (int)filter.Kind.Value); }
            if (filter.Value != null) { sql.Append("and x.value=").Append(prefix).Append("value "); Add("value", filter.Value); }
            if (filter.Currency != null) { sql.Append("and x.currency=").Append(prefix).Append("currency "); Add("currency", filter.Currency.Trim().ToUpperInvariant()); }
            if (filter.AmountMin.HasValue || filter.AmountMax.HasValue)
            {
                sql.Append("and x.kind=").Append((int)ArchiveFactKind.Amount).Append(' ');
                if (filter.AmountMin.HasValue) { sql.Append("and ow_decimal_compare(x.value,").Append(prefix).Append("min) in (0,1) "); Add("min", filter.AmountMin.Value.ToString("0.############################", CultureInfo.InvariantCulture)); }
                if (filter.AmountMax.HasValue) { sql.Append("and ow_decimal_compare(x.value,").Append(prefix).Append("max) in (-1,0) "); Add("max", filter.AmountMax.Value.ToString("0.############################", CultureInfo.InvariantCulture)); }
            }
            if (filter.DateFrom != null) { sql.Append("and x.date_value>=").Append(prefix).Append("from "); Add("from", filter.DateFrom); }
            if (filter.DateTo != null) { sql.Append("and x.date_value<=").Append(prefix).Append("to "); Add("to", filter.DateTo); }
            sql.Append(") ");
        }
    }
}
