# Getting started

## Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download) (or newer; the projects target
  `net10.0`).
- A disc image to experiment with: a plain `.iso`, or a `.rvz`/`.wia`/`.gcz`/`.ciso`/
  `.wbfs`/`.tgc`/`.nfs` file.

> No game files are required for the test suite — tests generate synthetic discs.

## Repository layout

```
CSharp_RVZSharp.slnx        XML solution file (do NOT rename to .sln)
Directory.Build.props       net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors
src/RVZSharp/               the library
src/RVZSharp.Cli/           the info/decode/convert tool
tests/RVZSharp.Tests/       unit + end-to-end tests (221)
References/dolphin-master/  Dolphin source (C++) — format reference
References/rvz-1.0.3/       Go RVZ reader — cross-check reference
docs/                       the wiki (this documentation)
```

## Building

```bash
dotnet build CSharp_RVZSharp.slnx -c Release
```

The build treats warnings as errors, so a clean build means zero warnings.

## Running the tests

```bash
dotnet test CSharp_RVZSharp.slnx -c Release
```

Expected result: `Passed: 221, Failed: 0`.

To run a single test class:

```bash
dotnet test CSharp_RVZSharp.slnx -c Release --filter "FullyQualifiedName~RvzWriterTests"
```

## First commands

Inspect a disc image (format is auto-detected):

```bash
dotnet run --project src/RVZSharp.Cli -c Release -- info game.iso
dotnet run --project src/RVZSharp.Cli -c Release -- info game.rvz
```

Decode any format to a plain ISO:

```bash
dotnet run --project src/RVZSharp.Cli -c Release -- decode game.gcz game.iso
dotnet run --project src/RVZSharp.Cli -c Release -- decode game.rvz game.iso --sha1 <expected-sha1>
```

Convert any format to RVZ:

```bash
dotnet run --project src/RVZSharp.Cli -c Release -- convert game.iso game.rvz
dotnet run --project src/RVZSharp.Cli -c Release -- convert game.wia game.rvz --compression zstd --level 5
```

`convert` is the flagship command: it accepts **any** supported input (plain ISO or any
legacy format) and writes a fully self-contained RVZ file. See the
[CLI reference](usage-cli.md) for all options.

## Notes for contributors

- **Never rename `CSharp_RVZSharp.slnx` to `.sln`** — the `dotnet` CLI rejects the old
  format (`MSB5010`) for this project; the XML solution format is required.
- Keep the build at **zero warnings** (`TreatWarningsAsErrors` is on).
- When changing format behaviour, cross-check against `References/dolphin-master/` (C++ is
  the source of truth for layout) and `References/rvz-1.0.3/` (Go reader) where possible;
  see [Testing](testing.md) for the validation strategy.
