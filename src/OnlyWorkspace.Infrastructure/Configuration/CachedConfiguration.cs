using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Infrastructure.Configuration;

/// <summary>
/// The configuration loaded again only when config.json or secrets.bin change: the archive engine asked for it three
/// times per pipe request, each a full load with the vault's DPAPI decryption under its lock. A failed load is not kept.
/// </summary>
public sealed class CachedConfiguration
{
    private readonly IConfigurationService _settings;
    private readonly object _gate = new();
    private WorkspaceConfig? _loaded;

    public CachedConfiguration(IConfigurationService settings) => _settings = settings;

    public WorkspaceConfig Current
    {
        get
        {
            lock (_gate)
            {
                if (_loaded != null && !_settings.HasConfigurationChanged()) return _loaded;
                _loaded = null;
                return _loaded = _settings.LoadConfiguration();
            }
        }
    }
}
