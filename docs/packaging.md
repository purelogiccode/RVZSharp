# Packaging & distribution

`RVZSharp` is published as a NuGet package. This page documents what the package contains,
how to build it, how to publish it, and the quality gates that protect the API.

## Package facts

| | |
|---|---|
| Package ID | `RVZSharp` |
| Version | `0.1.0` (SemVer; bumped per release) |
| Target frameworks | `net8.0`, `net9.0`, `net10.0` |
| License | GPL-2.0-or-later (`PackageLicenseExpression`) |
| Dependencies | `LZMA-SDK`, `SharpZipLib`, `ZstdSharp.Port` (all pure managed) |
| Symbols | `RVZSharp.0.1.0.snupkg` (source link + embedded sources) |
| Reproducible | deterministic builds (`ContinuousIntegrationBuild` for release packs) |

## What's inside the package

```
lib/net8.0/RVZSharp.dll        assemblies per target framework
lib/net8.0/RVZSharp.xml        XML API documentation (IntelliSense)
lib/net9.0/…
lib/net10.0/…
README.md                      package readme (shown on nuget.org)
LICENSE                        GPL-2.0-or-later text
THIRD-PARTY-NOTICES.md         MIT notice for the vendored LZMA decoder
```

## Building the package

```bash
dotnet pack src/RVZSharp/RVZSharp.csproj -c Release
# output: src/RVZSharp/bin/Release/RVZSharp.<version>.nupkg (+ .snupkg)
```

For a deterministic release build (reproducible SourceLink paths):

```bash
dotnet pack src/RVZSharp/RVZSharp.csproj -c Release -p:ContinuousIntegrationBuild=true
```

## Quality gates

1. **Zero warnings** — `TreatWarningsAsErrors` is on; the pack fails on any analyzer
   warning.
2. **Package validation** — `EnablePackageValidation` checks at pack time that the
   `net8.0`/`net9.0`/`net10.0` assets are compatible with the package's supported
   frameworks. Once the API stabilizes (before `1.0.0`), add
   `PackageValidationBaselineVersion` to diff the public API against the previous release
   (API-compat analysis) — see the roadmap.
3. **Tests on every framework** — `dotnet test CSharp_RVZSharp.slnx -c Release` runs the
   full suite (255 tests) on `net8.0`, `net9.0` and `net10.0`.
4. **Consumer smoke test** — before publishing, a fresh project consuming only the nupkg
   (from a local feed) must compile and run on .NET 8 and .NET 10, converting and decoding
   a disc image byte-exactly. This catches packaging mistakes (missing files, wrong
   dependency graph) that unit tests cannot.

## Publishing to nuget.org

```bash
# 1. Build the package and the symbols package.
dotnet pack src/RVZSharp/RVZSharp.csproj -c Release

# 2. Push (the API key comes from nuget.org → API Keys).
dotnet nuget push src/RVZSharp/bin/Release/RVZSharp.0.1.0.nupkg \
    --source https://api.nuget.org/v3/index.json \
    --api-key <NUGET_API_KEY>
```

The `.snupkg` is pushed with the same command (NuGet uploads both). After publishing,
verify the package page: readme rendering, license, dependencies, and the `lib/` folder
list for all three target frameworks.

## Versioning policy

- The package version follows SemVer: `0.x.y` while the API may still change.
- Public API changes are intentional and reviewed: the reader/writer surface
  (`Blob.Open`, `RvzReader`, `RvzWriter.Write`) is stable; format-specific structs
  (`WiaDisc`, `WiaPartEntry`, …) may grow fields.
- Before a `1.0.0` release: real-game validation (see `docs/roadmap.md`), a
  `PackageValidationBaselineVersion`, and a public changelog.

## Local development feed

To test the package without publishing:

```xml
<!-- nuget.config -->
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="D:\path\to\src\RVZSharp\bin\Release" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

```bash
dotnet add package RVZSharp --version 0.1.0
```

## The CLI

The command-line tool (`src/RVZSharp.Cli`) is **not** packaged as a NuGet tool — it is a
reference implementation and smoke-test surface for the library. It targets `net8.0` so it
runs on .NET 8, 9 and 10 runtimes alike:

```bash
dotnet build CSharp_RVZSharp.slnx -c Release
dotnet run --project src/RVZSharp.Cli -c Release -- header -i game.rvz
```
