using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Infrastructure.Archive;

public sealed partial class ArchiveDb
{
    public string? GetAttachmentText(string hash)
    {
        lock (_gate) return Scalar<string?>("select c.text from attachment_contents c where c.hash=$hash and c.reader_revision=$rev and exists(select 1 from attachments a where a.content_hash=c.hash and a.state='indexed' and a.text_limited=0 and a.reader_revision=$rev)", null, ("$hash", hash), ("$rev", ArchiveAttachment.CurrentReaderRevision));
    }
    private void PruneAttachmentContents(SqliteTransaction tx) =>
        Exec("delete from attachment_contents where not exists(select 1 from attachments a where a.content_hash=attachment_contents.hash)", tx);

    /// <summary>Full original, never a preview. A pending/invalidated original must be reread from Outlook.</summary>
    public ArchiveBody? GetOriginal(string entryId, string storeId)
        => ReadOriginal(entryId, storeId, null);

    private ArchiveBody? ReadOriginal(string entryId, string storeId, long? emailId)
    {
        lock (_gate)
        {
            using var command = Command("select e.id,e.full_body,e.modified_utc,e.source_version,e.recipients_limited,e.attachments_limited,coalesce(e.message_id,'') " +
                "from emails e join folders f on f.id=e.folder_id where e.entry_id=$entry and f.store_id=$store and ($id is null or e.id=$id) and e.body_state=1 and e.present=1 and e.full_body is not null limit 1",
                null, ("$entry", entryId), ("$store", storeId), ("$id", emailId));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            long id = reader.GetInt64(0);
            var body = new ArchiveBody
            {
                MessageId = reader.GetString(6),
                Text = reader.GetString(1),
                ModifiedUtc = reader.IsDBNull(2) ? null : new DateTime(reader.GetInt64(2), DateTimeKind.Utc),
                SourceVersion = reader.IsDBNull(3) ? null : reader.GetString(3),
                RecipientsLimited = reader.GetInt64(4) != 0,
                AttachmentsLimited = reader.GetInt64(5) != 0
            };
            reader.Close();
            using (var attachments = Command("select a.source_id,a.name,a.version,a.state,a.problem,a.text_limited,c.text,a.reader_revision,a.size,a.type from attachments a left join attachment_contents c on c.hash=a.content_hash where a.email_id=$id order by a.rowid", null, ("$id", id)))
            using (var rows = attachments.ExecuteReader())
                while (rows.Read())
                {
                    body.AttachmentNames.Add(rows.GetString(1));
                    body.Attachments.Add(new ArchiveAttachment
                    {
                        SourceId = rows.IsDBNull(0) ? "" : rows.GetString(0),
                        Name = rows.GetString(1),
                        Version = rows.IsDBNull(2) ? null : rows.GetString(2),
                        State = rows.GetString(3),
                        Problem = rows.IsDBNull(4) ? null : rows.GetString(4),
                        TextLimited = rows.GetInt64(5) != 0,
                        Text = rows.IsDBNull(6) ? "" : rows.GetString(6),
                        ReaderRevision = rows.GetInt32(7),
                        Size = rows.IsDBNull(8) ? null : rows.GetInt64(8),
                        Type = rows.GetString(9)
                    });
                }
            using (var recipients = Command("select a.addr,a.name,ea.role from email_addr ea join addresses a on a.id=ea.addr_id where ea.email_id=$id and ea.role<>0", null, ("$id", id)))
            using (var rows = recipients.ExecuteReader())
                while (rows.Read()) body.Recipients.Add(new ArchiveRecipient { Address = rows.GetString(0), Name = rows.IsDBNull(1) ? "" : rows.GetString(1), IsCc = rows.GetInt64(2) == 2 });
            return body;
        }
    }

    internal int AttachmentContentCount
    {
        get { lock (_gate) return (int)Scalar<long>("select count(*) from attachment_contents"); }
    }
}
