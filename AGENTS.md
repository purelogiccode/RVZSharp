# AGENTS.md — RVZSharp

Guidance for coding agents and contributors working in this repository. It describes the
layout, the build/test/release workflow, the logging and telemetry contract, and the rules
that keep the library and CLI safe to publish.

## Project overview

RVZSharp is a pure managed .NET library and command-line tool for GameCube/Wii disc images:

- **Read:** RVZ, WIA, GCZ, CISO/WBI, WBFS, TGC, NFS and plain ISO.
- **Write:** RVZ (None, Zstd, Bzip2, LZMA1, LZMA2, optional PRNG-junk packing), WIA
  (None, PURGE, Bzip2, LZMA1, LZMA2) and GCZ (zlib deflate), plus CISO/WBFS/TGC writers.
- **Verify/extract:** Disc hashing, Wii hash-tree/TMD/H3 verification, and FST extraction.
- **Reference implementations:** `References/dolphin-master/` (C++) and
  `References/rvz-1.0.3/` (Go). Match their on-disk behaviour and error rules.

## Repository layout

| Path | Contents |
|---|---|
| `RVZSharp/` | The library. No logging, telemetry, HTTP or UI dependencies. |
| `RVZSharp.Cli/` | The CLI (`rvzsharp`): Serilog, ApplicationStats, bug reporting, update check. |
| `RVZSharp.Tests/` | Fast library tests (xUnit) on every target framework. |
| `RVZSharp.Cli.Tests/` | CLI parser, update-check and logging tests. |
| `RVZSharp.Slow.Tests/` | Real-file and differential tests against `DolphinTool.exe`. |
| `RVZSharp.Benchmarks/` | BenchmarkDotNet suites. |
| `docs/` | Jekyll site, GitHub Wiki source and user documentation. |
| `docfx/` | API reference configuration (DocFX) published under `/api/`. |
| `.github/workflows/` | CI, CodeQL, dependency review, Pages, Wiki and Release automation. |
| `References/` | Dolphin and Go rvz reference sources (read-only). |

## Prerequisites

- .NET SDK **10.0** (see `global.json`; it rolls forward to the latest major).
- The library targets `net8.0`, `net9.0` and `net10.0`; tests run on all three.
- Windows, Linux or macOS. The CLI ships framework-dependent single-file builds for
  `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`.
- Optional for slow tests: `DolphinTool.exe` at the repository root.

## Build and test

```powershell
dotnet restore CSharp_RVZSharp.sln
dotnet build CSharp_RVZSharp.sln -c Release
dotnet test CSharp_RVZSharp.sln -c Release            # fast suites, all frameworks
dotnet test RVZSharp.Slow.Tests -c Release            # real-file/differential suite
dotnet run --project RVZSharp.Benchmarks -c Release -- --filter '*'
dotnet pack RVZSharp/RVZSharp.csproj -c Release -o artifacts/packages
```

Rules:

- `TreatWarningsAsErrors` is enabled; analyzer warnings fail the build.
- Keep the library **Native AOT/trimming clean**. Do not introduce reflection-based JSON,
  dynamic code or unbounded `Activator` use.
- Add or update tests for every behavioural change. Prefer the synthetic builders in
  `RVZSharp.Tests/Helpers/` over checking in large fixtures.
- `InternalsVisibleTo` already exposes internals to the test projects; prefer `internal`
  for implementation helpers and keep the public package surface deliberate.
- Do not edit generated folders (`bin/`, `obj/`, `docfx/api/`, `docfx/_site/`, `docs/api/`).

## Public API and documentation

- Every public type and member must have XML documentation; documentation generation is
  enabled and missing comments are build warnings.
- The package validates API compatibility against the last published release
  (`EnablePackageValidation`, baseline `1.0.0`). Breaking changes require a documented
  suppression; prefer additive changes.
- Public API changes must update `docs/usage-library.md`, the DocFX API reference and,
  where relevant, `README.md`.
- Visibility convention: consumer-facing API is `public`; implementation details are
  `internal`; test-only helpers stay `internal` and rely on `InternalsVisibleTo`.

## Async conventions

- Every asynchronous method is named with the `Async` suffix (`WriteAsync`,
  `ReadAtAsync`, `CheckAsync`, `SendBugReportAsync`, ...).
- Library async methods accept a `CancellationToken` and must not block on async work.
- CLI fire-and-forget work (telemetry, update checks) must be awaited or bounded before
  the process exits; see `Program.Main`.

## Logging, telemetry and bug reports (CLI only)

The **library never logs and never performs I/O beyond the disc image**. All diagnostics
live in `RVZSharp.Cli` and use Serilog exclusively:

- `LogSetup.Initialize()` configures console (stderr), a daily rolling file under
  `%TEMP%/RVZSharp/logs`, and the `BugReportSink`.
- stdout is reserved for command results (JSON, hashes, image bytes). Write diagnostics
  to stderr or the logger.
- **Expected failures** (bad usage, unsupported input, missing files, I/O problems,
  cancellation, verification mismatches) are logged at **Information**. They must not be
  reported as bugs.
- **Warnings and above** are forwarded to the bug-report API by `BugReportSink`
  (minimum level `Warning`). Reserve warnings for genuine anomalies that need attention.
- Unhandled exceptions and unobserved task exceptions are captured at the top level,
  logged as Error/Fatal, and flushed before exit.
- The bug-report payload always contains these sections:

  ```text
  === Environment Details ===
  Date
  Application Name
  Application Version
  OS Version
  Architecture
  Bitness
  Windows / Linux / MacOSX Version
  Processor Count
  Base Directory
  Temp Path

  === Error Details ===
  Error message

  === Exception Details ===
  Type
  Message
  Source
  StackTrace
  ```

  `BugReportDetails.Build` is the single formatter; keep its tests green.

- Launch telemetry posts to `https://www.purelogiccode.com/ApplicationStats/stats` and is
  best-effort. The server rate-limits to one hit per hour per IP/application (HTTP 429 is
  expected and ignored).
- The update check queries the latest GitHub release at launch and prompts after the
  command finishes. Set `RVZSHARP_NO_UPDATE_CHECK=1` to disable it.
- Never block the command for telemetry or update checks for more than a few seconds.

## Security

- Follow `SECURITY.md` for vulnerability reporting and disclosure.
- Never commit real secrets, tokens or credentials. The ApplicationStats and bug-report
  keys are client-side publishable values; do not reuse them for privileged access.
- Treat every byte read from a disc image as hostile: bound allocations, validate offsets
  and sizes, and keep the extract path traversal defenses intact.
- CodeQL, dependency review, Dependabot security updates and secret scanning run on the
  repository; keep the workflows green.

## Documentation pipeline

- `docs/**/*.md` is the single source for both GitHub Pages (Jekyll) and the GitHub Wiki.
  `docs/README.md` becomes `Home.md`; `_Sidebar.md` is the wiki sidebar.
- Update `docs/_data/nav.yml` and `docs/_Sidebar.md` when adding a page.
- The DocFX API reference is generated from the library XML comments into `docfx/_site`
  and copied to `docs/api/` by `.github/workflows/pages.yml`.
- Do not edit wiki pages on GitHub directly; the workflow overwrites them.

## Release workflow

1. Bump `<Version>` in `Directory.Build.props`, `AssemblyVersion`/`FileVersion` in
   `RVZSharp/RVZSharp.csproj`, and update `WhatsNew.md`.
2. Add `docs/release-notes-<version>.md`.
3. Push a `v<version>` tag. `.github/workflows/release.yml` builds all RIDs, runs the
   smoke tests, packs the NuGet package, creates the GitHub Release with checksums and
   pushes stable versions to nuget.org when `NUGET_API_KEY` is configured.
4. Dry-run with `workflow_dispatch` and a version input; no release is created.

## Linux VM validation

The CLI is validated on the Hyper-V `LinuxMint` VM when available. Do not commit
machine-specific IP addresses or credentials. Use environment variables or a local,
git-ignored file (for example `AGENTS.local.md`) for host details, then:

```powershell
dotnet publish RVZSharp.Cli/RVZSharp.Cli.csproj -c Release -f net10.0 -r linux-x64 --self-contained false -p:PublishSingleFile=true -o artifacts/linux-x64
# copy artifacts/linux-x64/RVZSharp to the VM and run:
./RVZSharp --help
./RVZSharp header -i /path/to/sample.rvz
./RVZSharp verify -i /path/to/sample.rvz
```

Use `ssh`/`scp` (or `Posh-SSH`) with the host-specific details. Record the results in the
pull request; a green CI run does not replace the native Linux smoke test.

## Pull requests

- Keep changes focused; one behavioural change per PR.
- Run the fast suites before pushing; slow/differential tests when touching codecs or
  container parsing.
- Update docs and tests in the same PR.
- Do not merge a red PR. Dependabot PRs are handled by a maintainer; duplicates are closed
  in favour of the newest passing PR.
