# Getting started

## Prerequisites

- [.NET SDK 11](https://dotnet.microsoft.com/download) to build (C# 15). The library, CLI
  and test suite target **`net8.0`, `net9.0`, `net10.0` and `net11.0`**.
- A disc image to experiment with: a plain `.iso`, or a `.rvz`/`.wia`/`.gcz`/`.ciso`/
  `.wbfs`/`.tgc`/`.nfs` file.

> No game files are required for the test suite — tests generate synthetic discs.

## Install the CLI

Install the .NET tool (runs on .NET 8 or newer, picking the matching `net8.0`–`net11.0`
asset):

```bash
dotnet tool install -g RVZSharp.Cli
rvzsharp --help
```

On Windows, WinGet installs the release bundle and the .NET 11 runtime:

```powershell
winget install PureLogicCode.RVZSharp
```

Or download the platform archive from the
[GitHub Releases](https://github.com/purelogiccode/RVZSharp/releases) page:

| | |
|---|---|
| `rvzsharp_v<version>_win-x64.zip` | Windows 10/11, x64 |
| `rvzsharp_v<version>_win-arm64.zip` | Windows 11, arm64 |
| `rvzsharp_v<version>_linux-x64.tar.gz` | Linux, x64 |
| `rvzsharp_v<version>_linux-arm64.tar.gz` | Linux, arm64 |
| `rvzsharp_v<version>_osx-x64.zip` | macOS, Intel |
| `rvzsharp_v<version>_osx-arm64.dmg` | macOS, Apple Silicon |

Extract the archive (`unzip`/`Expand-Archive` for `.zip`, `tar -xzf` for `.tar.gz`, open
the `.dmg` and copy the executable on Apple Silicon), then run `RVZSharp`
(`RVZSharp.exe` on Windows). Each bundle holds the single-file executable plus `LICENSE`,
`README.md`, `WhatsNew.md` and `THIRD-PARTY-NOTICES.md`. The executables are
framework-dependent — the .NET runtime is not embedded — so install the
[.NET 11 Runtime](https://dotnet.microsoft.com/download/dotnet/11.0) first (the SDK
works too). Verify with `RVZSharp --help` (also prints the product version). Each
release page also carries `SHA256SUMS.txt` to verify a download. To build from source
instead, see [Building](#building) below.

## Install the library

The library package supports .NET 8 through .NET 11:

```bash
dotnet add package RVZSharp --version 1.3.0
```

## Repository layout

```
CSharp_RVZSharp.sln        solution file (fast test suite runs solution-wide)
Directory.Build.props       net8.0/net9.0/net10.0/net11.0, C# 15, warnings as errors
RVZSharp/                   the library (NuGet package RVZSharp)
RVZSharp.Cli/               the CLI (NuGet tool package RVZSharp.Cli)
RVZSharp.Tests/             fast unit + end-to-end tests (613 per framework)
RVZSharp.Cli.Tests/         CLI tests (106 per framework: parsing, extract paths, completions, update checker, logging)
RVZSharp.Slow.Tests/        real-file tests (276) — kept out of the solution; run explicitly
RVZSharp.Benchmarks/        BenchmarkDotNet throughput suite (net11.0, in the solution)
scripts/                    WinGet manifest generator
References/dolphin-master/  Dolphin source (C++) — format reference
References/rvz-1.0.3/       Go RVZ reader — cross-check reference
docs/                       the wiki (this documentation)
```

## Building

```bash
dotnet build CSharp_RVZSharp.sln -c Release
```

The build treats warnings as errors, so a clean build means zero warnings.

## Running the tests

```bash
dotnet test CSharp_RVZSharp.sln -c Release
```

Expected result: `Passed: 613` library tests and `Passed: 106` CLI tests on each of
`net8.0`, `net9.0`, `net10.0` and `net11.0`. The real-file suite is not part of the
solution and runs only when requested:

```bash
dotnet test RVZSharp.Slow.Tests -c Release   # 276 real-game tests, ~30 min when mounted
```

To run a single test class:

```bash
dotnet test CSharp_RVZSharp.sln -c Release --filter "FullyQualifiedName~RvzWriterTests"
```

## First commands

The CLI speaks the same command surface as Dolphin's `dolphin-tool` (plus the legacy
`info`/`decode` commands):

```bash
dotnet run --project RVZSharp.Cli -c Release -- header -i game.iso
dotnet run --project RVZSharp.Cli -c Release -- header -i game.rvz
dotnet run --project RVZSharp.Cli -c Release -- verify -i game.rvz -a sha1
dotnet run --project RVZSharp.Cli -c Release -- convert -i game.wia -o game.rvz -f rvz -c zstd -l 5 -b 131072
dotnet run --project RVZSharp.Cli -c Release -- convert -i game.iso -o game.wia -f wia -c lzma2 -l 5 -b 2097152
dotnet run --project RVZSharp.Cli -c Release -- convert -i game.rvz -o game.iso -f iso
```

`convert` accepts **any** supported input (plain ISO or any legacy format) and writes a
fully self-contained RVZ or WIA file (`-f iso` decodes back to a plain ISO). See the
[CLI reference](usage-cli.md) for all options.

## Notes for contributors

- Keep the solution file and project layout as-is: `RVZSharp.Tests` is the fast suite that
  ships in the solution, `RVZSharp.Slow.Tests` holds the long real-file tests and is
  intentionally left out of it.
- Keep the build at **zero warnings** (`TreatWarningsAsErrors` is on) and keep every
  public/internal type and member documented (XML doc comments — the package ships them).
- When changing format behaviour, cross-check against `References/dolphin-master/` (C++ is
  the source of truth for layout) and `References/rvz-1.0.3/` (Go reader) where possible;
  see [Testing](testing.md) for the validation strategy.
