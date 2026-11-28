using System.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Privacy;
using Xunit;

namespace OnlyWorkspace.Tests;

/// <summary>#319: the model is downloaded only from the source fixed in the product, and installed only after the hashes match.</summary>
public sealed class PiiModelInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "model-install-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Weights = Encoding.UTF8.GetBytes("synthetic onnx weights");
    private static readonly byte[] Tokenizer = Encoding.UTF8.GetBytes("synthetic tokenizer");
    private static readonly byte[] Config = Encoding.UTF8.GetBytes("synthetic config");

    public PiiModelInstallerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    private static PiiModelSource Source() => new()
    {
        Repository = "example/synthetic-model",
        Revision = new string('a', 40),
        DisplayName = "synthetic-1",
        Files =
        {
            new PiiModelFile { RemotePath = "onnx/model.onnx", LocalName = "model.onnx", Size = Weights.Length, Sha256 = Hash(Weights) },
            new PiiModelFile { RemotePath = "tokenizer.json", LocalName = "tokenizer.json", Size = Tokenizer.Length, Sha256 = Hash(Tokenizer) },
            new PiiModelFile { RemotePath = "config.json", LocalName = "config.json", Size = Config.Length, Sha256 = Hash(Config) },
        }
    };

    private sealed class FakeHost : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public readonly List<Uri> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (!Files.TryGetValue(request.RequestUri!.AbsolutePath, out var bytes)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request });
        }
    }

    private static FakeHost Host(PiiModelSource source)
    {
        var host = new FakeHost();
        host.Files["/example/synthetic-model/resolve/" + source.Revision + "/onnx/model.onnx"] = Weights;
        host.Files["/example/synthetic-model/resolve/" + source.Revision + "/tokenizer.json"] = Tokenizer;
        host.Files["/example/synthetic-model/resolve/" + source.Revision + "/config.json"] = Config;
        return host;
    }

    [Fact]
    public void ShippedSource_IsComplete_AndPinnedToOneCommit()
    {
        var source = PiiModelSource.Default!;
        source.Validate();
        Assert.Equal(40, source.Revision.Length);
        Assert.StartsWith("https://huggingface.co/rizzoaiacademy/rizzo-pii-0.3B/resolve/" + source.Revision + "/", source.UrlOf(source.Files[0]).ToString());
        Assert.Equal("model.onnx_data", source.Files[1].LocalName);
    }

    [Fact]
    public async Task Download_WritesAVerifiedFlatFolder_FromThePinnedRevisionOnly()
    {
        var source = Source(); var host = Host(source); string staging = Path.Combine(_root, "staging");
        await PiiModelInstaller.DownloadAsync(source, staging, host);
        var package = PiiModelPackage.Verify(staging);
        Assert.Equal("synthetic-1", package.Revision);
        Assert.Equal(new[] { "config.json", "tokenizer.json" }, package.Companions.Keys.OrderBy(k => k).ToArray());
        Assert.All(host.Requests, u => { Assert.Equal("https", u.Scheme); Assert.Contains(source.Revision, u.AbsolutePath); });
        Assert.Empty(Directory.GetFiles(staging, "*.part"));
    }

    [Fact]
    public async Task Download_RefusesAFileWithAnotherHash_AndKeepsNothing()
    {
        var source = Source(); var host = Host(source); string staging = Path.Combine(_root, "staging");
        host.Files["/example/synthetic-model/resolve/" + source.Revision + "/tokenizer.json"] = Encoding.UTF8.GetBytes("synthetic tokenizeR");
        await Assert.ThrowsAsync<InvalidDataException>(() => PiiModelInstaller.DownloadAsync(source, staging, host));
        Assert.False(File.Exists(Path.Combine(staging, "tokenizer.json")));
        Assert.False(File.Exists(Path.Combine(staging, PiiModelPackage.ManifestFileName)));
        Assert.Empty(Directory.GetFiles(staging, "*.part"));
    }

    [Fact]
    public async Task Download_RefusesALongerFile_AndAMissingOne()
    {
        var source = Source(); var host = Host(source);
        host.Files["/example/synthetic-model/resolve/" + source.Revision + "/config.json"] = Encoding.UTF8.GetBytes("synthetic config and more");
        await Assert.ThrowsAsync<InvalidDataException>(() => PiiModelInstaller.DownloadAsync(source, Path.Combine(_root, "a"), host));
        host.Files.Remove("/example/synthetic-model/resolve/" + source.Revision + "/config.json");
        await Assert.ThrowsAsync<HttpRequestException>(() => PiiModelInstaller.DownloadAsync(source, Path.Combine(_root, "b"), host));
    }

    [Fact]
    public void Source_MustBeCompleteAndFlat()
    {
        var shortRevision = Source(); shortRevision.Revision = "main";
        Assert.Throws<InvalidDataException>(() => shortRevision.Validate());
        var nested = Source(); nested.Files[1].LocalName = "sub\\tokenizer.json";
        Assert.Throws<InvalidDataException>(() => nested.Validate());
        var noModel = Source(); noModel.Files.RemoveAt(0);
        Assert.Throws<InvalidDataException>(() => noModel.Validate());
        Source().Validate();
    }

    [Fact]
    public async Task Install_ReplacesTheOldFolder_AndCopiesOnlyWhatTheManifestLists()
    {
        var source = Source(); string staging = Path.Combine(_root, "staging"), target = Path.Combine(_root, "recognizer", "model");
        await PiiModelInstaller.DownloadAsync(source, staging, Host(source));
        File.WriteAllText(Path.Combine(staging, "planted.txt"), "not listed");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "old.bin"), "previous model");
        PiiModelInstaller.Install(staging, target);
        Assert.Equal(new[] { "config.json", "model.json", "model.onnx", "tokenizer.json" }, Directory.GetFiles(target).Select(Path.GetFileName).OrderBy(n => n).ToArray());
        Assert.False(Directory.Exists(target + ".old")); Assert.False(Directory.Exists(target + ".new"));
        PiiModelPackage.Verify(target);
    }

    [Fact]
    public async Task Status_DistinguishesMissingReadyCorruptAndDifferent()
    {
        var source = Source(); string staging = Path.Combine(_root, "staging"), target = Path.Combine(_root, "recognizer", "model");
        Assert.Equal(PiiModelState.Missing, PiiModelStatus.Check(target, source).State);
        await PiiModelInstaller.DownloadAsync(source, staging, Host(source));
        PiiModelInstaller.Install(staging, target);
        var ready = PiiModelStatus.Check(target, source);
        Assert.Equal(PiiModelState.Ready, ready.State); Assert.False(ready.NeedsInstall);

        var other = Source(); other.Files[2].Sha256 = Hash(Encoding.UTF8.GetBytes("another config"));
        Assert.Equal(PiiModelState.Different, PiiModelStatus.Check(target, other).State);

        File.WriteAllText(Path.Combine(target, "tokenizer.json"), "tampered");
        var corrupt = PiiModelStatus.Check(target, source);
        Assert.Equal(PiiModelState.Corrupt, corrupt.State); Assert.True(corrupt.NeedsInstall);
        File.Delete(Path.Combine(target, "tokenizer.json"));
        Assert.Equal(PiiModelState.Corrupt, PiiModelStatus.Check(target, source).State);
    }

    [Fact]
    public async Task InstallModelCommand_UsesOnlyAStagingFolderMadeByTheTool()
    {
        var source = Source(); string target = Path.Combine(_root, "recognizer", "model");
        string staging = OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.CreateStagingDirectory();
        try
        {
            await PiiModelInstaller.DownloadAsync(source, staging, Host(source));
            Assert.Equal(0, OnlyWorkspace.ConfigTool.Program.InstallModel(new[] { "install-model", staging }, target));
            PiiModelPackage.Verify(target);
            Assert.Equal("OK", File.ReadAllText(Path.Combine(staging, "result.txt")));
            string notStaging = Path.Combine(_root, "elsewhere"); Directory.CreateDirectory(notStaging);
            Assert.NotEqual(0, OnlyWorkspace.ConfigTool.Program.InstallModel(new[] { "install-model", notStaging }, Path.Combine(_root, "other")));
            Assert.False(Directory.Exists(Path.Combine(_root, "other")));
        }
        finally { OnlyWorkspace.ConfigTool.Services.ElevatedConfigWriter.DeleteStagingDirectory(staging); }
    }

    [Fact]
    public void ShippedSourceRevision_IsAcceptedByThePrivacyAudit()
    {
        // #36: the revision written to model.json is the NER revision of every audit record; one the ledger rejects blocks every cloud request.
        var source = PiiModelSource.Default!;
        var record = new PseudonymizationAudit { Utc = DateTime.UtcNow, Outcome = "completed" };
        record.Revisions["ner"] = source.DisplayName;
        record.Counts["PERSONA"] = 1;
        var store = new Infrastructure.Security.PseudonymizationAuditStore(Path.Combine(_root, "audit"));
        store.Write(record);
        Assert.Equal(source.DisplayName, store.Read().Single().Revisions["ner"]);
    }

    [Fact]
    public async Task Install_WithATamperedStagingFile_LeavesTheOldFolderInPlace()
    {
        var source = Source(); string staging = Path.Combine(_root, "staging"), target = Path.Combine(_root, "recognizer", "model");
        await PiiModelInstaller.DownloadAsync(source, staging, Host(source));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "old.bin"), "previous model");
        File.WriteAllText(Path.Combine(staging, "model.onnx"), "tampered after the download");
        Assert.Throws<DataEgressBlockedException>(() => PiiModelInstaller.Install(staging, target));
        Assert.Equal("previous model", File.ReadAllText(Path.Combine(target, "old.bin")));
        Assert.False(Directory.Exists(target + ".new"));
    }
}
