# Third-party inventory — preliminary discovery

No Lunar product binary or installer is being distributed. This is an initial development inventory, not a complete redistribution notice bundle or license approval. All actual release payloads/native components need exact license text and notices before shipping.

BE-01 development/test inventory (2026-10-27): `xunit.v3.mtp-off` 4.0.2 and
`xunit.runner.visualstudio` 4.0.1 use Apache-2.0; `Microsoft.NET.Test.Sdk` 18.10.0
uses MIT. All are test-only with `PrivateAssets=all`, not production dependencies.
Exact direct/transitive versions, package hashes, source and nuspec license metadata
are in [packages.json](docs/evidence/sprint-1/be-01-20261027/packages.json) and the
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
| .NET SDK | 10.0.402 | Portable development toolchain, ignored `.tools/`; [official provenance](docs/evidence/sprint-0/sdk-provenance.json) |
| Microsoft.NETCore.App / Microsoft.WindowsDesktop.App | 10.0.13 | Runtime/framework shipped within local SDK; [runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT), [WPF license](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT) |
| Microsoft.Windows.SDK.NET.Ref | 10.0.19041.57 | SDK-managed targeting/reference pack restored from NuGet; package's Microsoft license URL: [Windows SDK terms](https://aka.ms/WinSDKLicenseURL). Nuspec requires license acceptance; do not classify entire pack as MIT based on CsWinRT alone |
| Windows native APIs | Host build 26300.9711 | OS-provided Core Audio, WLAN, display/DPI, radios and GSMTC; no copied native binaries |

Continuation on 2026-10-19: a disposable converter harness added SkiaSharp / SkiaSharp.NativeAssets.Win32 4.153.1, DocumentFormat.OpenXml / Framework 3.5.1 and System.IO.Packaging 10.0.2, all with MIT metadata at package level. [Exact versions/hashes](docs/evidence/sprint-0/converter-20261019/packages.json), [signature verification](docs/evidence/sprint-0/converter-20261019/package-signatures.log), [native package license](docs/evidence/sprint-0/converter-20261019/SkiaSharp-4.153.1-LICENSE.txt) and [aggregate third-party notices](docs/evidence/sprint-0/converter-20261019/SkiaSharp-4.153.1-THIRD-PARTY-NOTICES.txt) are preserved.

The native aggregate contains varied/alternative terms including LGPL/eCos sections. Applicability to the exact tested native binary and resulting obligations is not fully established; do not label the complete payload MIT-only. Redistribution remains on hold pending component-specific review. This inventory is not a native SBOM or final legal clearance.

No LibreOffice binary or installer tool was installed. Word already present on the host was investigated only as a reference; export timed out and it is not a Lunar dependency or proof of license entitlement. All generated fixtures remain local ignored artifacts. Candidate decisions and obligations: [DEPENDENCY_REVIEW.md](docs/DEPENDENCY_REVIEW.md). No Lunar source license was invented or selected.
