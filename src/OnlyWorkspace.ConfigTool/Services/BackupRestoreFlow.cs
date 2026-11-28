using System;
using System.Threading.Tasks;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.ConfigTool.Services;

/// <summary>Outcome of a restore, for the form to show: no WinForms control is touched here.</summary>
public enum RestoreOutcome { Completed, Cancelled, Failed }

public sealed class RestoreResult
{
    public RestoreOutcome Outcome { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Restores a backup into the configuration folder, directly when the user may write it, otherwise through the elevated
/// writer with a staging folder that is always removed. Every dependency is injected so the flow runs without a form.
/// </summary>
public sealed class BackupRestoreFlow
{
    private readonly string _configDir;
    private readonly Func<string, bool> _canWrite;
    private readonly Action<string, string> _restoreDirect;
    private readonly Func<string> _createStaging;
    private readonly Action<string> _deleteStaging;
    private readonly Func<string, string, Task<ElevatedConfigWriter.ElevationResult>> _runElevated;

    public BackupRestoreFlow(string configDir, Func<string, bool> canWrite, Action<string, string> restoreDirect,
        Func<string> createStaging, Action<string> deleteStaging,
        Func<string, string, Task<ElevatedConfigWriter.ElevationResult>> runElevated)
    {
        _configDir = configDir;
        _canWrite = canWrite;
        _restoreDirect = restoreDirect;
        _createStaging = createStaging;
        _deleteStaging = deleteStaging;
        _runElevated = runElevated;
    }

    /// <summary>The production wiring: ConfigurationBackupService and ElevatedConfigWriter.</summary>
    public static BackupRestoreFlow Create(string configDir, Func<string, string?, Task<ElevatedConfigWriter.ElevationResult>> runElevated) =>
        new(configDir, ElevatedConfigWriter.CanWriteConfig, ConfigurationBackupService.RestoreBackup,
            ElevatedConfigWriter.CreateStagingDirectory, ElevatedConfigWriter.DeleteStagingDirectory,
            (arguments, staging) => runElevated(arguments, staging));

    public async Task<RestoreResult> RestoreAsync(string backupFile)
    {
        if (_canWrite(_configDir))
        {
            _restoreDirect(backupFile, _configDir);
            return new RestoreResult { Outcome = RestoreOutcome.Completed };
        }

        var staging = _createStaging();
        ElevatedConfigWriter.ElevationResult result;
        try
        {
            result = await _runElevated(
                "restore-authorized " + ElevatedConfigWriter.Quote(backupFile) + " " + ElevatedConfigWriter.Quote(_configDir) + " " + ElevatedConfigWriter.Quote(staging),
                staging);
        }
        finally { _deleteStaging(staging); }

        if (result.Success) return new RestoreResult { Outcome = RestoreOutcome.Completed };
        return new RestoreResult { Outcome = result.Cancelled ? RestoreOutcome.Cancelled : RestoreOutcome.Failed, Message = result.Message };
    }
}
