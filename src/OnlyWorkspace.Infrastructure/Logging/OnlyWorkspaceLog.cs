using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace OnlyWorkspace.Infrastructure.Logging;

public static class OnlyWorkspaceLog
{
    /// <summary>Daily log files with size roll-over, also shown in Privacy e dati (AppStorageUsage).</summary>
    public const int FilesKept = 14;
    public const long FileLimitBytes = 10L * 1024 * 1024;

    private static bool _initialized;
    private static readonly object _syncLock = new();

    public static ILogger Logger => Serilog.Log.Logger;

    private static readonly Serilog.Core.LoggingLevelSwitch _level = new(LogEventLevel.Information);

    public static void Initialize(string? configuredLevel = null, bool configTool = false)
    {
        lock (_syncLock)
        {
            // The add-in starts logging before the configuration is read: a level given later still applies.
            if (configuredLevel != null || !_initialized) _level.MinimumLevel = ParseLevel(configuredLevel);
            if (_initialized) return;

            var logDirectory = OnlyWorkspace.Core.AppPaths.LogDirectory;

            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            // The tray and Outlook are separate processes: each owns a distinct rolling file.
            var logFilePath = Path.Combine(logDirectory, configTool ? "onlyworkspace-configtool-.log" : "onlyworkspace-.log");

            Serilog.Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(_level)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    path: logFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: FilesKept,
                    fileSizeLimitBytes: FileLimitBytes,
                    rollOnFileSizeLimit: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            _initialized = true;
            Serilog.Log.Information("OnlyWorkspace logging initialized. Level: {Level}", _level.MinimumLevel);
        }
    }

    public static void CloseAndFlush()
    {
        lock (_syncLock)
        {
            if (_initialized)
            {
                Serilog.Log.CloseAndFlush();
                _initialized = false;
            }
        }
    }

    private static LogEventLevel ParseLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level)) return LogEventLevel.Information;
        return level!.Trim().ToLowerInvariant() switch
        {
            "verbose" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "information" or "info" => LogEventLevel.Information,
            "warning" or "warn" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "fatal" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
    }
}
