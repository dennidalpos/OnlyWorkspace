using System.Collections.Generic;

namespace OnlyWorkspace.Core;

/// <summary>One component OnlyWorkspace uses, with the license declared by its publisher.</summary>
public sealed class ThirdPartyComponent
{
    public ThirdPartyComponent(string name, string version, string license, string url, bool distributed = true, string note = "")
    {
        Name = name;
        Version = version;
        License = license;
        Url = url;
        Distributed = distributed;
        Note = note;
    }

    /// <summary>NuGet id or product name.</summary>
    public string Name { get; }
    public string Version { get; }
    /// <summary>SPDX expression from the package metadata, or the publisher's own terms.</summary>
    public string License { get; }
    public string Url { get; }
    /// <summary>False for build and test tools that never reach the user's PC.</summary>
    public bool Distributed { get; }
    public string Note { get; }
}

/// <summary>
/// Credits shown in the ConfigTool (Informazioni). Licenses come from each package's nuspec on
/// api.nuget.org (checked 2026-10-15); THIRD-PARTY-NOTICES.txt carries notices and license references. A test keeps this list
/// in step with the PackageReference items of the projects.
/// </summary>
public static class ThirdPartyComponents
{
    public const string Author = "Danny Perondi";
    public const string Publisher = "OnlyApps";
    public const string GitHubUrl = "https://github.com/dennidalpos";
    public const string License = "Software proprietario OnlyApps";

    public static IReadOnlyList<ThirdPartyComponent> All { get; } = new[]
    {
        new ThirdPartyComponent("Microsoft.Web.WebView2", "1.0.4314.35", "Licenza Microsoft (stile BSD, LICENSE.txt del pacchetto)", "https://www.nuget.org/packages/Microsoft.Web.WebView2"),
        new ThirdPartyComponent("Microsoft.Data.Sqlite.Core", "10.0.14", "MIT", "https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core"),
        new ThirdPartyComponent("SQLite3MC.PCLRaw.bundle", "2.6.1", "MIT", "https://www.nuget.org/packages/SQLite3MC.PCLRaw.bundle"),
        new ThirdPartyComponent("SQLite3MC.PCLRaw.lib", "2.6.1", "MIT", "https://www.nuget.org/packages/SQLite3MC.PCLRaw.lib"),
        new ThirdPartyComponent("SQLitePCLRaw.core", "3.0.2", "Apache-2.0", "https://www.nuget.org/packages/SQLitePCLRaw.core", note: "dipendenza transitiva"),
        new ThirdPartyComponent("Serilog", "4.4.0", "Apache-2.0", "https://www.nuget.org/packages/Serilog"),
        new ThirdPartyComponent("Serilog.Sinks.File", "7.0.0", "Apache-2.0", "https://www.nuget.org/packages/Serilog.Sinks.File"),
        new ThirdPartyComponent("PdfPig", "0.1.16", "Apache-2.0", "https://www.nuget.org/packages/PdfPig"),
        new ThirdPartyComponent("DocumentFormat.OpenXml", "3.5.1", "MIT", "https://www.nuget.org/packages/DocumentFormat.OpenXml"),
        new ThirdPartyComponent("OpenMcdf", "3.4.0", "MPL-2.0", "https://www.nuget.org/packages/OpenMcdf"),
        new ThirdPartyComponent("ExcelDataReader", "3.9.0", "MIT", "https://www.nuget.org/packages/ExcelDataReader"),
        new ThirdPartyComponent("MimeKitLite", "4.18.1", "MIT", "https://www.nuget.org/packages/MimeKitLite"),
        new ThirdPartyComponent("Nager.PublicSuffix", "3.8.0", "MIT", "https://www.nuget.org/packages/Nager.PublicSuffix"),
        new ThirdPartyComponent("Public Suffix List", "-", "MPL-2.0", "https://publicsuffix.org/"),
        new ThirdPartyComponent("System.Text.Json", "10.0.14", "MIT", "https://www.nuget.org/packages/System.Text.Json"),
        new ThirdPartyComponent("System.Threading.Channels", "10.0.14", "MIT", "https://www.nuget.org/packages/System.Threading.Channels"),
        new ThirdPartyComponent("System.Diagnostics.DiagnosticSource", "10.0.14", "MIT", "https://www.nuget.org/packages/System.Diagnostics.DiagnosticSource"),
        new ThirdPartyComponent("System.Security.Cryptography.ProtectedData", "10.0.14", "MIT", "https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData"),
        new ThirdPartyComponent("Microsoft.Bcl.AsyncInterfaces", "10.0.14", "MIT", "https://www.nuget.org/packages/Microsoft.Bcl.AsyncInterfaces"),
        new ThirdPartyComponent("DocumentFormat.OpenXml.Framework", "3.5.1", "MIT", "https://www.nuget.org/packages/DocumentFormat.OpenXml.Framework", note: "dipendenza transitiva"),
        new ThirdPartyComponent("Microsoft.Bcl.HashCode", "6.0.0", "MIT", "https://www.nuget.org/packages/Microsoft.Bcl.HashCode", note: "dipendenza transitiva"),
        new ThirdPartyComponent("Microsoft.Extensions.Configuration.Abstractions", "10.0.3", "MIT", "https://www.nuget.org/packages/Microsoft.Extensions.Configuration.Abstractions", note: "dipendenza transitiva"),
        new ThirdPartyComponent("Microsoft.Extensions.DependencyInjection.Abstractions", "10.0.3", "MIT", "https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions", note: "dipendenza transitiva"),
        new ThirdPartyComponent("Microsoft.Extensions.Logging.Abstractions", "10.0.3", "MIT", "https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions", note: "dipendenza transitiva"),
        new ThirdPartyComponent("Microsoft.Extensions.Primitives", "10.0.3", "MIT", "https://www.nuget.org/packages/Microsoft.Extensions.Primitives", note: "dipendenza transitiva"),
        new ThirdPartyComponent("SQLite3MC.PCLRaw.provider", "2.6.1", "MIT", "https://www.nuget.org/packages/SQLite3MC.PCLRaw.provider", note: "dipendenza transitiva"),
        new ThirdPartyComponent("System.Buffers", "4.6.1", "MIT", "https://www.nuget.org/packages/System.Buffers", note: "dipendenza transitiva"),
        new ThirdPartyComponent("System.IO.Pipelines", "10.0.14", "MIT", "https://www.nuget.org/packages/System.IO.Pipelines", note: "dipendenza transitiva"),
        new ThirdPartyComponent("System.Memory", "4.6.3", "MIT", "https://www.nuget.org/packages/System.Memory", note: "dipendenza transitiva"),
        new ThirdPartyComponent("System.Numerics.Vectors", "4.6.1", "MIT", "https://www.nuget.org/packages/System.Numerics.Vectors", note: "dipendenza transitiva"),
        new ThirdPartyComponent("System.Runtime.CompilerServices.Unsafe", "6.1.2", "MIT", "https://www.nuget.org/packages/System.Runtime.CompilerServices.Unsafe", note: "dipendenza transitiva"),
        new ThirdPartyComponent("System.Text.Encodings.Web", "10.0.14", "MIT", "https://www.nuget.org/packages/System.Text.Encodings.Web", note: "dipendenza transitiva"),
        new ThirdPartyComponent("System.Threading.Tasks.Extensions", "4.6.3", "MIT", "https://www.nuget.org/packages/System.Threading.Tasks.Extensions", note: "dipendenza transitiva"),
        new ThirdPartyComponent("Microsoft.Office.Interop.Outlook", "15.0.4797.1004", "Nessuna licenza nel pacchetto", "https://www.nuget.org/packages/Microsoft.Office.Interop.Outlook",
            distributed: false, note: "Solo compilazione: tipi incorporati, DLL non distribuita; il pacchetto non dichiara una licenza"),
        new ThirdPartyComponent("Microsoft.Office.Interop.Word", "15.0.4797.1004", "Nessuna licenza nel pacchetto", "https://www.nuget.org/packages/Microsoft.Office.Interop.Word",
            distributed: false, note: "Solo compilazione: tipi incorporati, DLL non distribuita; il pacchetto non dichiara una licenza"),
        new ThirdPartyComponent("Microsoft.Windows.SDK.Contracts", "10.0.19041.1", "Licenza Microsoft Windows SDK", "https://aka.ms/WinSDKLicenseURL",
            distributed: false, note: "Solo compilazione: metadati delle API OCR e PDF di Windows, nessun file distribuito"),
        new ThirdPartyComponent("NSIS", "3", "zlib/libpng", "https://nsis.sourceforge.io/License", note: "programma di installazione"),
        // Build and test only.
        new ThirdPartyComponent("Microsoft.NETFramework.ReferenceAssemblies", "1.0.3", "MIT", "https://www.nuget.org/packages/Microsoft.NETFramework.ReferenceAssemblies", distributed: false),
        new ThirdPartyComponent("xunit", "2.9.3", "Apache-2.0", "https://www.nuget.org/packages/xunit", distributed: false),
        new ThirdPartyComponent("xunit.runner.visualstudio", "4.0.1", "Apache-2.0", "https://www.nuget.org/packages/xunit.runner.visualstudio", distributed: false),
        new ThirdPartyComponent("Microsoft.NET.Test.Sdk", "18.11.0", "MIT", "https://www.nuget.org/packages/Microsoft.NET.Test.Sdk", distributed: false),
        new ThirdPartyComponent("coverlet.collector", "10.1.0", "MIT", "https://www.nuget.org/packages/coverlet.collector", distributed: false)
    };
}
