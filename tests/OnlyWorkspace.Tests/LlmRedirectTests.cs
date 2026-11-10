using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.Tests;

[CollectionDefinition("Isolated TLS", DisableParallelization = true)]
public sealed class IsolatedTlsCollection { }

[Collection("Isolated TLS")]
public sealed class LlmRedirectTests
{
    public static IEnumerable<object[]> Redirects =>
        from status in new[] { 301, 302, 303, 307, 308 }
        from destination in new[] { "same", "host", "port", "downgrade" }
        select new object[] { status, destination };

    [Theory]
    [MemberData(nameof(Redirects))]
    public async Task RealTransport_NeverForwardsPostOrCredentials(int status, string destination)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        // A PFX roundtrip makes the private key usable by Schannel on net48 as well.
#if NETFRAMEWORK
        using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
#else
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
#endif
        var source = new TcpListener(IPAddress.Loopback, 0);
        var sink = new TcpListener(IPAddress.Loopback, 0);
        source.Start(); sink.Start();
        bool tls = destination == "downgrade";
        int port = ((IPEndPoint)source.LocalEndpoint).Port;
        int sinkPort = ((IPEndPoint)sink.LocalEndpoint).Port;
        string location = destination switch
        {
            "same" => $"http://127.0.0.1:{port}/target",
            "host" => $"http://localhost:{sinkPort}/target",
            "port" => $"http://127.0.0.1:{sinkPort}/target",
            _ => $"http://127.0.0.1:{sinkPort}/target"
        };
#if NETFRAMEWORK
        var previous = ServicePointManager.ServerCertificateValidationCallback;
#endif
        Task<string>? received = null;
        try
        {
            var handler = new HttpClientHandler();
#if NETFRAMEWORK
            // Process-local, serialized with every other test; trusts only this short-lived fixture certificate.
            if (tls) ServicePointManager.ServerCertificateValidationCallback = (_, cert, _, errors) =>
                cert?.GetCertHashString() == certificate.GetCertHashString() || errors == SslPolicyErrors.None;
#else
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                cert?.Thumbprint == certificate.Thumbprint || errors == SslPolicyErrors.None;
#endif
            received = ServeOnce(source, tls ? certificate : null, status, location);
            var profile = new LlmProfileConfig { BaseUrl = $"{(tls ? "https" : "http")}://127.0.0.1:{port}", UseSystemProxy = false };
            using var client = LlmHttp.CreateClient(profile, "Fixture", transport: handler);
            client.Timeout = TimeSpan.FromSeconds(5);
            using var post = new HttpRequestMessage(HttpMethod.Post, "start") { Content = new StringContent("SYNTHETIC-BODY") };
            foreach (var header in new[] { "x-api-key", "x-goog-api-key", "api-key", "Authorization" })
                post.Headers.TryAddWithoutValidation(header, "SYNTHETIC-KEY");
            using var response = await client.SendAsync(post);
            Assert.Equal(status, (int)response.StatusCode);
            var error = await Assert.ThrowsAsync<LlmRequestException>(() => LlmHttp.EnsureSuccessAsync(response, "Fixture", CancellationToken.None));
            Assert.Contains("Configura l'URL finale", error.Message);
            string original = await received;
            Assert.Contains("SYNTHETIC-BODY", original);
            Assert.Contains("SYNTHETIC-KEY", original);
            Assert.False(sink.Pending());
            Assert.False(source.Pending()); // no same-authority follow or retry either
        }
        finally
        {
#if NETFRAMEWORK
            ServicePointManager.ServerCertificateValidationCallback = previous;
#endif
            source.Stop(); sink.Stop();
            if (received != null) { try { await received; } catch (Exception) { } }
        }
    }

    private static async Task<string> ServeOnce(TcpListener listener, X509Certificate2? certificate, int status, string location)
    {
        using var socket = await listener.AcceptTcpClientAsync();
        using var network = socket.GetStream();
        using var stream = certificate == null ? (Stream)network : new SslStream(network);
        if (stream is SslStream ssl) await ssl.AuthenticateAsServerAsync(certificate!, false, SslProtocols.Tls12, false);
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        var text = new StringBuilder();
        int length = 0;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            text.AppendLine(line);
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
        }
        // net48 may use Expect: 100-continue.
        if (text.ToString().IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            byte[] interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
            await stream.WriteAsync(interim, 0, interim.Length);
        }
        var body = new char[length];
        int read = 0;
        while (read < length) { int n = await reader.ReadAsync(body, read, length - read); if (n == 0) break; read += n; }
        text.Append(body);
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Redirect\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, 0, response.Length);
        return text.ToString();
    }
}
