using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Infrastructure.Privacy;

namespace OnlyWorkspace.Tests;

/// <summary>The built OnlyWorkspace.PiiRecognizer.exe (copied to "recognizer" next to the tests) with a synthetic model, #247.</summary>
public sealed class NerRecognizerProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ner-exe-" + Guid.NewGuid().ToString("N"));
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "recognizer", PiiRecognizerClient.HelperFileName);

    public NerRecognizerProcessTests()
    {
        Directory.CreateDirectory(_dir);
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic weights");
        File.WriteAllBytes(Path.Combine(_dir, "m.bin"), bytes);
        using var sha = SHA256.Create();
        File.WriteAllText(Path.Combine(_dir, PiiModelPackage.ManifestFileName),
            JsonSerializer.Serialize(new { Revision = "rev-exe", File = "m.bin", Sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "") }));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void RealProcess_AnswersOverThePipe_AndEndsWithItsParent()
    {
        Assert.True(File.Exists(Helper), "Recognizer exe missing next to the tests: " + Helper);
        using var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        string name = "OnlyWorkspace_Pii_" + ArchivePipe.UserSid + "_" + Guid.NewGuid().ToString("N");
        using var child = Process.Start(new ProcessStartInfo(Helper, $"--pipe {name} --model-dir \"{_dir}\\\\\" --parent {parent.Id}") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(10_000);
            ArchivePipe.ValidateServer(pipe);
            // The bundled model is a placeholder: a reply that carries an error proves the request went through the real server.
            var ex = Assert.Throws<InvalidDataException>(() => NerProtocol.Call(pipe, "Chiama Luigi Verdi", TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Equal("Recognizer error.", ex.Message);
            Assert.False(child.HasExited);

            parent.Kill();
            Assert.True(child.WaitForExit(10_000), "The recognizer outlived its parent.");
        }
        finally
        {
            try { if (!child.HasExited) child.Kill(); } catch (InvalidOperationException) { }
            try { if (!parent.HasExited) parent.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void RealProcess_StartedByTheClientFactory_RepliesAndStopsWhenTheChannelCloses()
    {
        using var client = new PiiRecognizerClient(_dir, new PiiRecognizerProcess(Helper, _dir));
        // The placeholder model answers with an error, which the client turns into a block (nothing is sent).
        Assert.Throws<DataEgressBlockedException>(() => client.Recognize("Chiama Luigi Verdi", TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal("rev-exe", client.ModelRevision);
    }
}
