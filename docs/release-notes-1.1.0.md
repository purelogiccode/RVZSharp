# RVZSharp 1.1.0 Release Notes

RVZSharp 1.1.0 is a **feature and correctness release**. The public API stays compatible
with 1.0.0 (validated at pack time against the published package via
`PackageValidationBaselineVersion`), so upgrading is a drop-in.

```
dotnet add package RVZSharp
```

## What's new

### Formats & verification

- **Legacy writers** — `CisoWriter`, `WbfsWriter` and `TgcWriter` complete the container
  matrix and are exposed as `convert -f ciso|wbfs|tgc`; all-zero blocks/clusters are
  stored absent, so scrubbed images shrink.
- **Wii verification** — `DiscVerifier` walks every partition's h0/h1/h2/h3 hash tree
  plus the TMD/H3 tables and reports per-partition issues (`verify --partitions`).
- **Retail ticket keys** — partition keys are decrypted from the ticket with the Wii
  common key (Dolphin: `TicketReader::GetTitleKey`), so `verify --partitions`, `extract`
  and RVZ writing work on real encrypted discs; RVZ/WIA inputs prefer the container's
  authoritative partition key. `WiiVolume.GetTitleKey(ticket)` exposes the decryption.
- **Extract fixes** — `tmd.bin`/`cert.bin`/`h3.bin` are read at their partition-relative
  offsets, `h3.bin` is skipped for hashless discs, and partition headers are printed by
  `extract --list`.
- **WBFS fixes** — `hd_sector_count` is relative to the stream position, and the disc
  header copy is written at offset `0x200` for tools that identify discs without
  decoding.
- **Zero-group decode** — RVZ/WIA partition chunks that are entirely zeroes are decoded
  back to zeroes instead of rejecting the missing exception lists.

### CLI

- `--json` for `convert` and `verify`, `-` stdin/stdout piping, and
  `rvzsharp completions bash|zsh|fish|powershell`. Logs and progress go to stderr, so
  stdout stays machine-readable.
- `convert --verify` honors Ctrl+C (exit code 130), writer progress reaches exactly
  `1.0`, and the update check never blocks help runs.
- **Extract path safety** — `-s` paths and image file names can no longer escape the
  output folder; hostile names fail with a format error.

### Performance & robustness

- Thread-safe `ReadAt` with a 16 MiB LRU decoded-unit cache, binary-search area lookup
  and ArrayPool group reads.
- Parallel group compression and parallel RVZ/WIA full-image decode (`--threads`,
  byte-identical output for any thread count); Native AOT/trimming validated.
- `IBlobReader.Length`, the async API (`CopyToAsync`/`ReadFullyAsync`/`WriteAsync`),
  split plain ISO (`game.part0.iso`), `BlobStream`, `DiscInfo`, `Scrub` and path
  overloads.

## CLI downloads (1.1.0 bundles)

The 1.1.0 bundles are **framework-dependent single-file** executables: install the
[.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) once, then unzip
and run. They are far smaller than the self-contained 1.0.0 bundles and now cover six
runtimes, including macOS:

| File | Platform |
|---|---|
| `rvzsharp_v1.1.0_win-x64.zip` | Windows 10/11, x64 |
| `rvzsharp_v1.1.0_win-arm64.zip` | Windows 11, arm64 |
| `rvzsharp_v1.1.0_linux-x64.zip` | Linux, x64 |
| `rvzsharp_v1.1.0_linux-arm64.zip` | Linux, arm64 |
| `rvzsharp_v1.1.0_osx-x64.zip` | macOS, Intel |
| `rvzsharp_v1.1.0_osx-arm64.zip` | macOS, Apple Silicon |

Each zip holds the `RVZSharp` executable (`RVZSharp.exe` on Windows) plus `LICENSE`,
`README.md`, `WhatsNew.md` and `THIRD-PARTY-NOTICES.md`. Verify downloads against
`SHA256SUMS.txt`.

> Upgrading from 1.0.0? The 1.0.0 bundles were self-contained; 1.1.0 bundles require the
> .NET 10 Runtime to be installed.

## Trust, verified

- **609 library tests** on each of `net8.0`/`net9.0`/`net10.0` plus **57 CLI tests** and
  a **276-test real-file suite** (90 GameCube/Wii RVZ games decoded against their official
  No-Intro SHA-1s and re-encoded back to the same hash).
- Audited against Dolphin's implementation and the `rvz-1.0.3` Go reference; every
  divergence pinned by a regression test.

## Install & documentation

| | |
|---|---|
| NuGet | [`RVZSharp 1.1.0`](https://www.nuget.org/packages/RVZSharp/1.1.0) |
| Docs | the `docs/` wiki — format specs, library API, CLI reference |
| Repository | [github.com/purelogiccode/RVZSharp](https://github.com/purelogiccode/RVZSharp) |

## License

**GPL-2.0-or-later** — the RVZ/WIA format logic derives from
[Dolphin](https://github.com/dolphin-emu/dolphin), and the library is licensed under the
same terms Dolphin uses. All dependencies and third-party components are credited in
`THIRD-PARTY-NOTICES.md`.
