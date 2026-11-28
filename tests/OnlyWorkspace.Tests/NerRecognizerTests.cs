using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Privacy;
using OnlyWorkspace.PiiRecognizer;

namespace OnlyWorkspace.Tests;

/// <summary>Second masking layer (#247): additive strategy, hash-checked model, separate process over a user-only pipe.</summary>
public sealed class NerRecognizerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ner-" + Guid.NewGuid().ToString("N"));
    private static IReadOnlyList<ChatMessage> User(string text) => new[] { ChatMessage.User(text) };

    public NerRecognizerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Mask(string text, INerRecognizer? ner, TimeSpan? budget = null) =>
        new Pseudonymizer(recognizer: ner, budget: budget).Mask(User(text))[0].Content;

    [Fact]
    public void NeverRemovesARuleMask_WhateverTheModelReturns()
    {
        const string text = "Bonifico a Mario Rossi <m.rossi@esempio.it> su IT60X0542811101000000123456, CF RSSMRA80A01H501U, tel +39 333 1234567.";
        string[] ruleValues = { "m.rossi@esempio.it", "IT60X0542811101000000123456", "RSSMRA80A01H501U", "333 1234567" };
        foreach (var span in new[]
        {
            (0, text.Length),
            (text.IndexOf("IT60", StringComparison.Ordinal) - 5, 12),
            (text.IndexOf("m.rossi", StringComparison.Ordinal) + 2, 8),
            (text.IndexOf("RSSMRA", StringComparison.Ordinal) + 8, 30),
            (text.IndexOf("333", StringComparison.Ordinal), 4)
        })
        {
            int start = Math.Max(0, span.Item1), length = Math.Min(span.Item2, text.Length - start);
            var ner = new FixedRecognizer("t", new NerSpan { Start = start, Length = length, Label = "FULLNAME" });
            string withModel = Mask(text, ner);
            foreach (string value in ruleValues) Assert.DoesNotContain(value, withModel);
            // Every placeholder the rules alone produce is still there.
            foreach (var placeholder in System.Text.RegularExpressions.Regex.Matches(Mask(text, null), @"\[(?:EMAIL|IBAN|CF|TEL)_\d+\]").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value))
                Assert.Contains(placeholder, withModel);
        }
    }

    [Fact]
    public void AddsWhatTheRulesCannotSee_AndFusesAddressParts()
    {
        const string text = "Scrivere a Luigi Verdi, abitante in Rua Augusta 12, 1100 Lisboa.";
        var ner = new MaskedFreeTextTransportTests.LiteralRecognizer("t", ("Luigi Verdi", "FULLNAME"), ("Rua Augusta", "STREET"), ("12", "BUILDINGNUM"),
            ("1100", "ZIPCODE"), ("Lisboa", "CITY"), ("abitante", "UNMAPPED"));
        // Street, number, postcode and city are one mask; a label outside the catalog map is ignored.
        Assert.Equal("Scrivere a [PERSONA_1], abitante in [INDIRIZZO_1].", Mask(text, ner));
    }

    [Fact]
    public void EntityInTheOverlapOfTwoBlocks_IsOneMask()
    {
        string text = new string('x', 1850) + " Luigi Verdi " + new string('y', 1000);
        var ner = new MaskedFreeTextTransportTests.LiteralRecognizer("t", ("Luigi Verdi", "FULLNAME"));
        string masked = Mask(text, ner);
        Assert.Equal(2, ner.Calls);
        Assert.DoesNotContain("Luigi", masked);
        Assert.Contains("[PERSONA_1]", masked);
        Assert.DoesNotContain("[PERSONA_2]", masked);
    }

    [Fact]
    public void BudgetGrowsWithTheBlock_AndNeverExceedsTheRequestDeadline()
    {
        Assert.True(NerStrategy.BudgetFor(2000) > NerStrategy.BudgetFor(100));
        var ner = new MaskedFreeTextTransportTests.LiteralRecognizer("t");
        Mask("Testo di prova", ner, TimeSpan.FromMilliseconds(800));
        Assert.InRange(ner.LastBudget, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(800));
    }

    [Fact]
    public void ExhaustedBudget_Blocks()
    {
        var slow = new SlowRecognizer(TimeSpan.FromMilliseconds(300));
        Assert.Throws<DataEgressBlockedException>(() => Mask("Luigi Verdi", slow, TimeSpan.FromMilliseconds(60)));
    }

    [Theory]
    [InlineData(-1, 3)]
    [InlineData(0, 0)]
    [InlineData(5, 100)]
    public void InvalidSpans_Block(int start, int length)
    {
        var ner = new FixedRecognizer("t", new NerSpan { Start = start, Length = length, Label = "FULLNAME" });
        Assert.Throws<DataEgressBlockedException>(() => Mask("Luigi Verdi", ner));
    }

    [Fact]
    public void RecognizerFailure_Blocks()
    {
        Assert.Throws<DataEgressBlockedException>(() => Mask("Luigi Verdi", new FailingRecognizer()));
        Assert.Throws<DataEgressBlockedException>(() => Mask("Luigi Verdi", new UnverifiedRecognizer()));
    }

    [Fact]
    public void Cache_AnswersTheSecondPassWithoutTheModel()
    {
        var inner = new MaskedFreeTextTransportTests.LiteralRecognizer("t", ("Luigi Verdi", "FULLNAME"));
        var cache = new CachingNerRecognizer(inner, 8);
        const string text = "Scrivere a Luigi Verdi per conferma.";
        cache.PreAnalyze(text, CancellationToken.None);
        Assert.Equal(1, inner.Calls);
        string masked = Mask(text, cache);
        Assert.Equal(1, inner.Calls);
        Assert.DoesNotContain("Luigi", masked);
        Assert.Equal(1, cache.Count);
        for (int i = 0; i < 20; i++) cache.Recognize("blocco " + i, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(8, cache.Count);
    }

    [Fact]
    public void Model_MissingOrDifferentOrCorrect()
    {
        Assert.Throws<DataEgressBlockedException>(() => PiiModelPackage.Verify(_dir));
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic weights");
        File.WriteAllBytes(Path.Combine(_dir, "m.bin"), bytes);
        Manifest("rev-1", "m.bin", new string('0', 64));
        Assert.Throws<DataEgressBlockedException>(() => PiiModelPackage.Verify(_dir));
        Manifest("rev-1", "m.bin", Hash(bytes));
        Assert.Equal("rev-1", PiiModelPackage.Verify(_dir).Revision);
        Manifest("rev-1", "..\\m.bin", Hash(bytes));
        Assert.Throws<DataEgressBlockedException>(() => PiiModelPackage.Verify(_dir));
        Manifest("rev-1", "gone.bin", Hash(bytes));
        Assert.Throws<DataEgressBlockedException>(() => PiiModelPackage.Verify(_dir));
    }

    [Fact]
    public void Client_BlocksBeforeAnyProcess_WhenTheModelIsMissingOrChanged()
    {
        var factory = new PipeFactory(new EchoModel());
        using var client = new PiiRecognizerClient(_dir, factory);
        Assert.Throws<DataEgressBlockedException>(() => client.Recognize("Luigi Verdi", TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.Throws<DataEgressBlockedException>(() => _ = client.ModelRevision);
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic weights");
        File.WriteAllBytes(Path.Combine(_dir, "m.bin"), bytes);
        Manifest("rev-1", "m.bin", Hash(bytes));
        Assert.Single(client.Recognize("Luigi Verdi", TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(1, factory.Opened);
        // A model replaced after verification no longer matches its hash: nothing more is analysed.
        File.WriteAllBytes(Path.Combine(_dir, "m.bin"), Encoding.UTF8.GetBytes("tampered weights"));
        Assert.Throws<DataEgressBlockedException>(() => client.Recognize("Luigi Verdi", TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.Equal(1, factory.Opened);
    }

    [Fact]
    public void Client_RoundTripsOverAUserOnlyPipe_AndBlocksOnDeathOrTimeout()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic weights");
        File.WriteAllBytes(Path.Combine(_dir, "m.bin"), bytes);
        Manifest("rev-1", "m.bin", Hash(bytes));
        var factory = new PipeFactory(new EchoModel());
        using var client = new PiiRecognizerClient(_dir, factory);
        var spans = client.Recognize("Chiama Luigi Verdi", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(("FULLNAME", 7, 11), (spans[0].Label, spans[0].Start, spans[0].Length));
        Assert.Equal("rev-1", client.ModelRevision);

        // The process ends: the next request is blocked, then a fresh process is started.
        factory.Last!.Alive = false;
        Assert.Throws<DataEgressBlockedException>(() => client.Recognize("Luigi Verdi", TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.Single(client.Recognize("Luigi Verdi", TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(2, factory.Opened);

        // A model that outlasts the budget blocks and the half-read connection is dropped.
        var slow = new PipeFactory(new SlowModel(TimeSpan.FromSeconds(3)));
        using var impatient = new PiiRecognizerClient(_dir, slow, TimeSpan.Zero);
        Assert.Throws<DataEgressBlockedException>(() => impatient.Recognize("Luigi Verdi", TimeSpan.FromMilliseconds(300), CancellationToken.None));
        Assert.Throws<DataEgressBlockedException>(() =>
            new PiiRecognizerClient(_dir, new PipeFactory(new ThrowingModel())).Recognize("Luigi Verdi", TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public void Client_GivesOnlyTheFirstRequestOfAFreshProcessTheColdStartMargin()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic weights");
        File.WriteAllBytes(Path.Combine(_dir, "m.bin"), bytes);
        Manifest("rev-cold", "m.bin", Hash(bytes));
        var factory = new PipeFactory(new SlowModel(TimeSpan.FromMilliseconds(800)));
        using var client = new PiiRecognizerClient(_dir, factory, TimeSpan.FromSeconds(5));
        var budget = TimeSpan.FromMilliseconds(300);
        // The slow first answer fits only thanks to the margin; the same answer on the warm process is blocked.
        client.Recognize("Luigi Verdi", budget, CancellationToken.None);
        Assert.Throws<DataEgressBlockedException>(() => client.Recognize("Luigi Verdi", budget, CancellationToken.None));
        // A blocked request drops the connection: the next process is fresh and gets the margin again.
        client.Recognize("Luigi Verdi", budget, CancellationToken.None);
        Assert.Equal(2, factory.Opened);
    }

    [Fact]
    public void Cancellation_IsClean_AndTheNextRequestStartsAFreshProcess()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic weights");
        File.WriteAllBytes(Path.Combine(_dir, "m.bin"), bytes);
        Manifest("rev-c", "m.bin", Hash(bytes));
        var factory = new PipeFactory(new FirstCallSlowModel(TimeSpan.FromSeconds(3)));
        using var client = new PiiRecognizerClient(_dir, factory);

        // Already cancelled: no process, no pipe, nothing sent.
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => client.Recognize("Luigi Verdi", TimeSpan.FromSeconds(5), cancelled.Token));
        }
        Assert.Equal(0, factory.Opened);

        // Cancelled while the model works: the call returns promptly with the cancellation, not as a block or a timeout.
        using var midway = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => client.Recognize("Luigi Verdi", TimeSpan.FromSeconds(10), midway.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), "cancellation took " + watch.Elapsed);
        Assert.Equal(1, factory.Opened);

        // The half-used connection was dropped: the next request gets a fresh process.
        Assert.Single(client.Recognize("Luigi Verdi", TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(2, factory.Opened);
    }

    [Fact]
    public void StartFailure_Blocks_SendsNothing_AndTheNextRequestRestarts()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic weights");
        File.WriteAllBytes(Path.Combine(_dir, "m.bin"), bytes);
        Manifest("rev-s", "m.bin", Hash(bytes));
        var inner = new PipeFactory(new EchoModel());
        var flaky = new FlakyFactory(inner) { Failures = 2 };
        using var client = new PiiRecognizerClient(_dir, flaky);

        // The process does not start (first a timeout, then a refused pipe): each request is blocked.
        Assert.Throws<DataEgressBlockedException>(() => client.Recognize("Luigi Verdi", TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.Throws<DataEgressBlockedException>(() => client.Recognize("Luigi Verdi", TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.Equal(0, inner.Opened);
        // The helper is back: the client starts it again without any manual reset.
        Assert.Single(client.Recognize("Luigi Verdi", TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(3, flaky.Attempts);
    }

    [Fact]
    public void NoPartialSend_WhenTheProcessIsGone_OrTheBlockIsRejected()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic weights");
        File.WriteAllBytes(Path.Combine(_dir, "m.bin"), bytes);
        Manifest("rev-p", "m.bin", Hash(bytes));
        var recording = new RecordingFactory();
        using var client = new PiiRecognizerClient(_dir, recording);

        byte[] body = Encoding.UTF8.GetBytes("{\"Spans\":[]}");
        recording.Channel.Reply = BitConverter.GetBytes(body.Length).Concat(body).ToArray();
        Assert.Empty(client.Recognize("Luigi Verdi", TimeSpan.FromSeconds(2), CancellationToken.None));
        int sent = recording.Channel.Written;
        Assert.True(sent > 0);

        // The process died after that request: the next text is never written to its pipe.
        recording.Channel.Alive = false;
        Assert.Throws<DataEgressBlockedException>(() => client.Recognize("Mario Rossi", TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.Equal(sent, recording.Channel.Written);

        // A reply cut in the middle yields no spans at all, only a block.
        recording.Channel.Alive = true;
        recording.Channel.Reply = new byte[] { 0x20, 0x00, 0x00, 0x00, (byte)'{' };
        Assert.Throws<DataEgressBlockedException>(() => client.Recognize("Luigi Verdi", TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    private sealed class FirstCallSlowModel : IPiiModel
    {
        private readonly TimeSpan _delay;
        private int _calls;
        public FirstCallSlowModel(TimeSpan delay) => _delay = delay;
        public IReadOnlyList<NerSpan> Recognize(string block, CancellationToken token)
        {
            if (Interlocked.Increment(ref _calls) == 1) token.WaitHandle.WaitOne(_delay);
            return new[] { new NerSpan { Start = 0, Length = 5, Label = "FULLNAME" } };
        }
    }

    private sealed class FlakyFactory : INerChannelFactory
    {
        private readonly INerChannelFactory _inner;
        public FlakyFactory(INerChannelFactory inner) => _inner = inner;
        public int Failures { get; set; }
        public int Attempts { get; private set; }
        public INerChannel Open(PiiModelPackage model, CancellationToken token)
        {
            Attempts++;
            if (Failures-- > 0) throw Attempts == 1 ? new TimeoutException("pipe") : new IOException("refused");
            return _inner.Open(model, token);
        }
    }

    /// <summary>A channel that records what the client writes and answers with a canned (possibly truncated) frame.</summary>
    private sealed class RecordingFactory : INerChannelFactory
    {
        public RecordingChannel Channel { get; } = new();
        public INerChannel Open(PiiModelPackage model, CancellationToken token) => Channel;
    }

    private sealed class RecordingChannel : INerChannel
    {
        private readonly RecordingStream _stream = new();
        public bool Alive { get; set; } = true;
        public byte[] Reply { set => _stream.Reply = value; }
        public int Written => _stream.Written;
        public Stream Stream => _stream;
        public bool IsAlive => Alive;
        public void Dispose() { }
    }

    private sealed class RecordingStream : Stream
    {
        private MemoryStream _reply = new();
        public int Written { get; private set; }
        public byte[] Reply { set => _reply = new MemoryStream(value); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _reply.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written += count;
    }

    [Fact]
    public async Task EndToEnd_ClientAsStrategy_MasksThroughThePipe()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic weights");
        File.WriteAllBytes(Path.Combine(_dir, "m.bin"), bytes);
        Manifest("rev-9", "m.bin", Hash(bytes));
        using var client = new PiiRecognizerClient(_dir, new PipeFactory(new EchoModel()));
        var pseudonymizer = new Pseudonymizer(recognizer: client);
        string masked = (await Task.Run(() => pseudonymizer.Mask(User("Chiama Luigi Verdi domani"))))[0].Content;
        Assert.Equal("Chiama [PERSONA_1] domani", masked);
        Assert.Equal("rev-9", pseudonymizer.Audit.Revisions["ner"]);
    }

    [Fact]
    public void Model_CompanionFiles_AreVerifiedLikeTheModel()
    {
        byte[] model = Encoding.UTF8.GetBytes("synthetic weights"), data = Encoding.UTF8.GetBytes("external weights");
        File.WriteAllBytes(Path.Combine(_dir, "m.bin"), model);
        File.WriteAllBytes(Path.Combine(_dir, "m.data"), data);
        void Write(string name, string sha) => File.WriteAllText(Path.Combine(_dir, PiiModelPackage.ManifestFileName), JsonSerializer.Serialize(new
        {
            Revision = "rev-1",
            File = "m.bin",
            Sha256 = Hash(model),
            Companions = new Dictionary<string, string> { [name] = sha }
        }));
        Write("m.data", Hash(data));
        Assert.Equal("m.data", Assert.Single(PiiModelPackage.Verify(_dir).Companions).Key);
        Write("m.data", new string('0', 64));
        Assert.Throws<DataEgressBlockedException>(() => PiiModelPackage.Verify(_dir));
        Write("gone.data", Hash(data));
        Assert.Throws<DataEgressBlockedException>(() => PiiModelPackage.Verify(_dir));
        Write("..\\m.data", Hash(data));
        Assert.Throws<DataEgressBlockedException>(() => PiiModelPackage.Verify(_dir));
        File.WriteAllBytes(Path.Combine(_dir, "m.data"), Encoding.UTF8.GetBytes("tampered"));
        Write("m.data", Hash(data));
        Assert.Throws<DataEgressBlockedException>(() => PiiModelPackage.Verify(_dir));
    }

    /// <summary>The model folder example of docs/tecnico/pseudonimizzazione.md (#317): same names, same synthetic contents, same hashes.</summary>
    [Fact]
    public void Model_DocumentedFolderLayout_Verifies()
    {
        File.WriteAllText(Path.Combine(_dir, "model.onnx"), "synthetic onnx weights");
        File.WriteAllText(Path.Combine(_dir, "tokenizer.json"), "synthetic tokenizer");
        File.WriteAllText(Path.Combine(_dir, "config.json"), "synthetic config");
        const string manifest = "{\"Revision\":\"example-1\",\"File\":\"model.onnx\",\"Sha256\":\"CEEB4CB6D73B90F65DB5D376A6A8351E31B5711B0D5F6002CC5DBB695163B45E\"," +
            "\"Companions\":{\"tokenizer.json\":\"EC0AC6D52CD7E7D35C59262357813237CDA55781FE0974E9C1F46FA1B7770A8F\"," +
            "\"config.json\":\"EC356EEA78FB4CBCCC9885FA5C4159F62EE2B8D547BD5EE38E7E240D637B1FF5\"}}";
        File.WriteAllText(Path.Combine(_dir, PiiModelPackage.ManifestFileName), manifest);
        var package = PiiModelPackage.Verify(_dir);
        Assert.Equal(2, package.Companions.Count);
    }

    private void Manifest(string revision, string file, string sha) =>
        File.WriteAllText(Path.Combine(_dir, PiiModelPackage.ManifestFileName), JsonSerializer.Serialize(new { Revision = revision, File = file, Sha256 = sha }));

    private static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    private sealed class FixedRecognizer : INerRecognizer
    {
        private readonly NerSpan[] _spans;
        public FixedRecognizer(string revision, params NerSpan[] spans) { ModelRevision = revision; _spans = spans; }
        public string ModelRevision { get; }
        public IReadOnlyList<NerSpan> Recognize(string block, TimeSpan budget, CancellationToken token) => _spans.Where(s => s.Start <= block.Length).Select(s => new NerSpan { Start = s.Start, Length = s.Length, Label = s.Label }).ToArray();
    }

    private sealed class SlowRecognizer : INerRecognizer
    {
        private readonly TimeSpan _delay;
        public SlowRecognizer(TimeSpan delay) => _delay = delay;
        public string ModelRevision => "slow";
        public IReadOnlyList<NerSpan> Recognize(string block, TimeSpan budget, CancellationToken token) { Thread.Sleep(_delay); return Array.Empty<NerSpan>(); }
    }

    private sealed class FailingRecognizer : INerRecognizer
    {
        public string ModelRevision => "failing";
        public IReadOnlyList<NerSpan> Recognize(string block, TimeSpan budget, CancellationToken token) => throw new InvalidOperationException("model crashed");
    }

    private sealed class UnverifiedRecognizer : INerRecognizer
    {
        public string ModelRevision => throw new DataEgressBlockedException("Modello assente");
        public IReadOnlyList<NerSpan> Recognize(string block, TimeSpan budget, CancellationToken token) => Array.Empty<NerSpan>();
    }

    private sealed class EchoModel : IPiiModel
    {
        public IReadOnlyList<NerSpan> Recognize(string block, CancellationToken token)
        {
            int at = block.IndexOf("Luigi Verdi", StringComparison.Ordinal);
            return at < 0 ? Array.Empty<NerSpan>() : new[] { new NerSpan { Start = at, Length = 11, Label = "FULLNAME" } };
        }
    }

    private sealed class SlowModel : IPiiModel
    {
        private readonly TimeSpan _delay;
        public SlowModel(TimeSpan delay) => _delay = delay;
        public IReadOnlyList<NerSpan> Recognize(string block, CancellationToken token) { token.WaitHandle.WaitOne(_delay); return Array.Empty<NerSpan>(); }
    }

    private sealed class ThrowingModel : IPiiModel
    {
        public IReadOnlyList<NerSpan> Recognize(string block, CancellationToken token) => throw new NotSupportedException("No backend");
    }

    /// <summary>Runs the recognizer's real pipe server and protocol loop in this process.</summary>
    private sealed class PipeFactory : INerChannelFactory
    {
        private readonly IPiiModel _model;
        public PipeFactory(IPiiModel model) => _model = model;
        public int Opened { get; private set; }
        public TestChannel? Last { get; private set; }

        public INerChannel Open(PiiModelPackage model, CancellationToken token)
        {
            Opened++;
            string name = "OnlyWorkspace_Test_" + Guid.NewGuid().ToString("N");
            var server = PiiPipeServer.Create(name);
            var cancel = new CancellationTokenSource();
            var serving = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync(cancel.Token);
                await NerProtocol.ServeAsync(server, _model, cancel.Token, () => PiiPipeServer.ValidateClient(server));
            });
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(5000);
            return Last = new TestChannel(client, server, cancel, serving);
        }
    }

    private sealed class TestChannel : INerChannel
    {
        private readonly NamedPipeClientStream _client;
        private readonly NamedPipeServerStream _server;
        private readonly CancellationTokenSource _cancel;
        private readonly Task _serving;
        public TestChannel(NamedPipeClientStream client, NamedPipeServerStream server, CancellationTokenSource cancel, Task serving) { _client = client; _server = server; _cancel = cancel; _serving = serving; }
        public bool Alive { get; set; } = true;
        public Stream Stream => _client;
        public bool IsAlive => Alive;
        public void Dispose()
        {
            _cancel.Cancel();
            _client.Dispose();
            try { _serving.Wait(2000); } catch (AggregateException) { }
            _server.Dispose();
            _cancel.Dispose();
        }
    }
}
