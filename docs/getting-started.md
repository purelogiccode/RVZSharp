# Getting started

## Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download) (or newer). The library and the
  test suite target **`net8.0`, `net9.0` and `net10.0`**; the CLI targets `net8.0` so the
  built tool runs on all three runtimes.
- A disc image to experiment with: a plain `.iso`, or a `.rvz`/`.wia`/`.gcz`/`.ciso`/
  `.wbfs`/`.tgc`/`.nfs` file.

> No game files are required for the test suite — tests generate synthetic discs.

## Repository layout

```
CSharp_RVZSharp.sln        solution file (fast test suite runs solution-wide)
Directory.Build.props       net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors
RVZSharp/                   the library
RVZSharp.Cli/               the header/verify/convert tool
RVZSharp.Tests/             fast unit + end-to-end tests (313, ×3 frameworks)
RVZSharp.Slow.Tests/        real-file tests (97) — kept out of the solution; run explicitly
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

Expected result: `Passed: 313, Failed: 0` on **each** of `net8.0`, `net9.0` and `net10.0`
(the suite runs once per target framework). The real-file suite is not part of the solution
and runs only when requested:

```bash
dotnet test RVZSharp.Slow.Tests -c Release   # 97 real-game tests, ~12 min when mounted
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
dotnet run --project RVZSharp.Cli -c Release -- convert -i game.rvz -o game.iso -f iso
```

`convert` accepts **any** supported input (plain ISO or any legacy format) and writes a
fully self-contained RVZ file (`-f iso` decodes back to a plain ISO). See the
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
