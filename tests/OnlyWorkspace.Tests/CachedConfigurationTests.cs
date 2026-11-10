using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.Tests;

/// <summary>The archive engine's configuration, loaded once per change of the files (A150).</summary>
public sealed class CachedConfigurationTests
{
    [Fact]
    public void Current_LoadsAgainOnlyWhenTheFilesChange()
    {
        var files = new CountingConfig();
        var cached = new CachedConfiguration(files);

        var first = cached.Current;
        Assert.Same(first, cached.Current);
        Assert.Same(first, cached.Current);
        Assert.Equal(1, files.Loads);

        files.Change();
        Assert.NotSame(first, cached.Current);
        Assert.Equal(2, files.Loads);
    }

    [Fact]
    public void Current_DoesNotKeepAFailedLoad()
    {
        var files = new CountingConfig();
        var cached = new CachedConfiguration(files);
        var first = cached.Current;

        files.Change();
        files.Fail = true;
        Assert.Throws<InvalidOperationException>(() => cached.Current);
        // The failed load marked the files as read: without dropping the old copy it would be served again.
        files.Fail = false;
        Assert.NotSame(first, cached.Current);
        Assert.Equal(3, files.Loads);
    }

    private sealed class CountingConfig : IConfigurationService
    {
        private int _version, _loaded = -1;
        public int Loads { get; private set; }
        public bool Fail { get; set; }

        public void Change() => _version++;

        public WorkspaceConfig LoadConfiguration()
        {
            Loads++;
            _loaded = _version;
            if (Fail) throw new InvalidOperationException("unreadable");
            return new WorkspaceConfig();
        }

        public bool HasConfigurationChanged() => _loaded != _version;
        public void SaveConfiguration(WorkspaceConfig config) => throw new NotSupportedException();
        public string? ResolveSecret(string secretRef) => null;
        public void StoreSecret(string secretRef, string secretValue) => throw new NotSupportedException();
        public bool GetUserFlag(string name) => false;
        public void SetUserFlag(string name, bool value) { }
    }
}
