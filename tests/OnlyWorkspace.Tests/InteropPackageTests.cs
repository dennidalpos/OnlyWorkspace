using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace OnlyWorkspace.Tests;

/// <summary>
/// The Office interop packages are a third-party NuGet repackaging of Microsoft's PIAs: the build must fail if the
/// restored DLLs are no longer the Microsoft-signed originals (strong name and Authenticode signature).
/// </summary>
public sealed class InteropPackageTests
{
    [Fact]
    public void BuiltAddin_EmbedsOfficeTypes_WithoutRuntimePiaReferences()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string binary = Path.Combine(root, "src", "OnlyWorkspace.Outlook", "bin", "Release", "net48", "OnlyWorkspace.Outlook.dll");
        var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(binary));
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name!.StartsWith("Microsoft.Office.Interop.", StringComparison.Ordinal));
    }

    private const string MicrosoftOfficeKeyToken = "71e9bce111e9429c";

    [Theory]
    [InlineData("Microsoft.Office.Interop.Outlook")]
    [InlineData("Microsoft.Office.Interop.Word")]
    public void RestoredInteropAssembly_IsTheMicrosoftSignedPia(string package)
    {
        string dll = RestoredDll(package);

        var name = System.Reflection.AssemblyName.GetAssemblyName(dll);
        Assert.Equal(MicrosoftOfficeKeyToken, BitConverter.ToString(name.GetPublicKeyToken()!).Replace("-", "").ToLowerInvariant());

        Assert.Equal(0, VerifyAuthenticode(dll));
#pragma warning disable SYSLIB0057 // Reads the signer of a signed file; the signature itself is checked above.
        using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(dll));
#pragma warning restore SYSLIB0057
        Assert.Contains("O=Microsoft Corporation", signer.Subject);
    }

    /// <summary>The check is not vacuous: an unsigned assembly (this test assembly) fails it.</summary>
    [Fact]
    public void UnsignedAssembly_FailsTheSignatureCheck()
    {
        Assert.NotEqual(0, VerifyAuthenticode(typeof(InteropPackageTests).Assembly.Location));
    }

    /// <summary>The DLL the Outlook project compiles against, from its restore output (package folder and version).</summary>
    private static string RestoredDll(string package)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "OnlyWorkspace.Outlook", "obj", "project.assets.json")));
        string folder = assets.RootElement.GetProperty("packageFolders").EnumerateObject().First().Name;
        var library = assets.RootElement.GetProperty("libraries").EnumerateObject()
            .Single(l => l.Name.StartsWith(package + "/", StringComparison.OrdinalIgnoreCase));
        string file = library.Value.GetProperty("files").EnumerateArray().Select(f => f.GetString()!)
            .Single(f => f.Equals("lib/net20/" + package + ".dll", StringComparison.OrdinalIgnoreCase));
        return Path.Combine(folder, library.Value.GetProperty("path").GetString()!, file);
    }

    /// <summary>WinVerifyTrust without UI or online revocation: 0 when the Authenticode signature is valid and trusted.</summary>
    private static int VerifyAuthenticode(string path)
    {
        var fileInfo = new WinTrustFileInfo { Size = Marshal.SizeOf<WinTrustFileInfo>(), FilePath = path };
        IntPtr pFile = Marshal.AllocHGlobal(fileInfo.Size);
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WinTrustData
            {
                Size = Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,          // WTD_UI_NONE
                RevocationChecks = 0,  // WTD_REVOKE_NONE
                UnionChoice = 1,       // WTD_CHOICE_FILE
                File = pFile,
                ProvFlags = 0x1000     // WTD_CACHE_ONLY_URL_RETRIEVAL: no network
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pFile);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public int Size;
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public int Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public int UiChoice;
        public int RevocationChecks;
        public int UnionChoice;
        public IntPtr File;
        public int StateAction;
        public IntPtr StateData;
        public string? UrlReference;
        public int ProvFlags;
        public int UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
}
