using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.PiiRecognizer;

/// <summary>
/// Local recognizer of personal data in free text, in its own process (crash and memory isolation from Outlook), #247.
/// Usage: OnlyWorkspace.PiiRecognizer.exe --pipe &lt;name&gt; --model-dir &lt;dir&gt; --parent &lt;pid&gt;.
/// The model is checked against the SHA-256 in model.json before any request is answered; the process ends with its parent.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            string pipeName = Arg(args, "--pipe"), directory = Arg(args, "--model-dir");
            if (!int.TryParse(Arg(args, "--parent"), out int parentId)) throw new ArgumentException("Missing --parent.");
            var package = PiiModelPackage.Verify(directory);
            using var parent = Process.GetProcessById(parentId);
            using var stop = new CancellationTokenSource();
            parent.EnableRaisingEvents = true;
            // A pipe read does not honour the token on .NET Framework, so the exit is forced rather than awaited.
            parent.Exited += (_, _) => { stop.Cancel(); Environment.Exit(0); };
            using var pipe = PiiPipeServer.Create(pipeName);
            pipe.WaitForConnectionAsync(stop.Token).Wait(TimeSpan.FromSeconds(10));
            if (!pipe.IsConnected) return 2;
            NerProtocol.ServeAsync(pipe, LoadModel(package), stop.Token, () => PiiPipeServer.ValidateClient(pipe)).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception) { return 1; }
    }

    private static string Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException("Missing " + name + ".");
    }

    // No inference backend is bundled until the model and its data licence are decided (#246, #245): requests are
    // answered with an error, which the add-in treats as a block.
    private static IPiiModel LoadModel(PiiModelPackage package) => new UnavailableModel(package.Revision);

    private sealed class UnavailableModel : IPiiModel
    {
        private readonly string _revision;
        public UnavailableModel(string revision) => _revision = revision;
        public IReadOnlyList<NerSpan> Recognize(string block, CancellationToken token) =>
            throw new NotSupportedException("No inference backend for model " + _revision + ".");
    }
}
