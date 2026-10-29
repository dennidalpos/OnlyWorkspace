using System;
using System.IO;
using System.IO.Compression;
using OnlyWorkspace.Infrastructure.Logging;
using Xunit;

namespace OnlyWorkspace.Tests;

public class DiagnosticsExporterTests
{
    [Theory]
    [InlineData("Windows 10 Pro", "26300", "Windows 11 Pro")]
    [InlineData("Windows 10 Pro", "22000", "Windows 11 Pro")]
    [InlineData("Windows 10 Pro", "19045", "Windows 10 Pro")]
    [InlineData("Windows Server 2022 Standard", "20348", "Windows Server 2022 Standard")]
    [InlineData("Windows 10 Enterprise", null, "Windows 10 Enterprise")]
    public void WindowsProductName_NamesWindows11ByItsBuild(string registryName, string? build, string expected) =>
        Assert.Equal(expected, WorkstationInfo.ProductName(registryName, build));

    [Fact]
    public void CreateDiagnosticsZip_GeneratesValidZipFileWithMetadata()
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"TestDiag_{Guid.NewGuid():N}.zip");

        try
        {
            var result = DiagnosticsExporter.CreateDiagnosticsZip(tempZip);

            Assert.True(File.Exists(result));
            Assert.True(new FileInfo(result).Length > 0);

            using var zip = ZipFile.OpenRead(result);
            var sysEntry = zip.GetEntry("diagnostics_system.json");
            var cfgEntry = zip.GetEntry("config_sanitized.json");

            Assert.NotNull(sysEntry);
            Assert.Null(cfgEntry);
            Assert.Single(zip.Entries);

            using var reader = new StreamReader(sysEntry!.Open());
            var sysJson = reader.ReadToEnd();
            Assert.Contains("Windows", sysJson);
            Assert.Contains("Outlook", sysJson);
            Assert.Contains("DotNetFramework", sysJson);
            Assert.Contains("ProductVersion", sysJson);
            Assert.Contains("WebView2Runtime", sysJson);
            Assert.Contains("\"Archive\"", sysJson);
            Assert.Contains("KeyPresent", sysJson);
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
    }
}
