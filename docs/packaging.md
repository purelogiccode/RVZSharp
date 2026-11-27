# Packaging & distribution

`RVZSharp` ships as two NuGet packages: the **library** (`RVZSharp`) and the **.NET tool**
(`RVZSharp.Cli`). Windows CLI releases are also published through **WinGet**. This page
documents what the packages contain, how to build and publish them, and the quality gates.

## Package facts

| | |
|---|---|
| Package IDs | `RVZSharp` (library), `RVZSharp.Cli` (.NET tool) |
| Version | `1.3.0` (SemVer; bumped per release) |
| Target frameworks | `net8.0`, `net9.0`, `net10.0`, `net11.0` (C# 15) |
| License | GPL-2.0-or-later (`PackageLicenseExpression`) |
| Dependencies | library: `LZMA-SDK`, `SharpZipLib`, `ZstdSharp.Port`; CLI: `Serilog`, `Serilog.Sinks.Console`, `Serilog.Sinks.File` (all pure managed) |
| Symbols | embedded portable PDBs inside the assemblies (no `.snupkg`) |
| SBOM | library package only: SPDX 2.2 manifest at `_manifest/spdx_2.2/manifest.spdx.json` (Release packs) |
| Reproducible | deterministic builds (`ContinuousIntegrationBuild` for release packs) |

## What's inside the packages

```
RVZSharp.<version>.nupkg
  lib/net8.0/RVZSharp.dll          assembly per target framework
  lib/net8.0/RVZSharp.xml          XML API documentation (IntelliSense)
  lib/net9.0/…  lib/net10.0/…  lib/net11.0/…
  README.md  LICENSE  THIRD-PARTY-NOTICES.md  icon.png
  _manifest/spdx_2.2/…             SPDX 2.2 SBOM (+ .sha256)

RVZSharp.Cli.<version>.nupkg
  tools/net8.0/any/…               .NET tool payload per target framework
  tools/net9.0/any/…  tools/net10.0/any/…  tools/net11.0/any/…
  tools/net8.0/any/DotnetToolSettings.xml   command name: rvzsharp
  README.md  LICENSE  THIRD-PARTY-NOTICES.md  icon.png
```

## Building the packages

```bash
dotnet pack RVZSharp/RVZSharp.csproj -c Release
dotnet pack RVZSharp.Cli/RVZSharp.Cli.csproj -c Release
# output: RVZSharp/bin/Release/RVZSharp.<version>.nupkg
#         RVZSharp.Cli/bin/Release/RVZSharp.Cli.<version>.nupkg
```

For a deterministic release build (reproducible SourceLink paths):

```bash
dotnet pack RVZSharp/RVZSharp.csproj -c Release -p:ContinuousIntegrationBuild=true
dotnet pack RVZSharp.Cli/RVZSharp.Cli.csproj -c Release -p:ContinuousIntegrationBuild=true
```

## Quality gates

1. **Zero warnings** — `TreatWarningsAsErrors` is on; the pack fails on any analyzer
   warning.
2. **Package validation** — `EnablePackageValidation` checks at pack time that the
   `net8.0`/`net9.0`/`net10.0`/`net11.0` assets are compatible with the package's supported
   frameworks, and `PackageValidationBaselineVersion` (currently `1.0.0`) diffs the public
   API against the last published release (API-compat analysis). A breaking change fails
   `dotnet pack`; intentional breaks must be suppressed in a `CompatibilitySuppressions.xml`
   with a justification.
3. **Tests** — `dotnet test CSharp_RVZSharp.sln -c Release` runs the fast library suite
   (613 tests per framework) and the CLI suite (106 tests) on `net8.0`, `net9.0`, `net10.0`
   and `net11.0`; the real-file slow suite (`dotnet test RVZSharp.Slow.Tests -c Release`)
   runs on machines with the games mounted.
4. **Consumer smoke test** — before publishing, a fresh project consuming only the nupkg
   (from a local feed) must compile and run on .NET 8, 9, 10 and 11, converting and decoding
   a disc image byte-exactly. The .NET tool package is installed from the local feed and
   `rvzsharp --help` must succeed. This catches packaging mistakes (missing files, wrong
   dependency graph) that unit tests cannot.
5. **Native AOT / trimming** — the library sets `IsAotCompatible`/`IsTrimmable` and the CLI
   sets `EnableAotAnalyzer`/`EnableTrimAnalyzer`, so trim/AOT warnings fail the build on all
   four frameworks. Release smoke test: `dotnet publish RVZSharp.Cli -r win-x64
   -p:PublishTrimmed=true` (and `-p:PublishAot=true`) must produce warning-free binaries
   that round-trip a disc image byte-exactly.
6. **CI** - `.github/workflows/ci.yml` builds and runs the fast suite on all four
   frameworks (coverage artifact), repeats the `net11.0` build/tests on Windows and macOS,
   builds the DocFX API reference, packs both packages with API validation + SBOM (library)
   and smoke-tests the CLI tool package, and publishes the framework-dependent single-file
   CLI (win-x64) that is smoke-tested before upload.
   `.github/workflows/codeql.yml` runs CodeQL `security-extended` on every push/PR and
   weekly; `.github/workflows/dependency-review.yml` blocks vulnerable dependency changes.
   `.github/workflows/release.yml` builds the release bundles (below),
   `.github/workflows/wiki.yml` mirrors `docs/` to the wiki and
   `.github/workflows/pages.yml` deploys `docs/` plus the generated `/api/` reference to
   GitHub Pages. Dependabot (`.github/dependabot.yml`) keeps NuGet and GitHub Actions
   dependencies current; secret scanning and push protection are enabled on the repository.

## Release process

Releases are built by `.github/workflows/release.yml`. Push a tag `v<version>`
(`v1.1.0`, `v1.3.0-beta.1`, … — valid SemVer after the leading `v`):

1. **CLI bundles** — the framework-dependent single-file CLI is published for six
   runtimes, smoke-tested on a native runner (`--help` must exit 0) and packaged, each
   archive holding the executable plus `LICENSE`, `README.md`, `WhatsNew.md` and
   `THIRD-PARTY-NOTICES.md`:

   | Asset | Runner |
   |---|---|
   | `rvzsharp_v<version>_win-x64.zip` | Windows Server 2025 (x64) |
   | `rvzsharp_v<version>_win-arm64.zip` | Windows 11 (arm64) |
   | `rvzsharp_v<version>_linux-x64.tar.gz` | Ubuntu 24.04 (x64) |
   | `rvzsharp_v<version>_linux-arm64.tar.gz` | Ubuntu 24.04 (arm64) |
   | `rvzsharp_v<version>_osx-x64.zip` | macOS 15 (Intel) |
   | `rvzsharp_v<version>_osx-arm64.dmg` | macOS 26 (Apple Silicon) |

   The bundles are framework-dependent: the .NET 11 runtime is **not** embedded, so
   target machines install it (see
   [Getting started](getting-started.md#install-the-cli)). Linux uses `tar.gz` (which
   preserves the executable bit), macOS Intel uses `zip(1)` for the same reason, and
   Apple Silicon ships as a compressed UDZO `.dmg`.
2. **NuGet packages** — `dotnet pack` builds `RVZSharp.<version>.nupkg` with
   API-compat validation and the embedded SBOM, plus
   `RVZSharp.Cli.<version>.nupkg`, the multi-target .NET tool package (embedded PDBs,
   so no symbols package).
3. **GitHub Release** — created with the six archives, `SHA256SUMS.txt`, both packages and
   `rvzsharp_v<version>_winget.zip` containing the generated WinGet manifests. The notes
   come from `docs/release-notes-<version>.md` when that file exists
   (`release-notes-1.0.0.md` shows the format); otherwise a fallback body is used. Tags
   containing `-` are marked as pre-releases.
4. **nuget.org** — stable versions of both packages are pushed automatically when the
   `NUGET_API_KEY` repository secret is set (nuget.org → API Keys → repository secret);
   without it the workflow logs a warning and the GitHub Release remains the only
   published artifact.
5. **WinGet** — a Windows job generates the manifests from the `win-x64`/`win-arm64`
   zips and attaches the bundle to the release. When the `WINGET_TOKEN` secret is set,
   `wingetcreate submit` opens a pull request against `microsoft/winget-pkgs`; without
   it the manifests stay attached for manual submission.

Run the workflow manually (`workflow_dispatch` with a `version` input) for a dry
run: everything builds and uploads as run artifacts for inspection, but no GitHub
Release is created and nothing is pushed to nuget.org or WinGet.

## Wiki and Pages automation

`docs/` is the single source of the documentation and is published twice by CI:

- **GitHub Pages** - `.github/workflows/pages.yml` builds `docs/` with Jekyll on
  every `master` push touching `docs/` (plus manual runs), after generating the DocFX
  API reference from the library XML comments with `dotnet docfx docfx/docfx.json`
  and copying it to `docs/api/`. `_config.yml` selects
  the Cayman theme, `README.md` is the home page (`permalink: /`),
  `_data/nav.yml` + `_layouts/default.html` render the side menu, and
  internal links use the `.md` form (`[CLI](usage-cli.md)`), which Jekyll converts
  to `.html`.
- **GitHub Wiki** — `.github/workflows/wiki.yml` mirrors `docs/` into the
  repository wiki on the same trigger: `README.md` → `Home.md` (front matter
  stripped), `format/*.md` → `format/…` subpages, `_Sidebar.md` kept as-is (the wiki
  sidebar), Jekyll-only files (`_config.yml`, `_data/`, `_layouts/`) dropped, and relative
  `page.md` links rewritten to the extensionless wiki form (`[CLI](usage-cli)`;
  anchors and absolute URLs untouched). The wiki is fully generated — edit
  `docs/`, never the wiki pages (manual edits are overwritten on the next sync).

One-time setup: enable Wikis (Settings → General → Features) and save a personal
access token with the `repo` scope (or a fine-grained token with Contents
read/write on this repository) as the `WIKI_TOKEN` repository secret; without it
the workflow fails with setup instructions.

## Publishing to nuget.org

Pushing a stable `v<version>` tag does this automatically (see [Release
process](#release-process) — the `NUGET_API_KEY` secret must be set). The manual
equivalent is:

```bash
# 1. Build both packages.
dotnet pack RVZSharp/RVZSharp.csproj -c Release
dotnet pack RVZSharp.Cli/RVZSharp.Cli.csproj -c Release

# 2. Push both (the API key comes from nuget.org → API Keys).
dotnet nuget push RVZSharp/bin/Release/RVZSharp.1.3.0.nupkg \
    --source https://api.nuget.org/v3/index.json \
    --api-key <NUGET_API_KEY>
dotnet nuget push RVZSharp.Cli/bin/Release/RVZSharp.Cli.1.3.0.nupkg \
    --source https://api.nuget.org/v3/index.json \
    --api-key <NUGET_API_KEY>
```

After publishing, verify both package pages: readme rendering, license, dependencies, the
`lib/` folders for all four target frameworks and the embedded `_manifest/spdx_2.2` SBOM
on the library page, and that `dotnet tool install -g RVZSharp.Cli` resolves the tool
package.

## WinGet

The Windows CLI is published to WinGet as `PureLogicCode.RVZSharp`. The manifests are
generated from the two Windows release zips by
[`scripts/Generate-WingetManifests.ps1`](https://github.com/purelogiccode/RVZSharp/blob/master/scripts/Generate-WingetManifests.ps1),
which fills in the SHA-256 hashes and points the installers at the GitHub release assets. The
portable installer command alias is `rvzsharp`, and the manifest depends on
`Microsoft.DotNet.Runtime.11` because the release bundles are framework-dependent.

Generate and inspect the manifests locally with the release zips:

```powershell
./scripts/Generate-WingetManifests.ps1 `
    -Version 1.3.0 -ReleaseTag v1.3.0 `
    -InputDirectory artifacts/release `
    -OutputDirectory artifacts/winget-manifests

winget validate --manifest artifacts/winget-manifests
```

To submit manually, download the `rvzsharp_v<version>_winget.zip` asset from the GitHub
release, unzip it and run:

```powershell
wingetcreate submit --prtitle "Update PureLogicCode.RVZSharp to 1.3.0" `
    --token <WINGET_TOKEN> --no-open artifacts/winget-manifests
```

The release workflow does this automatically when the `WINGET_TOKEN` repository secret is
configured. The token needs the `public_repo` scope so WinGetCreate can fork
`microsoft/winget-pkgs` and open the version pull request.

## Versioning policy

- The package version follows SemVer. The public API is diffed against the last published
  release at pack time (`PackageValidationBaselineVersion`), so breaking changes must be
  deliberate and suppressed with a justification.
- Public API changes are intentional and reviewed: the reader/writer surface
  (`Blob.Open`, `RvzReader`, `RvzWriter.Write`) is stable; format-specific structs
  (`WiaDisc`, `WiaPartEntry`, …) may grow fields.

## Local development feed

To test the package without publishing:

```xml
<!-- nuget.config -->
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="D:\path\to\RVZSharp\bin\Release" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

```bash
dotnet add package RVZSharp --version 1.3.0
```

## The CLI

`RVZSharp.Cli` is a reference implementation for the library, a smoke-test surface, and a
multi-target .NET tool package (`net8.0` through `net11.0`). Install it from NuGet or
WinGet:

```bash
dotnet tool install -g RVZSharp.Cli
rvzsharp --help

# Windows:
winget install PureLogicCode.RVZSharp
```

From a source checkout:

```bash
dotnet build CSharp_RVZSharp.sln -c Release
dotnet run --project RVZSharp.Cli -c Release -- header -i game.rvz

# Release bundles (all six RIDs), framework-dependent single-file:
dotnet publish RVZSharp.Cli -c Release -f net11.0 -r win-x64 --self-contained false \
    -p:PublishSingleFile=true
```

Each GitHub Release (see [Release process](#release-process)) ships
`rvzsharp_v<version>_<rid>.{zip|tar.gz|dmg}` bundles (`win-x64`, `win-arm64`,
`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`) — extract the archive, install the
[.NET 11 Runtime](https://dotnet.microsoft.com/download/dotnet/11.0) and run `RVZSharp`
(`RVZSharp.exe` on Windows); see [Getting started](getting-started.md#install-the-cli).
The .NET tool package does not need a specific runtime release: it selects the matching
`net8.0`–`net11.0` asset from the installed runtime.
