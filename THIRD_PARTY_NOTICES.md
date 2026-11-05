# Third-party inventory — preliminary discovery

No Lunar product binary or installer is being distributed. This is an initial development inventory, not a complete redistribution notice bundle or license approval. All actual release payloads/native components need exact license text and notices before shipping.

BE-01 development/test inventory (2026-10-27): `xunit.v3.mtp-off` 4.0.2 and
`xunit.runner.visualstudio` 4.0.1 use Apache-2.0; `Microsoft.NET.Test.Sdk` 18.10.0
uses MIT. All are test-only with `PrivateAssets=all`, not production dependencies.
Exact direct/transitive versions, package hashes, source and nuspec license metadata
are in `packages.json` (kept locally, not in the repository) and the
six test lockfiles. [NuGet xUnit variant](https://www.nuget.org/packages/xunit.v3.mtp-off/4.0.2),
[adapter](https://www.nuget.org/packages/xunit.runner.visualstudio/4.0.1),
[Test SDK](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.10.0).
This development inventory does not clear the historical converter native payload
for redistribution or replace the release notice bundle.

BE-04 product dependency (2026-11-03): `Microsoft.Extensions.DependencyInjection`
10.0.13 (MIT; transitive `Microsoft.Extensions.DependencyInjection.Abstractions`
10.0.13) in `Lunar.Desktop` only, approved in ADR-0006. Pinned in
`Directory.Packages.props` and the Desktop lockfile; managed, no native code.
[NuGet](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection/10.0.13).
Not yet part of any shipped payload; the release notice bundle remains BE-28/29 work.

BE-07 product dependencies (2026-11-03): `Serilog` 4.3.1 and `Serilog.Sinks.File` 7.0.0, both
Apache-2.0 (nuspec license expressions), managed only, in `Lunar.Infrastructure` (and so in the
Desktop payload), approved in ADR-0009. Serilog has no further dependencies on net10.0;
Serilog.Sinks.File requires Serilog 4.2.0 or newer. Pinned in `Directory.Packages.props` and the
lockfiles. [Serilog](https://www.nuget.org/packages/Serilog/4.3.1),
[Serilog.Sinks.File](https://www.nuget.org/packages/Serilog.Sinks.File/7.0.0). Apache-2.0 license
text and notices must ship with the release bundle (BE-28/29).

| Observed component | Version | Purpose / source |
|---|---|---|
| .NET SDK | 10.0.402 | Portable development toolchain, ignored `.tools/`; official provenance recorded locally |
| Microsoft.NETCore.App / Microsoft.WindowsDesktop.App | 10.0.13 | Runtime/framework shipped within local SDK; [runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT), [WPF license](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT) |
| Microsoft.Windows.SDK.NET.Ref | 10.0.19041.57 | SDK-managed targeting/reference pack restored from NuGet; package's Microsoft license URL: [Windows SDK terms](https://aka.ms/WinSDKLicenseURL). Nuspec requires license acceptance; do not classify entire pack as MIT based on CsWinRT alone |
| Windows native APIs | Host build 26300.9711 | OS-provided Core Audio, WLAN, display/DPI, radios and GSMTC; no copied native binaries |

Continuation on 2026-10-19: a disposable converter harness added SkiaSharp / SkiaSharp.NativeAssets.Win32 4.153.1, DocumentFormat.OpenXml / Framework 3.5.1 and System.IO.Packaging 10.0.2, all with MIT metadata at package level. [Exact versions/hashes](docs/evidence/sprint-0/converter-20261019/packages.json), [signature verification](docs/evidence/sprint-0/converter-20261019/package-signatures.log), [native package license](docs/evidence/sprint-0/converter-20261019/SkiaSharp-4.153.1-LICENSE.txt) and [aggregate third-party notices](docs/evidence/sprint-0/converter-20261019/SkiaSharp-4.153.1-THIRD-PARTY-NOTICES.txt) are preserved.

The native aggregate contains varied/alternative terms including LGPL/eCos sections. Applicability to the exact tested native binary and resulting obligations is not fully established; do not label the complete payload MIT-only. Redistribution remains on hold pending component-specific review. This inventory is not a native SBOM or final legal clearance.

No LibreOffice binary or installer tool was installed. Word already present on the host was investigated only as a reference; export timed out and it is not a Lunar dependency or proof of license entitlement. All generated fixtures remain local ignored artifacts. Candidate decisions and obligations: [DEPENDENCY_REVIEW.md](docs/DEPENDENCY_REVIEW.md). No Lunar source license was invented or selected.

BE-22 product dependency (2026-11-05): `SkiaSharp` 4.153.1 (MIT in the nuspec, Microsoft, signed by Microsoft Corporation and countersigned by NuGet.org; verified with `dotnet nuget verify`) in `Lunar.Conversion` (so also in the Desktop build output), for PNG/JPEG decoding and encoding; version approved in ADR-0003. Its dependencies are the native packages `SkiaSharp.NativeAssets.Win32` 4.153.1 (win-x86, win-x64, win-arm64 `libSkiaSharp.dll`) and, for the portable `net10.0` projects only (`Lunar.Conversion` and its tests, not Desktop), `SkiaSharp.NativeAssets.macOS` 4.153.1, which is unused on Windows. Exact hashes are in the lockfiles; the managed and Win32 hashes are identical to the ones recorded in the converter discovery. The note above about the native aggregate notice (mixed terms including LGPL/eCos sections) still applies in full: the native payload is part of the development build output now, but **no installer or release bundle exists, and shipping the native payload stays on hold** until the binary-specific license review required by ADR-0003 is done (release work, BE-28/29). [NuGet](https://www.nuget.org/packages/SkiaSharp/4.153.1).

BE-23 product dependency (2026-11-05): `DocumentFormat.OpenXml` 3.5.1 (MIT in the nuspec, Microsoft, countersigned by NuGet.org; verified with `dotnet nuget verify`) in `Lunar.Conversion` (so also in the Desktop build output), to write the Word documents that hold a picture; version approved in ADR-0003. Its dependencies are `DocumentFormat.OpenXml.Framework` 3.5.1 and `System.IO.Packaging` 10.0.2 (both MIT, managed only, no native code). Hashes in the lockfiles are identical to the ones recorded in the converter discovery. The package does not render documents. [NuGet](https://www.nuget.org/packages/DocumentFormat.OpenXml/3.5.1).

BE-25 optional external program (2026-11-05): LibreOffice is **not a Lunar dependency in the package sense and is not bundled, downloaded, installed or redistributed**. If the user has installed it themselves, Lunar can start the user's own soffice.exe (found only under Program Files, version 7 or newer) to turn a Word document into a PDF in a private working folder with a fresh profile; no LibreOffice file is copied into Lunar. LibreOffice is licensed by The Document Foundation under the MPL 2.0 (with parts under other licenses); because Lunar only runs the user's installed copy as a separate program, no LibreOffice license obligation passes to Lunar's own files, but this has had no legal review. The routes are switched off in this version (ADR-0005) until their fidelity is checked on a real LibreOffice.

FE-27 brand asset (2026-11-05): `src/Lunar.Desktop/Resources/ClaudeBrandmark.jpg` (800x172 JPEG, the "Claude" wordmark with its starburst) is shown unmodified on the Usage page and is compiled into `Lunar.Desktop` as a WPF resource. It was supplied by the project owner; no download page, license or written permission from Anthropic has been recorded for it. "Claude" and its logo are Anthropic's trademarks (see [Anthropic's trademark guidelines](https://www.anthropic.com/legal/trademark-guidelines)); Lunar claims no rights in them and the file must not be shipped in a release until permission or a confirmed official source is recorded here (release gate, Sprint 7). The Claude colors used on that page are plain color values (cream #FAF9F5, dark #141413, orange #D97757, light gray #E8E6DC).

## BE-28 dependency inventory (2026-11-05)

Every package any project resolves, from the lock files (exact versions and content hashes are in each `packages.lock.json`; restore runs in locked mode). Licenses are the SPDX expressions in each package's own nuspec. Package signatures were re-verified on 2026-11-05 with `dotnet nuget verify` (exit 0 for all nine product packages; Serilog and Serilog.Sinks.File carry only the nuget.org repository signature, the others also an author signature). `dotnet list package --vulnerable --include-transitive` reports none. The machine-checked copy of this list is `SecurityGovernanceTests` (a package, version or scope not in it fails the build).

| Package | Version | License | Scope | Purpose / notes |
|---|---|---|---|---|
| DocumentFormat.OpenXml, DocumentFormat.OpenXml.Framework | 3.5.1 | MIT | Product (Conversion) | create Word documents that embed pictures; never reads user documents |
| Microsoft.Extensions.DependencyInjection, .Abstractions | 10.0.13 | MIT | Product (Desktop) | composition root |
| Serilog, Serilog.Sinks.File | 4.3.1, 7.0.0 | Apache-2.0 | Product (Infrastructure) | bounded local log files; the Apache-2.0 text and NOTICE must ship in the release bundle |
| SkiaSharp | 4.153.1 | MIT | Product (Conversion) | picture decode/encode and PDF writing |
| SkiaSharp.NativeAssets.Win32 | 4.153.1 | MIT (package) | Product (Conversion) | native Windows library; **the aggregate has mixed terms including LGPL/eCos sections, so shipping it stays on hold (ADR-0003)** |
| SkiaSharp.NativeAssets.macOS | 4.153.1 | MIT (package) | restored with SkiaSharp | never part of a Windows payload; excluded from any release bundle |
| System.IO.Packaging | 10.0.2 | MIT | Product (transitive of OpenXml) | package handling |
| Microsoft.NET.Test.Sdk, Microsoft.CodeCoverage, Microsoft.TestPlatform.ObjectModel, .TestHost, Microsoft.Bcl.AsyncInterfaces 6.0.0, Microsoft.Win32.Registry 5.0.0, System.Security.AccessControl 6.0.1 | 18.10.0 (unless stated) | MIT | Test only | test host and its dependencies |
| xunit.v3.mtp-off, xunit.v3.core.mtp-off, .assert, .common, .extensibility.core, .runner.common, .runner.inproc.console | 4.0.2 | Apache-2.0 | Test only | test framework |
| xunit.runner.visualstudio, xunit.analyzers | 4.0.1, 2.2.0 | Apache-2.0 | Test only | test discovery and analyzers |

Release obligations still open (BE-29/BE-30): ship license texts and notices for every Product row; the SkiaSharp native payload, the Claude brandmark and the .NET runtime redistribution terms must be cleared before any installer is published. Newer versions exist for SkiaSharp (4.154.0), Serilog (4.4.0), Microsoft.Extensions.DependencyInjection (10.0.14) and the test SDK (18.11.0); none is needed for a known advisory and none was applied (an update is a reviewed change to the table above).

BE-34 (2026-11-05): no new package. The usage feature reads the user's own Claude Code sign-in file with the BCL only (ADR-0015); no Anthropic code or client identifier is copied or used.
