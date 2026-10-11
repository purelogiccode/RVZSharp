# RVZSharp 1.1.1 Release Notes

RVZSharp 1.1.1 is a **correctness and packaging patch**. The public API stays compatible
with 1.0.0 (validated at pack time against the published package via
`PackageValidationBaselineVersion`), so upgrading is a drop-in.

```
dotnet add package RVZSharp
```

## What's new

### Zstandard encoder memory (max level)

- **One-shot compression** — `ZstdEncoder` now compresses each chunk with a single
  `ZSTD_compress2` call (like Dolphin's `ZstdCompressor`) instead of a streaming
  session. A streaming session without a pledged source size reserves the level's full
  window before seeing any input (~128 MiB per worker at level 22), which exhausted
  memory when groups were compressed in parallel on modest machines; one-shot sizing
  caps the window at the chunk size. The output is unchanged standard Zstandard: a
  level-22 file decodes byte-identically in Dolphin's `dolphin-tool`.
- Pinned by `Zstd_MaxLevel_ParallelRoundTrip` plus a bidirectional `dolphin-tool`
  interop matrix (zstd, lzma, lzma2, bzip2, none at 2 MiB and 128 KiB blocks, both
  directions, SHA-1-compared against real game images).

### Packaging

- **No more empty symbols package** — the assemblies carry embedded portable PDBs, so
  the `.snupkg` build carried no symbol files and nuget.org rejected it; only the
  `.nupkg` ships now. Debuggers read the embedded PDBs directly.

## CLI downloads (1.1.1 bundles)

The 1.1.1 bundles are **framework-dependent single-file** executables: install the
[.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) once, then unzip
and run:

| File | Platform |
|---|---|
| `rvzsharp_v1.1.1_win-x64.zip` | Windows 10/11, x64 |
| `rvzsharp_v1.1.1_win-arm64.zip` | Windows 11, arm64 |
| `rvzsharp_v1.1.1_linux-x64.zip` | Linux, x64 |
| `rvzsharp_v1.1.1_linux-arm64.zip` | Linux, arm64 |
| `rvzsharp_v1.1.1_osx-x64.zip` | macOS, Intel |
| `rvzsharp_v1.1.1_osx-arm64.zip` | macOS, Apple Silicon |

Each zip holds the `RVZSharp` executable (`RVZSharp.exe` on Windows) plus `LICENSE`,
`README.md`, `WhatsNew.md` and `THIRD-PARTY-NOTICES.md`. Verify downloads against
`SHA256SUMS.txt`.

## Trust, verified

- **610 library tests** on each of `net8.0`/`net9.0`/`net10.0` plus **57 CLI tests** and
  a **276-test real-file suite** (90 GameCube/Wii RVZ games decoded against their official
  No-Intro SHA-1s and re-encoded back to the same hash), plus a full `dolphin-tool`
  codec interop matrix in both directions.
- Audited against Dolphin's implementation and the `rvz-1.0.3` Go reference; every
  divergence pinned by a regression test.

## Install & documentation

| | |
|---|---|
| NuGet | [`RVZSharp 1.1.1`](https://www.nuget.org/packages/RVZSharp/1.1.1) |
| Docs | the `docs/` wiki — format specs, library API, CLI reference |
| Repository | [github.com/purelogiccode/RVZSharp](https://github.com/purelogiccode/RVZSharp) |

## License

**GPL-2.0-or-later** — the RVZ/WIA format logic derives from
[Dolphin](https://github.com/dolphin-emu/dolphin), and the library is licensed under the
same terms Dolphin uses. All dependencies and third-party components are credited in
`THIRD-PARTY-NOTICES.md`.
