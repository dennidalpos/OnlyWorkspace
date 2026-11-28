using System.Net;

namespace OnlyWorkspace.Tests;

// Fails if HttpClient buffers the response instead of opening its stream.
internal sealed class UnbufferedHttpContent : HttpContent
{
    private readonly Stream _stream;

    public UnbufferedHttpContent(string body, string mediaType = "text/event-stream")
        : this(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)), mediaType) { }

    public UnbufferedHttpContent(Stream stream, string mediaType = "text/event-stream")
    {
        _stream = stream;
        Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        throw new InvalidOperationException("Response body must not be buffered.");

    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(_stream);

    protected override bool TryComputeLength(out long length) { length = 0; return false; }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _stream.Dispose();
        base.Dispose(disposing);
    }
}

// Blocks after its prefix until disposed, exercising the net48-compatible watchdog.
internal sealed class PendingReadStream : MemoryStream
{
    private readonly TaskCompletionSource<int> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool WasDisposed { get; private set; }

    public PendingReadStream(string prefix) : base(System.Text.Encoding.UTF8.GetBytes(prefix)) { }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (Position < Length) return base.ReadAsync(buffer, offset, count, cancellationToken);
        Waiting.TrySetResult(true);
        return _pending.Task;
    }

#if NET
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Position < Length) return base.ReadAsync(buffer, cancellationToken);
        Waiting.TrySetResult(true);
        return new ValueTask<int>(_pending.Task);
    }
#endif

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        _pending.TrySetException(new ObjectDisposedException(nameof(PendingReadStream)));
        base.Dispose(disposing);
    }
}
