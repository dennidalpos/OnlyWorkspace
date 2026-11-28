using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Infrastructure.Privacy;

/// <summary>
/// Download (as the user, into a staging folder) and installation (as administrator, into the folder next to the recognizer) of
/// the model (#319). Nothing is used that does not match the hashes fixed in <see cref="PiiModelSource"/>, and the installed folder
/// is checked again by <see cref="PiiModelPackage.Verify"/> before it replaces the old one.
/// </summary>
public static class PiiModelInstaller
{
    public sealed class Progress
    {
        public long Done { get; set; }
        public long Total { get; set; }
        public string File { get; set; } = string.Empty;
    }

    /// <summary>Downloads every file of the source into <paramref name="stagingDir"/> and writes model.json. Only the model files travel: no mail text, no profile data.</summary>
    /// <exception cref="InvalidDataException">A file does not match its size or hash.</exception>
    public static async Task DownloadAsync(PiiModelSource source, string stagingDir, HttpMessageHandler? handler = null,
        IProgress<Progress>? progress = null, CancellationToken ct = default)
    {
        source.Validate();
        Directory.CreateDirectory(stagingDir);
        using var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        long total = source.TotalSize, done = 0;
        var companions = new Dictionary<string, string>();
        foreach (var file in source.Files)
        {
            string target = Path.Combine(stagingDir, file.LocalName), part = target + ".part";
            using var request = new HttpRequestMessage(HttpMethod.Get, source.UrlOf(file));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Download non protetto da HTTPS.");
            string hash;
            long written = 0;
            using (var sha = SHA256.Create())
            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > file.Size) { output.Dispose(); File.Delete(part); throw new InvalidDataException("File del modello più grande del previsto."); }
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await output.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                    done += read;
                    progress?.Report(new Progress { Done = done, Total = total, File = file.LocalName });
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                hash = BitConverter.ToString(sha.Hash!).Replace("-", "");
            }
            if (written != file.Size || !string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new InvalidDataException("File del modello non corrispondente all'impronta attesa: nessuna installazione.");
            }
            File.Delete(target);
            File.Move(part, target);
            if (!string.Equals(file.LocalName, source.ModelFile, StringComparison.OrdinalIgnoreCase)) companions[file.LocalName] = file.Sha256.ToUpperInvariant();
        }
        var modelFile = source.Files.Find(f => string.Equals(f.LocalName, source.ModelFile, StringComparison.OrdinalIgnoreCase))!;
        var manifest = new PiiModelPackage { Revision = source.DisplayName, File = modelFile.LocalName, Sha256 = modelFile.Sha256.ToUpperInvariant(), Companions = companions };
        File.WriteAllText(Path.Combine(stagingDir, PiiModelPackage.ManifestFileName), JsonSerializer.Serialize(manifest));
        PiiModelPackage.Verify(stagingDir);
    }

    /// <summary>
    /// Replaces <paramref name="targetDir"/> with the verified content of <paramref name="stagingDir"/>. The old folder is put back
    /// if anything fails. Run elevated: the target is under the install folder, which only administrators can write.
    /// </summary>
    public static void Install(string stagingDir, string targetDir)
    {
        PiiModelPackage.Verify(stagingDir);
        string fresh = targetDir.TrimEnd('\\') + ".new", old = targetDir.TrimEnd('\\') + ".old";
        DeleteTree(fresh); DeleteTree(old);
        Directory.CreateDirectory(fresh);
        try
        {
            // Only what the manifest lists is copied (the staging folder belongs to the user), and the copy is verified, not the original.
            var package = PiiModelPackage.Verify(stagingDir);
            var names = new List<string> { PiiModelPackage.ManifestFileName, package.File };
            names.AddRange(package.Companions.Keys);
            foreach (var name in names) File.Copy(Path.Combine(stagingDir, name), Path.Combine(fresh, name));
            PiiModelPackage.Verify(fresh);
        }
        catch { DeleteTree(fresh); throw; }
        bool moved = false;
        try
        {
            if (Directory.Exists(targetDir)) { Directory.Move(targetDir, old); moved = true; }
            Directory.Move(fresh, targetDir);
        }
        catch
        {
            if (moved && !Directory.Exists(targetDir)) Directory.Move(old, targetDir);
            DeleteTree(fresh);
            throw;
        }
        DeleteTree(old);
    }

    private static void DeleteTree(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
