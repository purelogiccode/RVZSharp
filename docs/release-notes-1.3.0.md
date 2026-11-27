# RVZSharp 1.3.0 Release Notes

RVZSharp 1.3.0 is a **distribution and compatibility release**. The public API stays
compatible with 1.0.0.

> **Companion GUI:** [RVZStudio](https://github.com/purelogiccode/RVZStudio) is the desktop
> GUI for RVZSharp, maintained alongside this repository.

## Highlights

- **.NET 8 through .NET 11** — the library, CLI and tests now target `net8.0`, `net9.0`,
  `net10.0` and `net11.0` with **C# 15**; the benchmarks target `net11.0`.
- **Two NuGet packages** — the library stays `RVZSharp`; the CLI is now its own
  multi-target .NET tool package, `RVZSharp.Cli`, installed with
  `dotnet tool install -g RVZSharp.Cli` and invoked as `rvzsharp`.
- **WinGet** — the Windows CLI is published as `PureLogicCode.RVZSharp`:
  `winget install PureLogicCode.RVZSharp`.
- **Quoted paths fixed** — literal surrounding single or double quotes are stripped from
  CLI arguments, so scripts that pass `"C:\path\game.rvz"` as the argument text work.
  File-open errors now include the path.
- **Dependencies** — coverlet.collector 10.1.1, Meziantou.Analyzer 3.0.342,
  Microsoft.NET.Test.Sdk 18.12.0 (includes the CVE-2026-105544 fix),
  Microsoft.SourceLink.GitHub 10.0.404 and ZstdSharp.Port 0.9.1.

## Install

### Library

```bash
dotnet add package RVZSharp --version 1.3.0
```

### CLI

```bash
dotnet tool install -g RVZSharp.Cli --version 1.3.0
rvzsharp --help
```

On Windows, WinGet installs the portable bundle and its .NET 11 runtime dependency:

```powershell
winget install PureLogicCode.RVZSharp
```

### CLI downloads (1.3.0 bundles)

The bundles are **framework-dependent single-file** executables: install the
[.NET 11 Runtime](https://dotnet.microsoft.com/download/dotnet/11.0) first. Linux ships
as `.tar.gz`, Apple Silicon macOS as `.dmg`, and Windows/macOS Intel as `.zip`.

| Asset | Platform |
|---|---|
| `rvzsharp_v1.3.0_win-x64.zip` | Windows 10/11, x64 |
| `rvzsharp_v1.3.0_win-arm64.zip` | Windows 11, arm64 |
| `rvzsharp_v1.3.0_linux-x64.tar.gz` | Linux, x64 |
| `rvzsharp_v1.3.0_linux-arm64.tar.gz` | Linux, arm64 |
| `rvzsharp_v1.3.0_osx-x64.zip` | macOS, Intel |
| `rvzsharp_v1.3.0_osx-arm64.dmg` | macOS, Apple Silicon |

Each archive contains `RVZSharp` (`RVZSharp.exe` on Windows), `LICENSE`, `README.md`,
`WhatsNew.md` and `THIRD-PARTY-NOTICES.md`. Extract Linux bundles with
`tar -xzf rvzsharp_v1.3.0_linux-x64.tar.gz` (or `linux-arm64`), and open the Apple Silicon
`.dmg` to copy `RVZSharp`.

## Verification

- Clean Release build with zero warnings on all four target frameworks.
- **613 library tests** and **106 CLI tests** on each of `net8.0`, `net9.0`, `net10.0`
  and `net11.0`; **301 slow/real-file tests** on each framework.
- Package validation against the 1.0.0 baseline and an embedded SPDX 2.2 SBOM in the
  library package.
- DocFX API reference builds with zero warnings.
- The packed `.NET tool` is installed from a local feed and `rvzsharp --help` is
  smoke-tested.

## NuGet

- **RVZSharp 1.3.0** — `net8.0`, `net9.0`, `net10.0`, `net11.0`; XML docs and embedded PDBs.
- **RVZSharp.Cli 1.3.0** — multi-target .NET tool (`net8.0` through `net11.0`), command
  `rvzsharp`.
