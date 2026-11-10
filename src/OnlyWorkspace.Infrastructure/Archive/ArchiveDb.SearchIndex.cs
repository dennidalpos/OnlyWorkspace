using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Infrastructure.Archive;

public sealed partial class ArchiveDb
{
    internal sealed class SearchWork
    {
        public long Id;
        public string Body = "", Attachments = "", Stamp = "";
    }

    // A surrounding UPSERT's DO UPDATE uses ABORT even inside a trigger with OR IGNORE.
    // Remove existing jobs and duplicate source ids before inserting, independently of that policy.
    // https://www.sqlite.org/lang_createtrigger.html and https://www.sqlite.org/lang_upsert.html
    private static string QueueSearch(string ids) =>
        "insert into archive_search_jobs select distinct candidate.email_id from (" + ids + ") candidate " +
        "where not exists(select 1 from archive_search_jobs queued where queued.email_id=candidate.email_id); ";

    private void InitializeSearch(SqliteTransaction tx)
    {
        bool recover = !HasTable("code_body_fts", tx) || !HasTable("code_attachment_fts", tx) || !HasTable("archive_search_jobs", tx);
        bool recoverReferences = !HasTable("archive_message_refs", tx);
        _db.CreateFunction<string?, string?, long>("ow_code_contains", (text, fragment) =>
            fragment != null && ArchiveSearchText.Codes(text).Any(c => c.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) ? 1 : 0, isDeterministic: true);
        foreach (var column in new[] { ("sent_utc", "integer"), ("internet_references", "text"), ("thread_metadata_complete", "integer not null default 0") })
            if (Scalar<long>("select count(*) from pragma_table_info('emails') where name=$name", tx, ("$name", column.Item1)) == 0)
                Exec("alter table emails add column " + column.Item1 + " " + column.Item2, tx);
        Exec("create table if not exists archive_message_refs(email_id integer not null,kind integer not null,value text not null,primary key(email_id,kind,value)) without rowid", tx);
        Exec("create index if not exists ix_archive_message_refs_value on archive_message_refs(kind,value,email_id)", tx);
        Exec("create table if not exists archive_search_jobs(email_id integer primary key)", tx);
        foreach (string name in new[] { "body", "attachment" })
            Exec("create virtual table if not exists code_" + name + "_fts using fts5(code,content='',contentless_delete=1,tokenize='trigram')", tx);
        if (recover) Exec("insert or ignore into archive_search_jobs select id from emails", tx);
        if (Scalar<string?>("select value from meta where key='search_trigger_revision'", tx) != "2")
        {
            // Replace the persisted definitions atomically without rebuilding texts or pending work.
            foreach (string name in new[] { "search_email_inserted", "search_email_changed", "search_attachments_insert", "search_attachments_update", "search_attachments_delete", "search_content_update", "search_content_delete" })
                Exec("drop trigger if exists " + name, tx);
        }
        const string clear = "delete from code_body_fts where rowid=old.id; delete from code_attachment_fts where rowid=old.id; ";
        Exec("create trigger if not exists search_email_deleted after delete on emails begin " + clear +
             "delete from archive_search_jobs where email_id=old.id; delete from archive_message_refs where email_id=old.id; end", tx);
        Exec("create trigger if not exists search_email_inserted after insert on emails begin " + QueueSearch("select new.id as email_id") + "end", tx);
        Exec("create trigger if not exists search_email_changed after update of subject,preview,full_body,body_state on emails when old.subject is not new.subject or old.preview is not new.preview or old.full_body is not new.full_body or old.body_state<>new.body_state begin " + clear +
             QueueSearch("select new.id as email_id") + "end", tx);
        foreach (string table in new[] { "attachments" })
            foreach (string operation in new[] { "insert", "update", "delete" })
            {
                string row = operation == "delete" ? "old" : "new";
                string target = "attachment";
                Exec("create trigger if not exists search_" + table + "_" + operation + " after " + operation + " on " + table + " begin " +
                     "delete from code_" + target + "_fts where rowid=" + row + ".email_id; " + QueueSearch("select " + row + ".email_id as email_id") + "end", tx);
            }
        foreach (string operation in new[] { "update", "delete" })
            Exec("create trigger if not exists search_content_" + operation + " after " + operation + " on attachment_contents begin " +
                 "delete from code_attachment_fts where rowid in(select email_id from attachments where content_hash=old.hash); " +
                 QueueSearch("select email_id from attachments where content_hash=old.hash") + "end", tx);
        Exec("insert or replace into meta(key,value) values('search_trigger_revision','2')", tx);
        if (recoverReferences || Scalar<string?>("select value from meta where key='search_revision'", tx) != "1")
        {
            Exec("insert or ignore into archive_search_jobs select id from emails", tx);
            // Existing headers are already useful until Outlook can supply the new optional metadata.
            Exec("insert or ignore into archive_message_refs select id,0,message_id from emails where coalesce(message_id,'')<>''", tx);
            Exec("insert or ignore into archive_message_refs select id,0,in_reply_to from emails where coalesce(in_reply_to,'')<>''", tx);
            Exec("insert or ignore into archive_message_refs select id,1,conv_id from emails where coalesce(conv_id,'')<>''", tx);
            Exec("insert or replace into meta(key,value) values('search_revision','1')", tx);
        }
        if (recoverReferences)
        {
            long after = 0;
            while (true)
            {
                var batch = new List<(long Id, string References)>();
                using (var command = Command("select id,internet_references from emails where id>$after and coalesce(internet_references,'')<>'' order by id limit 500", tx, ("$after", after)))
                using (var rows = command.ExecuteReader()) while (rows.Read()) batch.Add((rows.GetInt64(0), rows.GetString(1)));
                if (batch.Count == 0) break;
                foreach (var mail in batch)
                    foreach (string reference in ArchiveSearchText.MessageIds(mail.References).Distinct(StringComparer.Ordinal))
                        Exec("insert or ignore into archive_message_refs values($id,0,$reference)", tx, ("$id", mail.Id), ("$reference", reference));
                after = batch[batch.Count - 1].Id;
            }
        }
    }

    private SearchWork? ReadSearchWork(long id)
    {
        using var command = Command("select subject,case when body_state=1 then coalesce(full_body,preview,'') else coalesce(preview,'') end,body_state from emails where id=$id", null, ("$id", id));
        using var rows = command.ExecuteReader();
        if (!rows.Read()) return null;
        var work = new SearchWork { Id = id, Body = rows.GetInt32(2) == 3 ? "" : (rows.IsDBNull(0) ? "" : rows.GetString(0)) + "\n" + rows.GetString(1) };
        bool indexed = rows.GetInt32(2) == 1; rows.Close();
        if (indexed && _indexAttachmentText != false)
        {
            using var files = Command("select a.name,coalesce(c.text,'') from attachments a left join attachment_contents c on c.hash=a.content_hash " +
                "where a.email_id=$id and a.state<>'facts-only' and (c.hash is null or c.reader_revision=$rev) and (a.state<>'indexed' or a.reader_revision=$rev) order by a.rowid", null,
                ("$id", id), ("$rev", ArchiveAttachment.CurrentReaderRevision));
            using var reader = files.ExecuteReader(); var texts = new List<string>();
            while (reader.Read()) { texts.Add(reader.GetString(0)); texts.Add(reader.GetString(1)); }
            work.Attachments = string.Join("\n", texts);
        }
        work.Stamp = JsonSerializer.Serialize(new[] { work.Body, work.Attachments });
        return work;
    }

    internal SearchWork? NextSearchWork()
    {
        lock (_gate)
        {
            if (GetMeta(ArchiveScope.PausedKey) == "1") return null;
            // A header or gone row can still need its stale terms removed.
            var id = Scalar<long?>("select j.email_id from archive_search_jobs j join emails e on e.id=j.email_id order by j.email_id limit 1");
            return id.HasValue ? ReadSearchWork(id.Value) : null;
        }
    }

    private string CodeCandidates(string code, string parameter, ArchiveQuery query)
    {
        if (code.Length < 3)
            return "select x.email_id as rowid from archive_facts x join emails e on e.id=x.email_id join archive_fact_jobs j on j.email_id=e.id where " +
                "e.body_state=1 and e.present=1 and x.body_version=e.body_version and j.body_version=e.body_version and x.revision=" + ArchiveFactExtractor.Revision +
                " and j.revision=x.revision and j.error is null and x.resolved=1 and x.kind in (3,4,5) and x.value=" + parameter +
                (query.IncludeAttachmentText && _indexAttachmentText != false ? "" : " and x.source_id=''");
        var sources = new List<string> { "select rowid from code_body_fts where code_body_fts match " + parameter + "fts" };
        if (query.IncludeAttachmentText && _indexAttachmentText != false)
            sources.Add("select c.rowid from code_attachment_fts c join emails e on e.id=c.rowid where code_attachment_fts match " + parameter + "fts and e.body_state=1");
        // Until recovery completes, inspect the acquired originals instead of claiming that no match exists.
        // OFFSET prevents flattening: with no pending jobs SQLite must not evaluate source-text
        // functions for every email before checking the empty jobs join.
        string fallback = "select e.id as rowid from (select source.* from archive_search_jobs j join emails source on source.id=j.email_id limit -1 offset 0) e where e.body_state<>3 and (" +
            "ow_code_contains(coalesce(e.subject,'') || char(10) || coalesce(case when e.body_state=1 then e.full_body else e.preview end,'')," + parameter + ")=1";
        if (query.IncludeAttachmentText && _indexAttachmentText != false)
            fallback += " or (e.body_state=1 and exists(select 1 from attachments a left join attachment_contents c on c.hash=a.content_hash where a.email_id=e.id " +
                "and a.state<>'facts-only' and (c.hash is null or c.reader_revision=" + ArchiveAttachment.CurrentReaderRevision + ") and " +
                "(a.state<>'indexed' or a.reader_revision=" + ArchiveAttachment.CurrentReaderRevision + ") and ow_code_contains(a.name || char(10) || coalesce(c.text,'')," + parameter + ")=1))";
        sources.Add(fallback + ")");
        return string.Join(" union ", sources);
    }

    internal bool CommitSearch(SearchWork work, string body, string attachments)
    {
        lock (_gate)
        {
            if (GetMeta(ArchiveScope.PausedKey) == "1" || ReadSearchWork(work.Id)?.Stamp != work.Stamp) return false;
            RequireIndexCapacity();
            using var tx = _db.BeginTransaction();
            foreach (var source in new[] { ("body", body), ("attachment", attachments) })
            {
                Exec("delete from code_" + source.Item1 + "_fts where rowid=$id", tx, ("$id", work.Id));
                if (source.Item2.Length > 0) Exec("insert into code_" + source.Item1 + "_fts(rowid,code) values($id,$code)", tx, ("$id", work.Id), ("$code", source.Item2));
            }
            Exec("delete from archive_search_jobs where email_id=$id", tx, ("$id", work.Id)); tx.Commit(); return true;
        }
    }

    private void UpdateThreadMetadata(long id, ArchiveHeader header, SqliteTransaction tx)
    {
        // Fallback header readers must not erase identifiers acquired by a more capable store reader.
        Exec("update emails set message_id=case when $m<>'' then $m else message_id end," +
             "in_reply_to=case when $complete=1 or $reply<>'' then $reply else in_reply_to end," +
             "conv_id=case when $conv<>'' then $conv else conv_id end," +
             "internet_references=case when $complete=1 or $refs<>'' then $refs else internet_references end," +
             "sent_utc=coalesce($sent,sent_utc),thread_metadata_complete=max(thread_metadata_complete,$complete) where id=$id", tx,
            ("$id", id), ("$m", header.MessageId.Trim()), ("$reply", header.InReplyTo.Trim()), ("$conv", header.ConversationId),
            ("$refs", header.References), ("$sent", header.SentUtc.HasValue ? ToUnix(header.SentUtc.Value) : (long?)null), ("$complete", header.ThreadMetadataComplete ? 1 : 0));
        using var command = Command("select message_id,in_reply_to,conv_id,internet_references from emails where id=$id", tx, ("$id", id));
        using var row = command.ExecuteReader(); if (!row.Read()) return;
        string message = row.IsDBNull(0) ? "" : row.GetString(0), reply = row.IsDBNull(1) ? "" : row.GetString(1), conv = row.IsDBNull(2) ? "" : row.GetString(2), refs = row.IsDBNull(3) ? "" : row.GetString(3); row.Close();
        Exec("delete from archive_message_refs where email_id=$id", tx, ("$id", id));
        foreach (string value in new[] { message, reply }.Where(x => x.Length > 0).Concat(ArchiveSearchText.MessageIds(refs)).Distinct(StringComparer.Ordinal))
            Exec("insert or ignore into archive_message_refs values($id,0,$v)", tx, ("$id", id), ("$v", value));
        if (conv.Length > 0) Exec("insert or ignore into archive_message_refs values($id,1,$v)", tx, ("$id", id), ("$v", conv));
    }
}
