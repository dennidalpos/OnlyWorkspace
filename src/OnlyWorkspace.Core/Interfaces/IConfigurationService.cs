using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Interfaces;

public interface IConfigurationService
{
    WorkspaceConfig LoadConfiguration();
    /// <summary>True when config or secrets changed on disk since the last LoadConfiguration.</summary>
    bool HasConfigurationChanged();
    void SaveConfiguration(WorkspaceConfig config);
    string? ResolveSecret(string secretRef);
    void StoreSecret(string secretRef, string secretValue);
    /// <summary>Per-user flag; absent means false. Read errors and invalid values throw, never imply permission.</summary>
    bool GetUserFlag(string name);
    void SetUserFlag(string name, bool value);
}
