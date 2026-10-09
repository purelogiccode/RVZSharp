[![CI](https://github.com/purelogiccode/RVZSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/purelogiccode/RVZSharp/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-8.0_%7C_9.0_%7C_10.0-blueviolet)](https://dotnet.microsoft.com/)
[![NuGet](https://img.shields.io/nuget/v/RVZSharp?color=blue)](https://www.nuget.org/packages/RVZSharp/)
[![License](https://img.shields.io/badge/license-GPL--2.0--or--later-green)](https://github.com/purelogiccode/RVZSharp/blob/master/LICENSE)
[![Tests](https://img.shields.io/badge/tests-xUnit-brightgreen)](https://github.com/purelogiccode/RVZSharp/blob/master/docs/testing.md)

# RVZSharp

A pure managed C# library and CLI (**.NET 8 / 9 / 10**) for decoding and encoding **Dolphin
RVZ/WIA** disc images (GameCube/Wii), encoding the legacy **GCZ** format, and decoding the
remaining legacy formats (CISO/WBI, WBFS, TGC, NFS). RVZSharp decodes RVZ/WIA files back to
the original disc image (`.iso`) **byte-for-byte**, including:

```
dotnet add package RVZSharp
```

- all six compression methods: NONE, PURGE (WIA), BZIP2, LZMA, LZMA2, Zstandard (100% managed
  codecs — ZstdSharp.Port, SharpZipLib, and a vendored 7-Zip LZMA/LZMA2 decoder, see
  THIRD-PARTY-NOTICES.md);
- the RVZ packing scheme (Lagged Fibonacci PRNG padding reconstruction);
- Wii partition reconstruction: SHA-1 hash trees (h0/h1/h2), hash exceptions, and
  AES-128-CBC re-encryption with the partition key — the output is identical to the
  original encrypted disc image. Retail ticket title keys are decrypted with the Wii
  common key (Dolphin: `TicketReader::GetTitleKey`, exposed as `WiiVolume.GetTitleKey`),
  and RVZ/WIA inputs use the container's authoritative partition key;
- full container validation (magic, versions, all SHA-1 integrity checks, structure rules);
- thread-safe random access (`RvzReader.ReadAt`) with a bounded 16 MiB LRU cache of decoded
  units, and Native AOT/trimming compatibility (the library is annotated and the CLI
  publishes clean with `PublishTrimmed`/`PublishAot`).

## Usage

### Library

```bash
dotnet add package RVZSharp
```

**1. Open any disc image** — the format is auto-detected from the magic bytes (RVZ, WIA,
GCZ, CISO/WBI, WBFS, TGC, NFS, plain ISO):

```csharp
using RVZSharp.Blobs;

// From a file path:
using var blob = Blob.Open(@"C:\games\game.rvz");

// From a stream (same result for any supported format):
using var stream = File.OpenRead(@"C:\games\game.gcz");
using var blob2 = Blob.Open(stream);

// NFS images carry their AES key outside the file (Dolphin convention):
using var blob3 = Blob.Open(nfsStream, nfsKey, leaveOpen: true);
```

**2. Decode — the whole image, or random-access ranges:**

```csharp
using var file = File.OpenRead(@"C:\games\game.rvz");
using var reader = RvzReader.Open(file);

Console.WriteLine($"ISO size: {reader.Length} bytes");             // 1459978240
Console.WriteLine($"Disc: {reader.Disc.DiscType}");                // Wii
Console.WriteLine($"Compression: {reader.Disc.Compression}");      // Zstd

// Decode everything (the full disc image, byte-for-byte, up to 2 GiB):
var iso = reader.ReadFully();

// ...or stream any range without decoding the whole file:
var buffer = new byte[0x80000];
long read = reader.ReadAt(partitionStart, buffer);

// ...or stream the whole image to a file (any size, with progress/cancellation):
var progress = new Progress<double>(f => Console.Error.Write($"\r{f,6:P1}"));
reader.CopyTo(File.Create(@"C:\games\game.iso"), progress);
```

`RvzReader.Open` parses and validates the whole container (magic, versions, every SHA-1,
structure rules) and throws `RvzHashMismatchException` / `RvzFormatException` on damage.

**3. Write an RVZ from any supported image** (mirrors Dolphin's converter):

```csharp
using RVZSharp;
using RVZSharp.Blobs;

using var input = Blob.Open(@"C:\games\game.wia");
using var output = File.Create(@"C:\games\game.rvz");

var options = new RvzWriteOptions
{
    Compression = CompressionType.Zstd,   // None, Bzip2, Lzma, Lzma2, Zstd
    CompressionLevel = 5,                 // Zstd: -131072..22 (negative = fast), others 1-9
    ChunkSize = 131072,
    Packing = true                        // PRNG-junk packing (smaller files)
};

RvzWriter.Write(input: input, output: output, options: options);
```

Wii partitions are stored decrypted with hash exceptions, exactly like Dolphin produces.
`options` defaults to the Dolphin-compatible settings (Zstd / level 5 / 2 MiB chunks,
packing on). Set `Scrub = true` to zero non-game Wii partitions (Dolphin's DiscScrubber).
Group packing/compression runs on a worker pool (`MaxThreads`; `0` = processor count) and
the output is byte-identical for any thread count. To get a plain ISO back, use the CLI's
`convert -f iso` (or a reader + copy). `RvzWriter.Write(inputPath, outputPath, options)`
(and the WIA/GCZ equivalents) opens and creates the files for you.

**4. Write WIA** (`WiaWriter`, sharing the same writer core; PURGE supported, no packing,
chunk size a multiple of 2 MiB):

```csharp
using var wia = File.Create(@"C:\games\game.wia");
WiaWriter.Write(input, wia, new RvzWriteOptions { Compression = CompressionType.Lzma2 });
```

**5. Write GCZ** (`GczWriter`; 16 KiB zlib blocks by default, per-block Adler-32, parallel):

```csharp
using var gcz = File.Create(@"C:\games\game.gcz");
GczWriter.Write(input, gcz, new GczWriteOptions { BlockSize = 0x4000 });
```

**6. Write the legacy formats** (`CisoWriter`, `WbfsWriter` — Wii only, `TgcWriter` —
GameCube only; all-zero blocks/clusters are stored absent, so scrubbed images shrink):

```csharp
using var ciso = File.Create(@"C:\games\game.ciso");
CisoWriter.Write(input, ciso, new CisoWriteOptions { BlockSize = 0x200000 });

using var wbfs = File.Create(@"C:\games\game.wbfs");
WbfsWriter.Write(wiiInput, wbfs); // 2 MiB clusters by default

using var tgc = File.Create(@"C:\games\game.tgc");
TgcWriter.Write(gcInput, tgc);
```

**7. Progress and cancellation** for long conversions (encode *and* decode):

```csharp
using var cts = new CancellationTokenSource();
var progress = new Progress<double>(f => Console.Error.Write($"\r{f,6:P1}"));

RvzWriter.Write(input: input, output: output, options: options,
    progress: progress, cancellationToken: cts.Token);

// Decode/stream instead: any size, no 2 GiB limit.
blob.CopyTo(isoStream, progress, cts.Token);
```

**8. Verify a decode** without materializing the ISO — `DiscHasher.Compute(blob)` returns
the CRC-32, MD5 and SHA-1 in one streaming pass (the digests Dolphin's verifier reports).
`DiscVerifier.Verify(blob)` goes further and walks every Wii partition's h0/h1/h2/h3 hash
tree plus the TMD/H3 tables (Dolphin's VolumeVerifier), reporting per-partition issues:

```csharp
var hashes = DiscHasher.Compute(blob);
Console.WriteLine(Convert.ToHexString(hashes.Sha1));

var report = DiscVerifier.Verify(blob);
foreach (var issue in report.Issues.Concat(report.Partitions.SelectMany(p => p.Issues)))
{
    Console.WriteLine($"[{issue.Severity}] {issue.Message}");
}
```

**9. Handling errors** — every format problem raises `RvzException` subclasses:

```csharp
try
{
    using var reader = RvzReader.Open(file);
    _ = reader.ReadFully();
}
catch (RvzHashMismatchException) { /* a SHA-1 failed (corrupt container) */ }
catch (RvzFormatException)       { /* structural damage / unsupported feature */ }
```

**9. Read disc metadata** — `DiscInfo.TryRead(blob)` returns the game ID, maker ID, revision,
internal name, region, country and Wii title ID from any container (Dolphin: `VolumeDisc`):

```csharp
var info = DiscInfo.TryRead(blob);
Console.WriteLine($"{info?.GameId} {info?.InternalName} ({info?.Region}, {info?.Country})");
```

**10. Use a decoded image as a `Stream`** — `new BlobStream(blob)` is a read-only, seekable
stream over any blob (any size) for `BinaryReader`/serializer-style code:

```csharp
using var stream = new BlobStream(blob);
var reader = new BinaryReader(stream);
```

**What `Blob.Open` accepts and rejects:** a file that starts with a recognized container
magic (RVZ/WIA/CISO/GCZ/WBFS/TGC/NFS) is always parsed as that container — a parse failure
throws an `RvzException` (`RvzFormatException`, `RvzHashMismatchException`, or
`RvzUnsupportedException` for newer container versions), never a silent fallback. Only
files with **no recognizable magic** are treated as a plain ISO — including split plain ISOs
(`game.part0.iso` + `game.part1.iso` + …, found from the path, like Dolphin's
`SplitPlainFileReader`). CISO is validated lazily like Dolphin (a plausible block size opens,
absent blocks decode to zeroes); `RvzWriter` handles the rest.

**`RvzWriter` validates the input:** the decoded bytes must carry the GameCube/Wii disc
header magic (Wii `5D 1C 9E A3` at offset `0x18`, GameCube `C2 33 9F 3D` at offset `0x1C`,
like Dolphin's `TryCreateDisc`). Any other input throws `RvzFormatException` before a
single byte is written — arbitrary data can never be wrapped into an unusable RVZ, so
`Blob.Open` + `RvzWriter.Write` is a complete "is this a real disc?" pipeline for every
input format. To run the same check yourself, use `Blob.GetDiscType(blob)` /
`Blob.IsDisc(blob)` (they inspect the decoded disc bytes of any blob, not just the
container magic).

### CLI

```
dotnet run --project RVZSharp.Cli -- header -i <file.rvz|.wia|.gcz|.ciso|.wbfs|.tgc|.nfs|.iso>
dotnet run --project RVZSharp.Cli -- verify -i <file> [-a crc32|md5|sha1] [--partitions] [--json]
dotnet run --project RVZSharp.Cli -- convert -i <file> -o <out> -f iso|rvz|wia|gcz|ciso|wbfs|tgc \
    [-b <block_size>] [-c none|zstd|bzip2|lzma|lzma2|purge] [-l <level>] [-s] \
    [--threads <n>] [--verify] [--json]
dotnet run --project RVZSharp.Cli -- extract -i <file> [-o <dir>] [-p <name>] \
    [-s <path>] [-l] [-q] [-g]
dotnet run --project RVZSharp.Cli -- completions bash|zsh|fish|powershell
```

The CLI accepts the same command arguments as Dolphin's `dolphin-tool` (`convert`,
`verify`, `header`, `extract`). `convert` accepts
**any** readable blob (a plain ISO or one of the legacy formats, including **split WBFS**
`.wbfs`+`.wbf1…` parts) and writes an RVZ, WIA or GCZ file, mirroring Dolphin's converter:
Wii partitions are stored decrypted with hash exceptions (RVZ/WIA), raw data as-is, PRNG
junk is packed with a recovered seed (Lagged Fibonacci `GetSeed`, RVZ only), and the tables
carry all SHA-1 checksums. `--scrub` zeroes the data of non-game Wii partitions
(update/channel) before converting. `-f iso` decodes back to a plain ISO. RVZSharp
extensions: `--threads <n>` sets the compression/decode worker count (output is
byte-identical for any value), `--verify` re-decodes the written file and compares
CRC-32/MD5/SHA-1 with the input, `--json` prints machine-readable results on stdout, `-`
reads stdin / writes stdout, `completions <shell>` prints bash/zsh/fish/PowerShell
 completion scripts, and `-c purge` exposes PURGE for WIA. `extract` reads the disc's file
 system: list or extract the FST tree and the system data (boot/BI2/apploader/DOL/FST, Wii
 disc header/region, ticket/TMD/cert/H3) per partition. `-s` takes a Dolphin-style FST
 path (`/` separators, relative, without `..`) and extraction can never escape `-o`:
 absolute paths stay inside, `..` is rejected, and hostile image file names fail with a
 format error.

## Documentation

The full documentation lives in [`docs/`](https://github.com/purelogiccode/RVZSharp/blob/master/docs/README.md) — published by CI to the
[project wiki](https://github.com/purelogiccode/RVZSharp/wiki) and the GitHub Pages site (side menu in both) — covering
[what's new](https://github.com/purelogiccode/RVZSharp/blob/master/docs/whats-new.md), the [CLI](https://github.com/purelogiccode/RVZSharp/blob/master/docs/usage-cli.md), the
[library API](https://github.com/purelogiccode/RVZSharp/blob/master/docs/usage-library.md), [architecture](https://github.com/purelogiccode/RVZSharp/blob/master/docs/architecture.md), the
[RVZ container format](https://github.com/purelogiccode/RVZSharp/blob/master/docs/format/rvz.md),
[compression & packing](https://github.com/purelogiccode/RVZSharp/blob/master/docs/format/compression-packing.md),
[Wii partitions](https://github.com/purelogiccode/RVZSharp/blob/master/docs/format/wii-partitions.md), the
[legacy formats](https://github.com/purelogiccode/RVZSharp/blob/master/docs/format/legacy.md), [testing](https://github.com/purelogiccode/RVZSharp/blob/master/docs/testing.md),
[packaging & distribution](https://github.com/purelogiccode/RVZSharp/blob/master/docs/packaging.md), [roadmap](https://github.com/purelogiccode/RVZSharp/blob/master/docs/roadmap.md) and a
[FAQ](https://github.com/purelogiccode/RVZSharp/blob/master/docs/faq.md). Release highlights are summarized in [WhatsNew.md](https://github.com/purelogiccode/RVZSharp/blob/master/WhatsNew.md).

## Project layout

- `RVZSharp` — the library: `Models` (container structs), `Interfaces` (`IBlobReader`,
  codec contracts), `IO` (big-endian reading, section streams), `Compression` (codecs +
  factories), `Chunks` (group decoding, exception lists), `Packing` (RVZ packing + PRNG,
  encoder and decoder), `Wii` (hash tree + region rebuild, partition extraction, decrypted
  `PartitionReader`), `Files` (`DiscFileSystem` FST parser), `Verification` (`DiscVerifier`
  Wii hash-tree/TMD/H3 verifier), `RvzReader`, `RvzWriter`, `WiaWriter`, `GczWriter`,
  `CisoWriter`, `WbfsWriter`, `TgcWriter`. Every public and internal type and member carries
  XML documentation (shipped in the package as `RVZSharp.xml` for IntelliSense).
- `RVZSharp.Cli` — the `header`/`verify`/`convert`/`extract` tool (DolphinTool-compatible
  surface, plus the legacy `info`/`decode` commands, `--json` output, `-` stdin/stdout and
  shell completions).
- `RVZSharp.Tests` — 609 synthetic tests (net8.0 + net9.0 + net10.0): unit (headers,
  tables, codecs, PRNG, packing, exceptions, region rebuild, metadata/offset edge cases,
  low-level IO helpers) and end-to-end round-trips of synthetic RVZ files built by
  `TestRvzBuilder`, plus writer round trips (every codec × packing, GC + Wii, legacy → RVZ,
  split WBFS, scrubbing), GCZ writer tests, parallel write/decode determinism tests,
  FST/file-system validation tests, async API tests, package-facing API tests (path open,
  ReadFully, progress, cancellation).
- `RVZSharp.Cli.Tests` — 57 tests (net10.0) for the CLI's option parser, extract path guards, shell-completion scripts and the
  update checker's release-tag parsing and `RVZSHARP_NO_UPDATE_CHECK` switch.
- `RVZSharp.Slow.Tests` — 276 real-file tests (`RealRvzFileTests`) that decode 90 real
  GameCube/Wii RVZ images byte-for-byte against their official No-Intro DAT SHA-1s.
  Kept out of the solution, so a plain `dotnet test` never runs them (~30 min); run
  explicitly with `dotnet test RVZSharp.Slow.Tests` (details in [docs/testing.md](https://github.com/purelogiccode/RVZSharp/blob/master/docs/testing.md)).
- `RVZSharp.Benchmarks` — BenchmarkDotNet suite (net10.0): encode/decode throughput per
  codec and writer thread scaling, on a synthetic 16 MiB GameCube image
  (`dotnet run -c Release --project RVZSharp.Benchmarks`).

## Real-world validation

The slow suite validates the decoder and writer against actual game images on a local
drive (`F:\Nintendo GameCube` / `F:\Nintendo Wii`):

- **180 decode tests** — 90 full-decode SHA-1 checks (45 GameCube + 45 Wii) plus an
  expected-ISO-size check per file, each compared byte-for-byte against its official
  No-Intro DAT entry (the canonical hash of the original disc image, from
  `References/rvz-1.0.3/testdata/*.dat`);
- **90 structural tests** — RVZ magic/version, legal chunk size, compression method and
  group-table sanity on every file;
- **3 region/random-access tests** — full-read hashing, `ReadAt` vs `ReadFully` across chunk
  boundaries, out-of-range clamping;
- **2 writer round-trips** — a real GameCube and a real Wii RVZ are re-encoded to RVZ with
  default options and decoded back to the same SHA-1.

The tests no-op when the files are not mounted, so the suite stays green on machines without
the games. The real Wii round-trip exposed and pinned a writer bug (see Status below).

## Status

RVZ **and** the legacy disc formats (WIA, GCZ, CISO/WBI, WBFS incl. split files, TGC, NFS)
are decoded byte-for-byte and covered by tests; the CLI `info`/`decode` commands accept any
of them (auto-detected by magic). The writers (`rvzsharp convert -f rvz|wia|gcz|ciso|wbfs|tgc`)
encode any of them back to RVZ (Zstd/Bzip2/LZMA1/LZMA2/None with Dolphin's level rules —
including negative Zstd "fast" levels — optional packing, chunks of 32 KiB–2 MiB powers of
two or multiples of 2 MiB), WIA (None/PURGE/Bzip2/LZMA1/LZMA2, chunks that are multiples of
2 MiB), GCZ (16 KiB zlib blocks by default, power-of-two sizes), CISO (all-zero blocks
absent), WBFS (Wii only, shared zero cluster) or TGC (GameCube only), with the same SHA-1s
Dolphin produces. Decode supports progress/cancellation;
`DiscHasher` computes CRC-32/MD5/SHA-1 in one pass and `DiscVerifier` walks the Wii hash
trees and TMD/H3 tables. The codebase was audited against the
reference implementations (Dolphin `WIABlob`/`WIACompression` and the Go `rvz-1.0.3` tool)
and every finding was fixed or explicitly documented.

**Real-world validation is done**: 90 real GameCube/Wii RVZ files decode byte-for-byte to
their official No-Intro SHA-1s, and real GC/Wii images re-encode to RVZ and decode back to
the same hash. That work also found and fixed a production writer bug: when re-encoding a
real Wii game with the default **2 MiB chunk size**, the writer used the ISO ticket key
instead of the RVZ partition-table key (No-Intro dumps carry re-signed tickets whose key
differs), producing files the reader rejected. `RvzWriter` now prefers the container's
partition-table key and falls back to the ticket key for plain ISO inputs.

**Packaging & CI**: the NuGet package is API-compat validated against the last published
release (`PackageValidationBaselineVersion` 1.0.0) and carries an embedded SPDX 2.2 SBOM;
GitHub Actions (`.github/workflows/ci.yml`) builds and tests on `net8.0`/`net9.0`/`net10.0`
with coverage, packs, and publishes a smoke-tested ReadyToRun CLI artifact. Dependabot
keeps NuGet and Actions dependencies current.

**1.1.0 (unreleased)** adds the legacy writers (CISO/WBFS/TGC), `DiscVerifier`, the CI and
packaging gates above, and the CLI conveniences (`--json`, `-` stdin/stdout, completions).
A post-release review also fixed a set of correctness bugs: retail ticket title keys are
now common-key decrypted (so `verify --partitions`, `extract` and RVZ writing work on real
encrypted discs), extract's `tmd.bin`/`cert.bin`/`h3.bin` use partition-relative offsets,
the WBFS header declares the file size correctly and carries the disc-header copy,
`convert --verify` honors Ctrl+C (exit 130), writer progress is monotonic, and parallel
decode/encode failures surface the original exception. A follow-up audit fixed
zero-group decoding (all-zero partition chunks no longer fail), extract path traversal
(`-s` paths and image file names cannot escape `-o`), `decode -h`, help-run telemetry
waits and stdin-spool cancellation. See [WhatsNew.md](https://github.com/purelogiccode/RVZSharp/blob/master/WhatsNew.md).

## License

RVZSharp is copyright (c) 2025-2026 by **Peterson Fernandes**
([github.com/drpetersonfernandes](https://github.com/drpetersonfernandes)) and **Pure Logic
Code** ([github.com/purelogiccode](https://github.com/purelogiccode)).

The RVZ/WIA format logic in this library is derived from
[Dolphin](https://github.com/dolphin-emu/dolphin), which is licensed under the
**GNU General Public License, version 2 or later**. To stay fully compliant, RVZSharp is
distributed under the **same license (GPL-2.0-or-later)** — see [LICENSE](https://github.com/purelogiccode/RVZSharp/blob/master/LICENSE).

All third-party code and dependencies are listed in
[THIRD-PARTY-NOTICES.md](https://github.com/purelogiccode/RVZSharp/blob/master/THIRD-PARTY-NOTICES.md) (MIT SharpCompress LZMA decoder port,
MIT/public-domain runtime dependencies, GPL Dolphin as the format source, BSD Go reader as
validation reference).
