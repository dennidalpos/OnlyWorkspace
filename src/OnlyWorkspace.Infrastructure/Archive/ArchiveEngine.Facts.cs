using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Infrastructure.Logging;

namespace OnlyWorkspace.Infrastructure.Archive;

public sealed partial class ArchiveEngine
{
    private int _factsRunning;

    // Called under the engine gate. There is at most one worker, and extraction never holds the gate.
    private void ScheduleFacts()
    {
        if (_disposed || !_enabled() || Interlocked.CompareExchange(ref _factsRunning, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    ArchiveDb[] archives;
                    lock (_gate) { if (_disposed || !_enabled()) return; archives = _archives.Values.ToArray(); }
                    bool worked = false;
                    foreach (var db in archives)
                    {
                        ArchiveDb.SearchWork? search;
                        lock (_gate)
                        {
                            if (_disposed) return;
                            if (!_archives.Values.Contains(db) || _maintenance.Any(profile => _archives.TryGetValue(profile, out var busy) && ReferenceEquals(busy, db))) continue;
                            db.ConfigureStorage(_indexAttachmentText(), _attachmentOcr(), _ocrLanguage());
                            search = db.NextSearchWork();
                        }
                        if (search != null)
                        {
                            worked = true;
                            var bodyCodes = OnlyWorkspace.Core.Archive.ArchiveSearchText.CodeTerms(search.Body);
                            var fileCodes = OnlyWorkspace.Core.Archive.ArchiveSearchText.CodeTerms(search.Attachments);
                            lock (_gate)
                                if (!_disposed && _enabled() && _archives.Values.Contains(db) && !_maintenance.Any(profile => _archives.TryGetValue(profile, out var busy) && ReferenceEquals(busy, db)))
                                {
                                    db.ConfigureStorage(_indexAttachmentText(), _attachmentOcr(), _ocrLanguage());
                                    db.CommitSearch(search, bodyCodes, fileCodes);
                                }
                        }
                        ArchiveDb.FactWork? work;
                        lock (_gate)
                        {
                            if (_disposed) return;
                            if (!_archives.Values.Contains(db) || _maintenance.Any(profile => _archives.TryGetValue(profile, out var busy) && ReferenceEquals(busy, db))) continue;
                            db.ConfigureStorage(_indexAttachmentText(), _attachmentOcr(), _ocrLanguage());
                            work = db.NextFactWork();
                        }
                        if (work == null) continue;
                        worked = true;
                        System.Collections.Generic.List<OnlyWorkspace.Core.Archive.ArchiveFact> facts;
                        string? error = null;
                        try { facts = ArchiveDb.ExtractFacts(work, _stop.Token); }
                        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                        catch (Exception ex) when (ex is System.Text.RegularExpressions.RegexMatchTimeoutException || ex is ArgumentException)
                        {
                            facts = new(); error = ex.GetType().Name;
                            OnlyWorkspaceLog.Logger.Warning("Local archive fact extraction incomplete: {Kind}", error);
                        }
                        lock (_gate)
                        {
                            if (_disposed) return;
                            if (_enabled() && _archives.Values.Contains(db) && !_maintenance.Any(profile => _archives.TryGetValue(profile, out var busy) && ReferenceEquals(busy, db)))
                            {
                                db.ConfigureStorage(_indexAttachmentText(), _attachmentOcr(), _ocrLanguage());
                                db.CommitFacts(work, facts, error);
                            }
                        }
                        await Task.Delay(25, _stop.Token).ConfigureAwait(false);
                    }
                    if (!worked) return;
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { OnlyWorkspaceLog.Logger.Warning("Local archive facts worker paused: {Kind}", ex.GetType().Name); }
            finally { Interlocked.Exchange(ref _factsRunning, 0); }
        });
    }
}
