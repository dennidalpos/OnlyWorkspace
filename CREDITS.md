# CREDITS: OnlyWorkspace

Third-party components used by OnlyWorkspace, with the license each one declares. Checked on 2026-11-12 against the `packages.lock.json` files, the nuspec of each restored NuGet package and the files the setup stages (`scripts/build-installer.ps1`: add-in output, `OnlyWorkspace.ConfigTool.exe`, `extractor\`). Project license: proprietary, all rights reserved, see [LICENSE](LICENSE). Notices and license texts for what is distributed: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Distributed with the setup

| Component | Version | License (package metadata) | Scope | Source |
| --- | --- | --- | --- | --- |
| `DocumentFormat.OpenXml` | 3.5.1 | MIT | direct | https://github.com/dotnet/Open-XML-SDK |
| `DocumentFormat.OpenXml.Framework` | 3.5.1 | MIT | transitive | https://github.com/dotnet/Open-XML-SDK |
| `ExcelDataReader` | 3.9.0 | MIT | direct | https://github.com/ExcelDataReader/ExcelDataReader |
| `Microsoft.Bcl.AsyncInterfaces` | 10.0.14 | MIT | direct | https://dot.net/ |
| `Microsoft.Bcl.HashCode` | 6.0.0 | MIT | transitive | https://dot.net/ |
| `Microsoft.Data.Sqlite.Core` | 10.0.14 | MIT | direct | https://docs.microsoft.com/dotnet/standard/data/sqlite/ |
| `Microsoft.Extensions.Configuration.Abstractions` | 10.0.3 | MIT | transitive | https://dot.net/ |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.3 | MIT | transitive | https://dot.net/ |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.3 | MIT | transitive | https://dot.net/ |
| `Microsoft.Extensions.Primitives` | 10.0.3 | MIT | transitive | https://dot.net/ |
| `Microsoft.Web.WebView2` | 1.0.4314.35 | LicenseRef: package LICENSE.txt (BSD-style) and NOTICE.txt | direct | https://aka.ms/webview |
| `MimeKitLite` | 4.18.1 | MIT | direct | https://www.mimekit.net/ |
| `Nager.PublicSuffix` | 3.8.0 | MIT | direct | https://github.com/nager/Nager.PublicSuffix |
| `OpenMcdf` | 3.4.0 | MPL-2.0 | direct | https://github.com/openmcdf/openmcdf |
| `PdfPig` | 0.1.16 | Apache-2.0 | direct | https://github.com/UglyToad/PdfPig |
| `Serilog` | 4.4.0 | Apache-2.0 | direct | https://serilog.net/ |
| `Serilog.Sinks.File` | 7.0.0 | Apache-2.0 | direct | https://github.com/serilog/serilog-sinks-file |
| `SQLite3MC.PCLRaw.bundle` | 2.6.1 | MIT | direct | https://utelle.github.io/SQLite3MultipleCiphers/ |
| `SQLite3MC.PCLRaw.lib` | 2.6.1 | MIT | direct | https://utelle.github.io/SQLite3MultipleCiphers/ |
| `SQLite3MC.PCLRaw.provider` | 2.6.1 | MIT | transitive | https://utelle.github.io/SQLite3MultipleCiphers/ |
| `SQLitePCLRaw.core` | 3.0.2 | Apache-2.0 | transitive | https://github.com/ericsink/SQLitePCL.raw |
| `System.Buffers` | 4.6.1 | MIT | transitive | https://dot.net/ |
| `System.Diagnostics.DiagnosticSource` | 10.0.14 | MIT | direct | https://dot.net/ |
| `System.IO.Pipelines` | 10.0.14 | MIT | transitive | https://dot.net/ |
| `System.Memory` | 4.6.3 | MIT | transitive | https://dot.net/ |
| `System.Numerics.Vectors` | 4.6.1 | MIT | transitive | https://dot.net/ |
| `System.Runtime.CompilerServices.Unsafe` | 6.1.2 | MIT | transitive | https://dot.net/ |
| `System.Text.Encodings.Web` | 10.0.14 | MIT | transitive | https://dot.net/ |
| `System.Text.Json` | 10.0.14 | MIT | direct | https://dot.net/ |
| `System.Threading.Channels` | 10.0.14 | MIT | direct | https://dot.net/ |
| `System.Threading.Tasks.Extensions` | 4.6.3 | MIT | transitive | https://dot.net/ |
| Public Suffix List | 2026-10-07 | MPL-2.0 | embedded in Core | https://publicsuffix.org/ |
| Adobe Core 14 AFM files | - | Adobe ReadMe terms (LicenseRef) | embedded in PdfPig | https://github.com/UglyToad/PdfPig |
| Adobe Glyph List, ITC Zapf Dingbats Glyph List | 2.0 | BSD-3-Clause (Adobe) | embedded in PdfPig | http://sourceforge.net/adobe/aglfn/ (URL in the list header) |
| NSIS runtime stub | 3.13 | Zlib | setup and uninstaller | https://nsis.sourceforge.io/License |

## Build and test only (not distributed)

| Component | Version | License (package metadata) | Use |
| --- | --- | --- | --- |
| `Microsoft.Office.Interop.Outlook` | 15.0.4797.1004 | none declared | types embedded at build (No-PIA); DLL not shipped |
| `Microsoft.Office.Interop.Word` | 15.0.4797.1004 | none declared | types embedded at build (No-PIA); DLL not shipped |
| `Microsoft.Windows.SDK.Contracts` | 10.0.28000.2957 | licenseUrl https://aka.ms/WinSDKLicenseURL | OCR and PDF API metadata at build |
| `Microsoft.NETFramework.ReferenceAssemblies` | 1.0.3 | licenseUrl https://github.com/Microsoft/dotnet/blob/master/LICENSE | build |
| `System.Security.Cryptography.ProtectedData` | 10.0.14 | MIT | net10 build and tests; net48 uses the .NET Framework DPAPI |
| `Microsoft.NET.Test.Sdk` | 18.11.0 | MIT | tests |
| `xunit` | 2.9.3 | Apache-2.0 | tests |
| `xunit.runner.visualstudio` | 4.0.1 | Apache-2.0 | tests |
| `coverlet.collector` | 10.1.0 | MIT | tests |

## Obligations and owner decisions

- OpenMcdf (MPL-2.0) is shipped unmodified as a separate DLL; its source location is stated in the notices. Weak copyleft: owner decision before redistribution.
- Apache-2.0 components (PdfPig, Serilog, Serilog.Sinks.File, SQLitePCLRaw.core) publish no NOTICE file in their repositories or packages; the license reference is in the notices.
- Microsoft.Web.WebView2 and the 10.0.x .NET packages ship their own third-party notices; both are reproduced in full in the notices appendices.
- The Office interop packages declare no license; the installer does not redistribute them.

## Limits

- Licenses are those declared in each restored package's nuspec; full license texts inside packages were compared only for WebView2 and the .NET notices.
- The OpenMcdf repository has no LICENSE file at its root; MPL-2.0 and its copyright come from the package metadata.
- Legal compatibility with the project license is a decision for the owner; this file is an inventory, not legal advice.
