using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace OnlyWorkspace.Infrastructure.Archive;

internal sealed class ArchivePipeRequest
{
    public int Version { get; set; } = ArchivePipe.Version;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Profile { get; set; } = string.Empty;
    public string Peer { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public JsonElement[] Arguments { get; set; } = Array.Empty<JsonElement>();
    public long[] Acknowledged { get; set; } = Array.Empty<long>();
}

internal sealed class ArchivePipeResponse
{
    public int Version { get; set; } = ArchivePipe.Version;
    public string Id { get; set; } = string.Empty;
    public JsonElement Result { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}

internal static class ArchivePipe
{
    internal const int Version = 16;
    private const int MaxFrameBytes = 64 * 1024 * 1024;
    private const int MaxMessageBytes = 256 * 1024 * 1024;

    internal static bool BatchIdentity(string id, out string peer, out long sequence)
    {
        peer = string.Empty; sequence = 0;
        int split = id.IndexOf(':');
        if (split != 32 || !Guid.TryParseExact(id.Substring(0, split), "N", out _) ||
            !long.TryParse(id.Substring(split + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out sequence) || sequence <= 0) return false;
        peer = id.Substring(0, split);
        return id == peer + ":" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static TimeSpan RequestTimeout(string operation) => operation is "Open" or "Profiles" or "OutlookSources" or "Compact" or "OptimizeIndex" or "FinishRescan" ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(30);
    internal static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate
    };

    internal static string UserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new UnauthorizedAccessException("Windows user identity unavailable.");
        }
    }

    internal static string Name(string directory)
    {
        using var hash = SHA256.Create();
        string path = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        string digest = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(path))).Replace("-", "");
        return "OnlyWorkspace_Archive_" + UserSid + "_" + digest;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool Inherit;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances, uint outputBuffer, uint inputBuffer, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    internal static NamedPipeServerStream CreateServer(string name, bool first)
    {
        // Explicit user SID also works when ConfigTool runs elevated; reject network clients in the OS.
        var security = new RawSecurityDescriptor("D:P(D;;GA;;;NU)(A;;GA;;;" + UserSid + ")");
        var bytes = new byte[security.BinaryLength];
        security.GetBinaryForm(bytes, 0);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = pin.AddrOfPinnedObject() };
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, 0x40000003u | (first ? 0x00080000u : 0), 0x8u, 8, 65536, 65536, 0, ref attributes);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new System.ComponentModel.Win32Exception(error);
            }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { pin.Free(); }
    }

    internal static void ValidateClient(NamedPipeServerStream pipe)
    {
        string expected = UserSid;
        bool valid = false;
        pipe.RunAsClient(() =>
        {
            using var client = WindowsIdentity.GetCurrent(true);
            valid = client?.User?.Value == expected && !(client.Groups?.Any(g => g.Value == "S-1-5-2") ?? true);
        });
        if (!valid) throw new UnauthorizedAccessException("Only a local client of this Windows user may access the archive.");
    }

    internal static void ValidateServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, 0x8, out var token))
            throw new UnauthorizedAccessException("The archive server identity cannot be verified.");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            if (identity.User?.Value != UserSid) throw new UnauthorizedAccessException("The archive server belongs to another Windows user.");
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaxMessageBytes) throw new InvalidDataException("The archive message exceeds the transport limit; no text was truncated.");
        byte[] length = BitConverter.GetBytes(bytes.Length <= MaxFrameBytes ? bytes.Length : -1);
        await stream.WriteAsync(length, 0, length.Length, ct).ConfigureAwait(false);
        for (int offset = 0; offset < bytes.Length;)
        {
            int count = Math.Min(MaxFrameBytes, bytes.Length - offset);
            if (bytes.Length > MaxFrameBytes) await stream.WriteAsync(BitConverter.GetBytes(count), 0, 4, ct).ConfigureAwait(false);
            await stream.WriteAsync(bytes, offset, count, ct).ConfigureAwait(false);
            offset += count;
        }
        if (bytes.Length > MaxFrameBytes) await stream.WriteAsync(new byte[4], 0, 4, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var length = new byte[4];
        await ReadExactAsync(stream, length, ct).ConfigureAwait(false);
        int count = BitConverter.ToInt32(length, 0);
        if (count == -1)
        {
            using var message = new MemoryStream();
            while (true)
            {
                await ReadExactAsync(stream, length, ct).ConfigureAwait(false);
                count = BitConverter.ToInt32(length, 0);
                if (count == 0) break;
                if (count < 0 || count > MaxFrameBytes || message.Length + count > MaxMessageBytes)
                    throw new InvalidDataException("Invalid archive message segment length.");
                var chunk = new byte[count];
                await ReadExactAsync(stream, chunk, ct).ConfigureAwait(false);
                await message.WriteAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false);
            }
            return JsonSerializer.Deserialize<T>(new ReadOnlySpan<byte>(message.GetBuffer(), 0, checked((int)message.Length)), Json) ?? throw new InvalidDataException("Empty archive message.");
        }
        if (count <= 0 || count > MaxFrameBytes) throw new InvalidDataException("Invalid archive message length.");
        var bytes = new byte[count];
        await ReadExactAsync(stream, bytes, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty archive message.");
    }

    private static async Task ReadExactAsync(Stream stream, byte[] bytes, CancellationToken ct)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes, offset, bytes.Length - offset, ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The archive connection ended before the complete message.");
            offset += read;
        }
    }
}
