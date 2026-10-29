using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Prompts;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// «Contatti»: the people of the chosen archive folders compared with one address book. Present contacts get the
/// details of their newest email, missing ones are proposed; nothing is written without the user's click.
/// </summary>
public partial class TaskPaneCoordinator
{
    private const string ContactsScopeKey = "contacts_scope";
    private const int ContactRowsPerStep = 500;

    /// <summary>State of the last scan: what the pane may send back is checked against it.</summary>
    private sealed class ContactsSession
    {
        public string DestStoreId = string.Empty;
        public string DestFolderId = string.Empty;
        public readonly Dictionary<string, ContactCandidate> Candidates = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Contacts of the destination that may be written for each address, with the time last read.</summary>
        public readonly Dictionary<string, HashSet<string>> Writable = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, DateTime> Modified = new(StringComparer.Ordinal);
        public readonly HashSet<string> Role = new(StringComparer.OrdinalIgnoreCase);
    }

    private ContactsSession? _contacts;

    private sealed class ContactsScope
    {
        public List<string> Stores { get; set; } = new();
        public List<long> Excluded { get; set; } = new();
        public string DestStore { get; set; } = string.Empty;
        public string DestFolder { get; set; } = string.Empty;
        public bool ExcludeOwnDomains { get; set; }
    }

    /// <summary>Ribbon: the Contatti page, or where to activate the archive.</summary>
    public void ShowContacts() => _ = GuardArchiveUiAsync(async () =>
    {
        if (ArchiveIndexer.Current == null || await ArchiveIndexer.Current.OpenForSearchAsync() == null)
        {
            Toast("L'archivio delle email non è attivo: si attiva da Stato → Archivio delle email.", "info", "open_archive");
            return;
        }
        // From the ribbon: a second click closes the page, as for Controparti.
        _control.PostMessage(new { type = "toggle_contacts", toggle = true });
    });

    private async Task HandleContactsCommandAsync(string type, JsonElement message)
    {
        try
        {
            var db = ArchiveIndexer.Current is { } indexer ? await indexer.OpenForSearchAsync() : null;
            if (_isDisposed) return;
            if (db == null)
            {
                _control.PostMessage(new { type = "contacts_scope", available = false });
                return;
            }
            switch (type)
            {
                case "contacts_open": await SendContactsScopeAsync(db, message.TryGetProperty("refresh", out var r) && r.ValueKind == JsonValueKind.True); break;
                case "contacts_folders": await SendDestinationFoldersAsync(GetString(message, "storeId") ?? string.Empty); break;
                case "contacts_create_folder":
                    var store = GetString(message, "storeId") ?? string.Empty;
                    if (_dataService.CreateContactsFolder(store).Length > 0) Toast("Cartella «Contatti» creata.", "success");
                    await SendDestinationFoldersAsync(store);
                    break;
                case "contacts_scan": await ScanContactsAsync(db, message); break;
                case "contacts_analyze": await AnalyzeContactsAsync(message); break;
                case "contacts_cancel": _operations.CancelContacts(); break;
                case "contacts_apply": ApplyContacts(message); break;
                case "contacts_reject":
                    if (_contacts == null) break;
                    var refused = Addresses(message).Where(_contacts.Candidates.ContainsKey).ToList();
                    if (refused.Count == 0) break;
                    await db.RejectContactsAsync(_contacts.DestStoreId, refused);
                    foreach (var a in refused) _contacts.Candidates.Remove(a);
                    _control.PostMessage(new { type = "contacts_removed", addresses = refused });
                    Toast(refused.Count == 1 ? "Indirizzo escluso: non verrà più proposto per questa rubrica." : $"{refused.Count} indirizzi esclusi per questa rubrica.", "info");
                    await SendRejectedAsync(db, _contacts.DestStoreId);
                    break;
                case "contacts_rejected": await SendRejectedAsync(db, GetString(message, "storeId") ?? _contacts?.DestStoreId ?? string.Empty); break;
                case "contacts_unreject":
                    var dest = GetString(message, "storeId") ?? string.Empty;
                    await db.UnrejectContactAsync(dest, GetString(message, "address") ?? string.Empty);
                    Toast("Indirizzo di nuovo proponibile: compare alla prossima ricerca.", "info");
                    await SendRejectedAsync(db, dest);
                    break;
                case "contacts_open_contact":
                    if (!_dataService.DisplayContact(GetString(message, "entryId") ?? string.Empty, GetString(message, "storeId") ?? string.Empty))
                        Toast("Il contatto non è più nella rubrica.", "error");
                    break;
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Contacts command {Command} failed", type);
            Toast("Operazione sui contatti non riuscita: " + ex.Message, "error");
            if (type == "contacts_scan") _control.PostMessage(new { type = "contacts_list", error = ex.Message });
        }
    }

    private static List<string> Addresses(JsonElement message) =>
        message.TryGetProperty("addresses", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => CounterpartyResolver.Normalize(e.GetString())).Where(a => a.Length > 0).Distinct().ToList()
            : new List<string>();

    /// <summary>The archived mailboxes and folders; <paramref name="refresh"/> is the page asking again while open.</summary>
    private async Task SendContactsScopeAsync(ArchiveClient db, bool refresh)
    {
        var indexer = ArchiveIndexer.Current!;
        var status = await indexer.GetStatusAsync();
        var chosen = new HashSet<string>(status.StoreIds, StringComparer.OrdinalIgnoreCase);
        var folders = await db.ScopeFoldersAsync();
        var coverage = (await db.GetStoreCoverageAsync()).ToDictionary(c => c.StoreId, StringComparer.OrdinalIgnoreCase);
        ContactsScope? saved = null;
        try { if (await db.GetMetaAsync(ContactsScopeKey) is { } json) saved = JsonSerializer.Deserialize<ContactsScope>(json); }
        catch (JsonException) { }
        if (_isDisposed) return;

        var stores = _dataService.EnumerateStores().Where(s => s.StoreId.Length > 0).Select(s =>
        {
            var own = folders.Where(f => string.Equals(f.StoreId, s.StoreId, StringComparison.OrdinalIgnoreCase)).ToList();
            bool archived = (chosen.Count == 0 || chosen.Contains(s.StoreId)) && own.Count > 0;
            coverage.TryGetValue(s.StoreId, out var c);
            string note = !archived ? "Non archiviata: si aggiunge in Configurazione → Archivio email → Le mie cassette."
                : c != null && c.FoldersDone < c.Folders ? $"Indicizzazione in corso ({c.FoldersDone} di {c.Folders} cartelle): risultati parziali."
                : string.Empty;
            return new
            {
                id = s.StoreId, name = s.DisplayName, open = s.IsOpen, archived, note,
                issue = c?.Error ?? string.Empty,
                folders = own.Select(f => new { id = f.Id, path = f.Path, emails = f.Emails }).ToList()
            };
        }).ToList();
        if (!refresh) OnlyWorkspaceLog.Logger.Information("Contacts scope: {Stores} mailboxes, {Archived} archived, {Folders} folders, {Chosen} chosen",
            stores.Count, stores.Count(s => s.archived), folders.Count, chosen.Count);
        _control.PostMessage(new
        {
            type = "contacts_scope", available = true, stores,
            saved = saved == null ? null : new { stores = saved.Stores, excluded = saved.Excluded, destStore = saved.DestStore, destFolder = saved.DestFolder, excludeOwnDomains = saved.ExcludeOwnDomains }
        });
    }

    private async Task SendDestinationFoldersAsync(string storeId)
    {
        object? folders;
        string error = string.Empty;
        try
        {
            folders = (await _dataService.ContactFoldersAsync(storeId)).Select(f => new
            {
                entryId = f.EntryId, path = f.Path, isDefault = f.IsDefault,
                // IMAP: kept in the local data file, not synchronized with the server.
                localOnly = f.Path.IndexOf("Solo questo computer", StringComparison.OrdinalIgnoreCase) >= 0 || f.Path.IndexOf("This computer only", StringComparison.OrdinalIgnoreCase) >= 0
            }).ToList();
        }
        catch (Exception ex)
        {
            folders = null;
            error = ex.Message;
        }
        _control.PostMessage(new { type = "contacts_dest_folders", storeId, folders, error });
    }

    private async Task ScanContactsAsync(ArchiveClient db, JsonElement message)
    {
        var destStore = GetString(message, "destStore") ?? string.Empty;
        var destFolder = GetString(message, "destFolder") ?? string.Empty;
        var folderIds = message.TryGetProperty("folders", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Where(e => e.TryGetInt64(out _)).Select(e => e.GetInt64()).Distinct().ToList() : new List<long>();
        bool excludeOwn = message.TryGetProperty("excludeOwnDomains", out var own) && own.ValueKind == JsonValueKind.True;
        if (destStore.Length == 0 || destFolder.Length == 0 || folderIds.Count == 0)
        {
            _control.PostMessage(new { type = "contacts_list", error = "Scegli almeno una cartella in cui cercare e la rubrica di destinazione." });
            return;
        }
        // The scope known to the archive: ids the pane did not get from it are dropped.
        var known = (await db.ScopeFoldersAsync()).ToDictionary(x => x.Id);
        folderIds = folderIds.Where(known.ContainsKey).ToList();
        var scope = new ContactsScope
        {
            Stores = folderIds.Select(id => known[id].StoreId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DestStore = destStore, DestFolder = destFolder, ExcludeOwnDomains = excludeOwn
        };
        scope.Excluded = known.Values.Where(x => scope.Stores.Contains(x.StoreId, StringComparer.OrdinalIgnoreCase) && !folderIds.Contains(x.Id)).Select(x => x.Id).ToList();
        await db.SetMetaAsync(ContactsScopeKey, JsonSerializer.Serialize(scope));

        _control.PostMessage(new { type = "contacts_phase", text = "Lettura dell'archivio…" });
        var candidates = await db.ContactCandidatesAsync(folderIds, destStore);
        var ownDomains = excludeOwn ? _dataService.GetOwnDomainList() : null;
        int filtered = candidates.Count;
        candidates = candidates.Where(c => ContactCandidateFilter.Accepts(c.Address, c.TwoWay, ownDomains)).ToList();
        filtered -= candidates.Count;

        _control.PostMessage(new { type = "contacts_phase", text = "Lettura della rubrica…" });
        var dest = await ReadContactIndexAsync(destStore, destFolder);
        if (dest == null)
        {
            _control.PostMessage(new { type = "contacts_list", error = "La cartella dei contatti scelta non è più disponibile." });
            return;
        }
        // Where else a missing contact already is: the default address book of the other open mailboxes.
        var elsewhere = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var storeNames = _dataService.EnumerateStores().ToDictionary(s => s.StoreId, s => s.DisplayName, StringComparer.OrdinalIgnoreCase);
        foreach (var other in _dataService.OtherDefaultContactFolders(destStore))
        {
            var entries = await ReadContactIndexAsync(other.StoreId, other.EntryId);
            foreach (var e in entries ?? new List<ContactIndexEntry>())
                foreach (var a in e.Emails)
                    if (!elsewhere.ContainsKey(a)) elsewhere[a] = storeNames.TryGetValue(other.StoreId, out var n) ? n : other.Path;
        }
        if (_isDisposed) return;

        var session = new ContactsSession { DestStoreId = destStore, DestFolderId = destFolder };
        var byEmail = new Dictionary<string, List<ContactIndexEntry>>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, List<ContactIndexEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in dest)
        {
            session.Modified[e.EntryId] = e.LastModifiedUtc;
            foreach (var a in e.Emails) (byEmail.TryGetValue(a, out var l) ? l : byEmail[a] = new List<ContactIndexEntry>()).Add(e);
            if (e.Name.Trim().Length > 0) (byName.TryGetValue(e.Name.Trim(), out var n) ? n : byName[e.Name.Trim()] = new List<ContactIndexEntry>()).Add(e);
        }

        var items = new List<(string Group, string GroupName, object Item, string Status)>();
        foreach (var c in candidates)
        {
            session.Candidates[c.Address] = c;
            bool role = ContactCandidateFilter.IsRole(c.Address);
            if (role) session.Role.Add(c.Address);
            var name = role ? string.Empty : ContactNameParser.Clean(c.Name, c.Address);
            byEmail.TryGetValue(c.Address, out var matches);
            matches ??= new List<ContactIndexEntry>();
            string status;
            object? sameName = null;
            if (matches.Count > 1) status = "conflict";
            else if (matches.Count == 1) status = c.LastFromUtc.HasValue && c.LastFromUtc.Value > matches[0].LastModifiedUtc ? "check" : "uptodate";
            else
            {
                status = "missing";
                if (name.Length > 0 && byName.TryGetValue(name, out var same) && same.Count == 1 && same[0].Emails.Count < 3)
                {
                    sameName = new { entryId = same[0].EntryId, name = same[0].Name };
                    matches = same;
                }
            }
            session.Writable[c.Address] = new HashSet<string>(matches.Select(m => m.EntryId), StringComparer.Ordinal);
            items.Add((c.GroupKey, c.GroupName, new
            {
                address = c.Address, name, role, status, emails = c.Emails, sources = c.Sources.Count,
                last = c.LastFromUtc.HasValue ? Day(c.LastFromUtc) : string.Empty,
                presentIn = status == "missing" && elsewhere.TryGetValue(c.Address, out var where) ? where : string.Empty,
                sameName,
                contacts = status == "missing" ? null : matches.Select(m => new { entryId = m.EntryId, name = m.Name }).ToList()
            }, status));
        }
        _contacts = session;

        var groups = items.GroupBy(i => i.Group, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { key = g.Key, name = g.First().GroupName, items = g.Select(i => i.Item).ToList() })
            .OrderBy(g => g.name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _control.PostMessage(new
        {
            type = "contacts_list", destStore, destFolder, groups,
            counts = new
            {
                missing = items.Count(i => i.Status == "missing"), check = items.Count(i => i.Status == "check"),
                conflict = items.Count(i => i.Status == "conflict"), uptodate = items.Count(i => i.Status == "uptodate"), filtered
            }
        });
    }

    /// <summary>The whole table, a batch per message-loop turn: Outlook stays responsive on large address books.</summary>
    private async Task<List<ContactIndexEntry>?> ReadContactIndexAsync(string storeId, string folderId)
    {
        using var reader = _dataService.OpenContactTable(storeId, folderId);
        if (reader == null) return null;
        var result = new List<ContactIndexEntry>();
        while (!reader.EndOfTable)
        {
            var batch = reader.ReadBatch(ContactRowsPerStep);
            if (batch.Count == 0 && reader.EndOfTable) break;
            result.AddRange(batch);
            await Task.Yield();
        }
        return result;
    }

    private async Task AnalyzeContactsAsync(JsonElement message)
    {
        var session = _contacts;
        if (session == null) return;
        var addresses = Addresses(message).Where(session.Candidates.ContainsKey).ToList();
        if (addresses.Count == 0) return;
        var profile = GetActiveProfile();
        if (profile == null)
        {
            PostNotConfigured();
            return;
        }

        var cts = _operations.BeginContacts();
        var ct = cts.Token;
        ILlmClient? client = null;
        ArchiveIndexer.InteractiveBegin();
        int done = 0;
        try
        {
            client = CreateLlmClient(profile);
            PostJob(new { type = "contacts_progress", done, total = addresses.Count, model = profile.DisplayName + " · " + profile.Model, external = profile.IsExternal });
            foreach (var address in addresses)
            {
                ct.ThrowIfCancellationRequested();
                var proposal = await ProposeContactAsync(session, session.Candidates[address], profile, client, ct);
                if (_isDisposed || !ReferenceEquals(session, _contacts)) return;
                _control.PostMessage(proposal);
                PostJob(new { type = "contacts_progress", done = ++done, total = addresses.Count });
            }
            PostJob(new { type = "contacts_analyze_end", done });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            PostJob(new { type = "contacts_analyze_end", done, cancelled = true });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Contact analysis failed");
            PostJob(new { type = "contacts_analyze_end", done, error = LlmErrorText.Describe(ex, profile) });
        }
        finally
        {
            DisposeClient(client);
            ArchiveIndexer.InteractiveEnd();
            _operations.EndContacts(cts);
        }
    }

    /// <summary>Fields of one address from its newest readable email; an unusable email gives way to the next one.</summary>
    private async Task<object> ProposeContactAsync(ContactsSession session, ContactCandidate candidate, LlmProfileConfig profile, ILlmClient client, CancellationToken ct)
    {
        var problems = new List<string>();
        Dictionary<string, string>? fields = null;
        ContactSourceEmail? used = null;
        foreach (var source in candidate.Sources)
        {
            var mail = _dataService.ReadContactMail(source.EntryId, source.StoreId);
            if (mail.Problem.Length > 0) { problems.Add(mail.Problem); continue; }
            var text = "Da: " + candidate.Name + " <" + candidate.Address + ">\nOggetto: " + mail.Subject + "\nData: " + mail.ReceivedUtc.ToLocalTime().ToString("dd/MM/yyyy") +
                       "\n\n" + _contextBuilder.CleanEmailBody(mail.Body, senderTextOnly: true);
            // Forwarded or quoted only: nothing written by the sender.
            if (_contextBuilder.CleanEmailBody(mail.Body, senderTextOnly: true).Trim().Length == 0) { problems.Add("solo testo citato o inoltrato"); continue; }
            try { text = _contextBuilder.EnsureFits(text, ProviderCatalog.PromptBudget(profile)); }
            catch (ContextTooLongException) { problems.Add("email troppo lunga per il contesto del modello locale"); continue; }
            var json = await client.CompleteStructuredAsync(PromptCatalog.CreateContactPrompt(text, candidate.Name, candidate.Address), PromptCatalog.ContactJsonSchema, ct);
            ModelOutput.EnsureComplete(client.LastCompletion);
            fields = ContactExtraction.Parse(json, text);
            used = source;
            break;
        }

        fields ??= new Dictionary<string, string>();
        bool role = session.Role.Contains(candidate.Address);
        if (role) fields.Remove(ContactFields.FullName);
        else if (!fields.ContainsKey(ContactFields.FullName) && ContactNameParser.Clean(candidate.Name, candidate.Address) is { Length: > 0 } name) fields[ContactFields.FullName] = name;
        if (role && !fields.ContainsKey(ContactFields.Company)) fields[ContactFields.Company] = candidate.GroupName;

        var existing = new List<object>();
        if (session.Writable.TryGetValue(candidate.Address, out var ids))
        {
            foreach (var id in ids)
            {
                var current = _dataService.ReadContactFields(id, session.DestStoreId, out var modified);
                if (current == null) continue;
                session.Modified[id] = modified;
                existing.Add(new
                {
                    entryId = id, name = current[ContactFields.FullName],
                    changes = ContactDiff.Compute(current, fields).Select(c => new { field = c.Field, label = c.Label, old = c.Old, @new = c.New }).ToList()
                });
            }
        }
        return new
        {
            type = "contacts_proposal", address = candidate.Address,
            fields = ContactFields.All.Where(fields.ContainsKey).Select(k => new { field = k, label = ContactFields.Labels[k], value = fields[k] }).ToList(),
            existing,
            source = used == null ? null : new { subject = used.Subject, date = Day(used.ReceivedUtc), entryId = used.EntryId, storeId = used.StoreId },
            problem = used == null ? (candidate.Sources.Count == 0 ? "nessuna email scritta da questo indirizzo nelle cartelle scelte" : "nessuna email leggibile: " + string.Join(", ", problems.Distinct())) : string.Empty
        };
    }

    /// <summary>The user's choices from the pane, checked against the last scan before anything is written.</summary>
    private void ApplyContacts(JsonElement message)
    {
        var session = _contacts;
        if (session == null || !message.TryGetProperty("items", out var array) || array.ValueKind != JsonValueKind.Array) return;
        var results = new List<(string Address, bool Ok, string Message)>();
        foreach (var e in array.EnumerateArray())
        {
            var address = CounterpartyResolver.Normalize(GetString(e, "address"));
            var action = GetString(e, "action") ?? string.Empty;
            var entryId = GetString(e, "entryId") ?? string.Empty;
            if (!session.Candidates.ContainsKey(address)) continue;
            var fields = e.TryGetProperty("fields", out var f) ? ContactFields.FromJson(f) : new Dictionary<string, string>();
            if (session.Role.Contains(address)) fields.Remove(ContactFields.FullName);
            string text;
            bool ok = false;
            try
            {
                if (action == "create")
                {
                    var id = _dataService.CreateContact(session.DestStoreId, session.DestFolderId, address, fields);
                    ok = id.Length > 0;
                    text = ok ? "Contatto creato." : "La rubrica non accetta nuovi contatti (sola lettura o non raggiungibile).";
                    if (ok)
                    {
                        session.Writable[address] = new HashSet<string> { id };
                        _dataService.ReadContactFields(id, session.DestStoreId, out var modified);
                        session.Modified[id] = modified;
                    }
                }
                else if ((action == "update" || action == "add_email") && session.Writable.TryGetValue(address, out var ids) && ids.Contains(entryId) &&
                         session.Modified.TryGetValue(entryId, out var expected))
                {
                    var outcome = _dataService.UpdateContact(entryId, session.DestStoreId, expected, fields, action == "add_email" ? address : null);
                    ok = outcome == ContactWrite.Saved;
                    text = outcome switch
                    {
                        ContactWrite.Saved => action == "add_email" ? "Indirizzo aggiunto al contatto." : "Contatto aggiornato.",
                        ContactWrite.ChangedMeanwhile => "Il contatto è stato modificato nel frattempo: rianalizzalo per vedere le differenze aggiornate.",
                        ContactWrite.Full => "Il contatto ha già tre indirizzi: crea un contatto nuovo.",
                        ContactWrite.Denied => "La rubrica è in sola lettura: nessuna modifica.",
                        _ => "Il contatto non è più nella rubrica."
                    };
                    if (ok)
                    {
                        _dataService.ReadContactFields(entryId, session.DestStoreId, out var modified);
                        session.Modified[entryId] = modified;
                    }
                }
                else text = "Operazione non valida per questo indirizzo: ripeti la ricerca.";
            }
            catch (Exception ex)
            {
                OnlyWorkspaceLog.Logger.Error(ex, "Contact write failed");
                text = "Scrittura non riuscita: " + ex.Message;
            }
            results.Add((address, ok, text));
        }
        if (results.Count == 0) return;
        int saved = results.Count(r => r.Ok);
        _control.PostMessage(new { type = "contacts_applied", results = results.Select(r => new { address = r.Address, ok = r.Ok, message = r.Message }).ToList() });
        Toast(saved == results.Count ? $"{saved} contatti salvati." : $"{saved} contatti salvati, {results.Count - saved} non riusciti.", saved == results.Count ? "success" : "error");
    }

    private async Task SendRejectedAsync(ArchiveClient db, string storeId)
    {
        if (storeId.Length == 0) return;
        var items = await db.RejectedContactsAsync(storeId);
        _control.PostMessage(new { type = "contacts_rejected", storeId, items = items.Select(r => new { address = r.Address, date = Day(r.RejectedUtc) }).ToList() });
    }
}
