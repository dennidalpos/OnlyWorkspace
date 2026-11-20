using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Archive;

namespace OnlyWorkspace.Infrastructure.Privacy;

/// <summary>One live connection to a recognizer process.</summary>
public interface INerChannel : IDisposable
{
    Stream Stream { get; }
    /// <summary>False once the recognizer process has ended.</summary>
    bool IsAlive { get; }
}

public interface INerChannelFactory
{
    INerChannel Open(PiiModelPackage model, CancellationToken token);
}

/// <summary>
/// The add-in side of the second masking layer (#247): checks the installed model by hash, talks to the separate
/// recognizer process and blocks the request on every failure (model missing or different, process dead, budget spent).
/// </summary>
public sealed class PiiRecognizerClient : INerRecognizer, IDisposable
{
    public const string HelperFileName = "OnlyWorkspace.PiiRecognizer.exe";
    private readonly string _modelDirectory;
    private readonly INerChannelFactory _channels;
    private readonly object _lock = new();
    private INerChannel? _channel;
    private PiiModelPackage? _verified;
    private string _stamp = string.Empty;

    public PiiRecognizerClient(string modelDirectory, INerChannelFactory channels)
    {
        _modelDirectory = modelDirectory ?? throw new ArgumentNullException(nameof(modelDirectory));
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
    }

    /// <summary>The installed recognizer next to the add-in, or null when the setup did not install it.</summary>
    public static PiiRecognizerClient? CreateInstalled(string modelDirectory)
    {
        var helper = FindHelper();
        return helper == null ? null : new PiiRecognizerClient(modelDirectory, new PiiRecognizerProcess(helper, modelDirectory));
    }

    public static string? FindHelper()
    {
        foreach (var dir in new[] { Path.GetDirectoryName(typeof(PiiRecognizerClient).Assembly.Location), AppDomain.CurrentDomain.BaseDirectory })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            foreach (var path in new[] { Path.Combine(dir, "recognizer", HelperFileName), Path.Combine(dir, HelperFileName) })
                if (File.Exists(path)) return path;
        }
        return null;
    }

    public string ModelRevision { get { lock (_lock) return Model().Revision; } }

    public IReadOnlyList<NerSpan> Recognize(string block, TimeSpan budget, CancellationToken token)
    {
        lock (_lock)
        {
            token.ThrowIfCancellationRequested();
            Model();
            if (_channel != null && !_channel.IsAlive) { Close(); throw new DataEgressBlockedException("Processo del riconoscimento locale terminato: nessun dato inviato."); }
            try
            {
                _channel ??= _channels.Open(_verified!, token);
                return NerProtocol.Call(_channel.Stream, block, budget, token);
            }
            catch (OperationCanceledException) { Close(); throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A timeout leaves a half-read frame behind: the connection is not reused.
                Close();
                throw new DataEgressBlockedException("Riconoscimento locale non completato: nessun dato inviato.");
            }
        }
    }

    // A new process always starts from a full hash check; while one is running the hash is read again only when the files changed on disk.
    private PiiModelPackage Model()
    {
        string manifest = Path.Combine(_modelDirectory, PiiModelPackage.ManifestFileName);
        string stamp = Stamp(manifest) + "|" + (_verified == null ? "" : Stamp(_verified.Path));
        if (_verified != null && _channel != null && stamp == _stamp) return _verified;
        Close();
        _verified = null;
        var verified = PiiModelPackage.Verify(_modelDirectory);
        _verified = verified;
        _stamp = Stamp(manifest) + "|" + Stamp(verified.Path);
        return verified;
    }

    private static string Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "absent";
    }

    private void Close() { _channel?.Dispose(); _channel = null; }

    public void Dispose() { lock (_lock) Close(); }
}

/// <summary>Starts the recognizer process and connects to its pipe, which only this Windows user can open.</summary>
public sealed class PiiRecognizerProcess : INerChannelFactory
{
    private readonly string _helper;
    private readonly string _modelDirectory;

    public PiiRecognizerProcess(string helperPath, string modelDirectory) { _helper = helperPath; _modelDirectory = modelDirectory; }

    public INerChannel Open(PiiModelPackage model, CancellationToken token)
    {
        string name = "OnlyWorkspace_Pii_" + ArchivePipe.UserSid + "_" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(_helper, $"--pipe {name} --model-dir \"{_modelDirectory.TrimEnd('\\')}\\\\\" --parent {Process.GetCurrentProcess().Id}")
        { UseShellExecute = false, CreateNoWindow = true };
        var process = Process.Start(start) ?? throw new InvalidOperationException("Recognizer not started.");
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(10_000);
            ArchivePipe.ValidateServer(pipe);
            return new Channel(process, pipe);
        }
        catch
        {
            pipe?.Dispose();
            try { process.Kill(); } catch (InvalidOperationException) { }
            process.Dispose();
            throw;
        }
    }

    private sealed class Channel : INerChannel
    {
        private readonly Process _process;
        private readonly NamedPipeClientStream _pipe;
        public Channel(Process process, NamedPipeClientStream pipe) { _process = process; _pipe = pipe; }
        public Stream Stream => _pipe;
        public bool IsAlive { get { try { return !_process.HasExited; } catch (InvalidOperationException) { return false; } } }
        public void Dispose()
        {
            _pipe.Dispose();
            try { if (!_process.WaitForExit(500)) _process.Kill(); } catch (InvalidOperationException) { }
            _process.Dispose();
        }
    }
}
