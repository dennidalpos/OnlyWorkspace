using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Infrastructure.Archive;

public sealed partial class ArchiveDb
{
    private bool HasTable(string name, SqliteTransaction tx) =>
        Scalar<long>("select count(*) from sqlite_master where type='table' and name=$n", tx, ("$n", name)) != 0;

    private void InitializeIndex(long version, CancellationToken ct, ArchiveMigrationOptions? options)
    {
        bool empty = Scalar<long>("select count(*) from sqlite_master where type='table' and name not like 'sqlite_%'") == 0;
        if (!empty && version != 11 && version != 12 && version != SchemaVersion)
            throw new InvalidDataException("Formato dell'archivio non supportato. I dati sono conservati; ripristina una versione supportata.");
        string? expected = !empty && version != SchemaVersion ? CreateMigrationBackup(version, ct, options) : null;
        using var tx = _db.BeginTransaction();
        if (expected != null)
        {
            if (version == 11) RetireStoredCards(tx);
            Exec("drop table if exists archive_case_closures", tx);
            Exec("delete from rpc_batches where operation='SetCaseClosed'", tx);
            if (PreservedDigest(_db, ct, tx) != expected)
                throw new InvalidDataException("La migrazione ha modificato dati da preservare; nessuna modifica confermata.");
            if (version == 11) FinishStoredCardRetirement(tx);
        }
        if (empty)
        {
            CreateSchema(tx);
            Exec("insert or replace into meta(key,value) values($k,$v)", tx, ("$k", MetaCheckTime), ("$v", UnixText(DateTime.UtcNow)));
        }
        // The address book flow is gone: its refusals and last scope go in every format, current included.
        Exec("drop table if exists contact_rejects", tx);
        Exec("delete from meta where key='contacts_scope'", tx);
        // Merging and splitting counterparties is gone: the addresses return to their automatic grouping.
        RetirePartyChoices(tx);
        Exec("create table if not exists archive_deletions(id integer primary key,profile text not null,store_id text not null,entry_id text not null,message_id text not null,deleted_ticks integer not null)", tx);
        Exec("create table if not exists archive_source_checks(id integer primary key,store_id text not null,entry_id text not null,message_id text not null,checked_pass integer not null default -1,unique(store_id,entry_id,message_id))", tx);
        EnsureReaderColumns(tx);
        RestoreOriginalIndexes(tx);
        // Reader revisions invalidate only derived attachment data, retaining acquired bodies.
        if (Scalar<string?>("select value from meta where key='attachment_reader_revision'", tx) != ArchiveAttachment.CurrentReaderRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            Exec("update emails set attachments_limited=1 where has_attach=1 or id in (select email_id from attachments)", tx);
            Exec("update emails set body_state=0 where body_state=1 and has_attach=1 and not exists(select 1 from attachments a where a.email_id=emails.id)", tx);
            Exec("update attachments set state=case when state='excluded' then state else 'pending' end,problem='reader-revision' where reader_revision<>$rev", tx, ("$rev", ArchiveAttachment.CurrentReaderRevision));
            Exec("delete from attachment_fts", tx);
            Exec("insert into attachment_fts(rowid,name,body) select rowid,name,'' from attachments", tx);
            Exec("insert or replace into meta(key,value) values('attachment_reader_revision',$rev)", tx,
                ("$rev", ArchiveAttachment.CurrentReaderRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        InitializeFacts(tx);
        InitializeSearch(tx);
        Exec("pragma user_version=" + SchemaVersion, tx);
        ct.ThrowIfCancellationRequested();
        options?.BeforeCommit?.Invoke();
        ct.ThrowIfCancellationRequested();
        tx.Commit();
    }

    private void RestoreOriginalIndexes(SqliteTransaction tx)
    {
        if (!HasTable("body_fts", tx))
        {
            Exec("create virtual table body_fts using fts5(subject,body,content='',contentless_delete=1,tokenize='unicode61 remove_diacritics 2')", tx);
            Exec("insert into body_fts(rowid,subject,body) select id,subject,case when body_state=1 then coalesce(full_body,preview,'') else coalesce(preview,'') end from emails where body_state<>3", tx);
        }
        if (!HasTable("attachment_fts", tx))
        {
            Exec("create virtual table attachment_fts using fts5(name,body,content='',contentless_delete=1,tokenize='unicode61 remove_diacritics 2')", tx);
            Exec("insert into attachment_fts(rowid,name,body) select a.rowid,a.name,case when a.state='indexed' and a.reader_revision=$rev and c.reader_revision=$rev then coalesce(c.text,'') else '' end from attachments a left join attachment_contents c on c.hash=a.content_hash where a.state<>'facts-only'", tx,
                ("$rev", ArchiveAttachment.CurrentReaderRevision));
        }
    }

    private void EnsureReaderColumns(SqliteTransaction tx)
    {
        foreach (var table in new[] { "attachments", "attachment_contents" })
            if (HasTable(table, tx) && Scalar<long>("select count(*) from pragma_table_info('" + table + "') where name='reader_revision'", tx) == 0)
                Exec("alter table " + table + " add column reader_revision integer not null default 0", tx);
        if (HasTable("attachments", tx))
        {
            foreach (var column in new[] { ("size", "integer"), ("type", "text not null default ''") })
                if (Scalar<long>("select count(*) from pragma_table_info('attachments') where name=$name", tx, ("$name", column.Item1)) == 0)
                    Exec("alter table attachments add column " + column.Item1 + " " + column.Item2, tx);
        }
    }

}
