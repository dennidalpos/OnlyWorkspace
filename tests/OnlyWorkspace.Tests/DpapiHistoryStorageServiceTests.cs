using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Security;
using Xunit;

namespace OnlyWorkspace.Tests;

public class DpapiHistoryStorageServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DpapiHistoryStorageService _storageService;

    public DpapiHistoryStorageServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_History_" + Guid.NewGuid().ToString("N"));
        _storageService = new DpapiHistoryStorageService(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                TestArtifacts.DeleteDirectory(_tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors in temp
            }
        }
    }

    [Fact]
    public async Task DeleteAll_ReportsLockedFiles_AndCanBeRetriedWithoutLosingSurvivors()
    {
        var locked = new ConversationHistory { Id = "locked", Title = "Keep until retry" };
        var removable = new ConversationHistory { Id = "removable", Title = "Remove now" };
        await _storageService.SaveConversationAsync(locked);
        await _storageService.SaveConversationAsync(removable);
        var files = Directory.GetFiles(_tempDir, "*.bin");
        string lockedPath = Array.Find(files, file => Path.GetFileNameWithoutExtension(file) == locked.Id)!;
        using (var held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<AggregateException>(() => _storageService.DeleteAllConversationsAsync());
            Assert.True(File.Exists(lockedPath));
            Assert.Single(Directory.GetFiles(_tempDir, "*.bin"));
        }
        Assert.Equal(locked.Title, (await _storageService.LoadConversationAsync(locked.Id))!.Title);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _storageService.SaveConversationAsync(locked));
        await _storageService.DeleteAllConversationsAsync();
        Assert.Empty(await _storageService.ListConversationsAsync());
    }

    [Fact]
    public async Task SaveAndLoad_Roundtrip_WorksAndCiphertextIsEncryptedOnDisk()
    {
        var conv = new ConversationHistory
        {
            Id = "conv-101",
            Title = "Discussione Progetto",
            ProfileId = "ollama-lan",
            Messages =
            {
                new HistoryChatMessage { Role = "user", Content = "Quali sono le scadenze?" },
                new HistoryChatMessage
                {
                    Role = "assistant", Content = "Le scadenze sono il 15 ottobre.",
                    WebSources = { new WebSource { Title = "Fonte web", Url = "https://example.org/source" } },
                    MailSources = { new HistoryContextReference { EntryId = "ENTRY-2", Subject = "Precedente", ItemType = "email" } },
                    Documents = { new OnlyWorkspace.Core.Agents.DocumentOutcome { Name = "PRIVATE-DOCUMENT.pdf",
                        EntryId = "ENTRY-2", StoreId = "STORE-2", SourceId = "1", IncludedInRequest = false,
                        Status = OnlyWorkspace.Core.Agents.DocumentStatus.Incomplete, ExtractionProblem = "ocr-off" } }
                }
            },
            ContextReferences =
            {
                new HistoryContextReference { EntryId = "ENTRY-1", Subject = "Email Scadenze", ItemType = "email" }
            }
        };

        await _storageService.SaveConversationAsync(conv);

        // Verify ciphertext
        var filePath = Path.Combine(_tempDir, "conv-101.bin");
        Assert.True(File.Exists(filePath));
        var rawBytes = File.ReadAllBytes(filePath);
        var rawString = Encoding.UTF8.GetString(rawBytes);

        Assert.DoesNotContain("Quali sono le scadenze?", rawString);
        Assert.DoesNotContain("Discussione Progetto", rawString);
        Assert.DoesNotContain("PRIVATE-DOCUMENT.pdf", rawString);

        // Verify roundtrip
        var loaded = await _storageService.LoadConversationAsync("conv-101");
        Assert.NotNull(loaded);
        Assert.Equal("conv-101", loaded!.Id);
        Assert.Equal("Discussione Progetto", loaded.Title);
        Assert.Equal("ollama-lan", loaded.ProfileId);
        Assert.Equal(2, loaded.Messages.Count);
        Assert.Equal("Quali sono le scadenze?", loaded.Messages[0].Content);
        Assert.Equal("https://example.org/source", loaded.Messages[1].WebSources[0].Url);
        Assert.Equal("ENTRY-2", loaded.Messages[1].MailSources[0].EntryId);
        var document = Assert.Single(loaded.Messages[1].Documents);
        Assert.Equal("PRIVATE-DOCUMENT.pdf", document.Name);
        Assert.Equal("STORE-2", document.StoreId); Assert.Equal("1", document.SourceId);
        Assert.Contains("OCR è disattivato", document.Reason);
        Assert.False(document.IncludedInRequest);
        Assert.Single(loaded.ContextReferences);
        Assert.Equal("ENTRY-1", loaded.ContextReferences[0].EntryId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Permitted triage")]
    public async Task ModelContextRoundTripPreservesLocalDisplayAndExplicitOmission(string? modelContent)
    {
        var conversation = new ConversationHistory
        {
            Messages = { new HistoryChatMessage { Role = "assistant", Content = "LOCAL-AGENDA", ModelContent = modelContent } }
        };
        await _storageService.SaveConversationAsync(conversation);
        var loaded = await _storageService.LoadConversationAsync(conversation.Id);
        var message = Assert.Single(loaded!.Messages);
        Assert.Equal("LOCAL-AGENDA", message.Content);
        Assert.Equal(modelContent, message.ModelContent);
    }

    [Fact]
    public async Task ListConversations_ReturnsSummariesInOrder()
    {
        var conv1 = new ConversationHistory { Id = "c1", Title = "Chat 1", ProfileId = "p1", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        var conv2 = new ConversationHistory { Id = "c2", Title = "Chat 2", ProfileId = "p1", UpdatedAt = DateTimeOffset.UtcNow };

        await _storageService.SaveConversationAsync(conv1);
        await _storageService.SaveConversationAsync(conv2);

        var list = await _storageService.ListConversationsAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal("c2", list[0].Id);
        Assert.Equal("c1", list[1].Id);
    }

    [Fact]
    public async Task ListConversations_FollowsAChangedFileAndTheDeletionLedger()
    {
        // Summaries are kept between listings (Storico opened on the pane): a saved file and a deleted source still show.
        var captured = DateTime.UtcNow.AddMinutes(-1);
        var conv = new ConversationHistory
        {
            Id = "kept",
            Title = "Prima",
            ProfileId = "p1",
            ContextReferences = { new HistoryContextReference { EntryId = "E1", StoreId = "S1", OutlookProfile = "Outlook", ItemType = "email", CapturedUtc = captured } }
        };
        await _storageService.SaveConversationAsync(conv);
        Assert.Equal("Prima", Assert.Single(await _storageService.ListConversationsAsync()).Title);

        conv.Title = "Dopo";
        conv.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await _storageService.SaveConversationAsync(conv);
        Assert.Equal("Dopo", Assert.Single(await _storageService.ListConversationsAsync()).Title);

        await _storageService.InvalidateSourcesAsync(new[] { new OnlyWorkspace.Core.Archive.ArchiveDeletedSource { OutlookProfile = "Outlook", StoreId = "S1", EntryId = "E1", DeletedUtc = DateTime.UtcNow } });
        Assert.Empty(await _storageService.ListConversationsAsync());
    }

    [Fact]
    public async Task ValidityCheck_DoesNotWaitForABusyHistory()
    {
        // The chat's check every 2 seconds skips a round while a save or a sweep holds the history (A149).
        var conv = new ConversationHistory
        {
            Id = "checked",
            ProfileId = "p1",
            ContextReferences = { new HistoryContextReference { EntryId = "E1", StoreId = "S1", OutlookProfile = "Outlook", ItemType = "email", CapturedUtc = DateTime.UtcNow.AddMinutes(-1) } }
        };
        await _storageService.InvalidateSourcesAsync(new[] { new OnlyWorkspace.Core.Archive.ArchiveDeletedSource { OutlookProfile = "Outlook", StoreId = "S1", EntryId = "E1", DeletedUtc = DateTime.UtcNow } });

        using var held = new System.Threading.ManualResetEventSlim();
        using var release = new System.Threading.ManualResetEventSlim();
        var holder = new System.Threading.Thread(() =>
        {
            using var gate = new System.Threading.Mutex(false, _storageService.HistoryMutexName);
            gate.WaitOne();
            held.Set();
            release.Wait();
            gate.ReleaseMutex();
        });
        holder.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(await _storageService.IsValidAsync(conv));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"The check waited {watch.ElapsedMilliseconds} ms for the history.");
        }
        finally
        {
            release.Set();
            holder.Join();
        }
        Assert.False(await _storageService.IsValidAsync(conv));
    }

    [Fact]
    public async Task Saves_LandInTheOrderAsked()
    {
        var conv = new ConversationHistory { Id = "ordered", Title = "1", ProfileId = "p1" };
        var first = _storageService.SaveConversationAsync(conv);
        conv.Title = "2";
        var second = _storageService.SaveConversationAsync(conv);
        await Task.WhenAll(first, second);

        Assert.Equal("2", (await _storageService.LoadConversationAsync("ordered"))!.Title);
    }

    [Fact]
    public async Task DeleteConversation_RemovesFile()
    {
        var conv = new ConversationHistory { Id = "to-delete", Title = "Test Delete", ProfileId = "p1" };
        await _storageService.SaveConversationAsync(conv);

        var deleted = await _storageService.DeleteConversationAsync("to-delete");
        Assert.True(deleted);

        var loaded = await _storageService.LoadConversationAsync("to-delete");
        Assert.Null(loaded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deletion_RejectsQueuedIndependentWriter_AndSurvivesReopen(bool all)
    {
        var conversation = new ConversationHistory { Id = "revoked", Title = "Before" };
        await _storageService.SaveConversationAsync(conversation);
        var writer = new DpapiHistoryStorageService(_tempDir);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Hold the existing save queue, before its mutex acquisition: no scheduler ordering assumption.
        typeof(DpapiHistoryStorageService).GetField("_saves", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(writer, release.Task);
        conversation.Id = "REVOKED";
        var queued = writer.SaveConversationAsync(conversation);
        try
        {
            if (all) await _storageService.DeleteAllConversationsAsync();
            else Assert.True(await _storageService.DeleteConversationAsync("revoked"));
        }
        finally { release.TrySetResult(true); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        var reopened = new DpapiHistoryStorageService(_tempDir);
        Assert.Null(await reopened.LoadConversationAsync("revoked"));
        Assert.Empty(await reopened.ListConversationsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SaveConversationAsync(conversation));
        await reopened.SaveConversationAsync(new ConversationHistory { Id = "new-after-delete" });
        Assert.Single(await reopened.ListConversationsAsync());
    }

    [Fact]
    public async Task DeleteAll_LeavesUnsavedNewIdentitiesEligible()
    {
        await _storageService.SaveConversationAsync(new ConversationHistory { Id = "persisted" });
        var unsaved = new ConversationHistory { Id = "not-persisted-yet" };
        await _storageService.DeleteAllConversationsAsync();
        await _storageService.SaveConversationAsync(unsaved);
        Assert.Equal(unsaved.Id, Assert.Single(await _storageService.ListConversationsAsync()).Id);
    }

    [Fact]
    public async Task FailedDelete_PreservesReadableFile_ButRevokesStaleSaves()
    {
        var conversation = new ConversationHistory { Id = "locked-single", Title = "Original" };
        await _storageService.SaveConversationAsync(conversation);
        using (var held = new FileStream(Path.Combine(_tempDir, conversation.Id + ".bin"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(() => _storageService.DeleteConversationAsync(conversation.Id));
        Assert.Equal("Original", (await _storageService.LoadConversationAsync(conversation.Id))!.Title);
        conversation.Title = "Must not replace survivor";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _storageService.SaveConversationAsync(conversation));
        Assert.True(await _storageService.DeleteConversationAsync(conversation.Id));
        Assert.Null(await _storageService.LoadConversationAsync(conversation.Id));
    }

    [Fact]
    public async Task UnreadableRevocations_FailSavesAndDeletesWithoutLosingExistingFiles()
    {
        var conversation = new ConversationHistory { Id = "survivor" };
        await _storageService.SaveConversationAsync(conversation);
        File.WriteAllBytes(Path.Combine(_tempDir, "deleted-conversations.dat"), new byte[] { 1, 2, 3 });
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => _storageService.SaveConversationAsync(conversation));
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => _storageService.DeleteConversationAsync(conversation.Id));
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => _storageService.DeleteAllConversationsAsync());
        Assert.NotNull(await _storageService.LoadConversationAsync(conversation.Id));
    }

    [Fact]
    public async Task RevocationWriteFailure_DoesNotDeleteAnyConversation()
    {
        await _storageService.SaveConversationAsync(new ConversationHistory { Id = "keep-a" });
        await _storageService.SaveConversationAsync(new ConversationHistory { Id = "keep-b" });
        await _storageService.SaveConversationAsync(new ConversationHistory { Id = "seed-revocation" });
        Assert.True(await _storageService.DeleteConversationAsync("seed-revocation"));
        using (var held = new FileStream(Path.Combine(_tempDir, "deleted-conversations.dat"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => _storageService.DeleteAllConversationsAsync());
        Assert.Equal(2, (await _storageService.ListConversationsAsync()).Count);
    }

    [Fact]
    public async Task CleanupExpiredConversations_RemovesOldAndExcessConversations()
    {
        var conv1 = new ConversationHistory
        {
            Id = "old-conv",
            Title = "Old",
            ProfileId = "p1",
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-100)
        };
        var conv2 = new ConversationHistory
        {
            Id = "recent-1",
            Title = "Recent 1",
            ProfileId = "p1",
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2)
        };
        var conv3 = new ConversationHistory
        {
            Id = "recent-2",
            Title = "Recent 2",
            ProfileId = "p1",
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };

        await _storageService.SaveConversationAsync(conv1);
        await _storageService.SaveConversationAsync(conv2);
        await _storageService.SaveConversationAsync(conv3);

        var cleaned = await _storageService.CleanupExpiredConversationsAsync(retentionDays: 90, maxConversations: 1);

        var remaining = await _storageService.ListConversationsAsync();
        Assert.Single(remaining);
        Assert.Equal("recent-2", remaining[0].Id);
    }

    [Fact]
    public async Task ReopenedConversation_WithNewMessages_SurvivesRetention()
    {
        await _storageService.SaveConversationAsync(new ConversationHistory { Id = "reopened", ProfileId = "p1", UpdatedAt = DateTimeOffset.UtcNow.AddDays(-100) });
        var loaded = await _storageService.LoadConversationAsync("reopened");
        loaded!.Messages.Add(new HistoryChatMessage { Role = "user", Content = "ancora", Timestamp = DateTimeOffset.UtcNow });
        await _storageService.SaveConversationAsync(loaded);

        await _storageService.CleanupExpiredConversationsAsync(retentionDays: 90, maxConversations: 10);

        Assert.NotNull(await _storageService.LoadConversationAsync("reopened"));
    }

    [Fact]
    public async Task IdsWithPathParts_AreRejected()
    {
        // A .bin outside the history folder must stay out of reach of an id sent by the pane.
        var outside = Path.Combine(Path.GetDirectoryName(_tempDir)!, Path.GetFileName(_tempDir) + "_outside.bin");
        File.WriteAllBytes(outside, new byte[] { 1, 2, 3 });
        try
        {
            Directory.CreateDirectory(_tempDir);
            var id = "..\\" + Path.GetFileNameWithoutExtension(outside);

            Assert.Null(await _storageService.LoadConversationAsync(id));
            Assert.False(await _storageService.DeleteConversationAsync(id));
            Assert.True(File.Exists(outside));
            await Assert.ThrowsAsync<ArgumentException>(() => _storageService.SaveConversationAsync(new ConversationHistory { Id = id }));
        }
        finally
        {
            TestArtifacts.DeleteFile(outside);
        }
    }
}
