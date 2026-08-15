# RVZSharp

A pure managed C# library and CLI (**.NET 8 / 9 / 10**) for decoding and encoding **Dolphin
RVZ** disc images (GameCube/Wii). RVZ is the successor of the WIA format; RVZSharp decodes
RVZ files back to the original disc image (`.iso`) **byte-for-byte**, including:

```
dotnet add package RVZSharp
```

- all five compression methods: NONE, BZIP2, LZMA, LZMA2, Zstandard (100% managed codecs —
  ZstdSharp.Port, SharpZipLib, and a vendored 7-Zip LZMA/LZMA2 decoder, see THIRD-PARTY-NOTICES.md);
- the RVZ packing scheme (Lagged Fibonacci PRNG padding reconstruction);
- Wii partition reconstruction: SHA-1 hash trees (h0/h1/h2), hash exceptions, and
  AES-128-CBC re-encryption with the partition key — the output is identical to the
  original encrypted disc image;
- full container validation (magic, versions, all SHA-1 integrity checks, structure rules).

## Usage

### Library

```csharp
using RVZSharp;

using var file = File.OpenRead("game.rvz");
using var reader = RvzReader.Open(file);

Console.WriteLine($"ISO size: {reader.Length} bytes");
Console.WriteLine($"Compression: {reader.Disc.Compression}");

// Random-access decode (the whole image, or ReadAt any range)
var iso = reader.ReadFully();
```

`RvzReader.Open` parses and validates the whole container; `ReadAt(position, span)` serves
decoded bytes at any offset; `ReadFully()` decodes the entire image.

### CLI

```
dotnet run --project RVZSharp.Cli -- header -i <file.rvz|.wia|.gcz|.ciso|.wbfs|.tgc|.nfs|.iso>
dotnet run --project RVZSharp.Cli -- verify -i <file> [-a crc32|md5|sha1]
dotnet run --project RVZSharp.Cli -- convert -i <file> -o <out> -f iso|rvz \
    [-b <block_size>] [-c none|zstd|bzip2|lzma|lzma2] [-l <level>] [-s]
```

The CLI accepts the same command arguments as Dolphin's `dolphin-tool` (`convert`,
`verify`, `header`; `extract` is recognized but not implemented). `convert` accepts
**any** readable blob (a plain ISO or one of the legacy formats, including **split WBFS**
`.wbfs`+`.wbf1…` parts) and writes an RVZ file, mirroring Dolphin's converter: Wii
partitions are stored decrypted with hash exceptions, raw data as-is, PRNG junk is packed
with a recovered seed (Lagged Fibonacci `GetSeed`), and the tables carry all SHA-1
checksums. `--scrub` zeroes the data of non-game Wii partitions (update/channel) before
converting. `-f iso` decodes back to a plain ISO.

## Documentation

The full documentation lives in [`docs/`](docs/README.md) — a multi-page wiki covering the
[CLI](docs/usage-cli.md), the [library API](docs/usage-library.md),
[architecture](docs/architecture.md), the [RVZ container format](docs/format/rvz.md),
[compression & packing](docs/format/compression-packing.md),
[Wii partitions](docs/format/wii-partitions.md), the
[legacy formats](docs/format/legacy.md), [testing](docs/testing.md),
[packaging & distribution](docs/packaging.md), [roadmap](docs/roadmap.md) and a
[FAQ](docs/faq.md).

## Project layout

- `RVZSharp` — the library: `Models` (container structs), `Interfaces` (`IBlobReader`,
  codec contracts), `IO` (big-endian reading, section streams), `Compression` (codecs +
  factories), `Chunks` (group decoding, exception lists), `Packing` (RVZ packing + PRNG,
  encoder and decoder), `Wii` (hash tree + region rebuild, partition extraction for the
  writer), `RvzReader`, `RvzWriter`. Every public and internal type and member carries XML
  documentation (shipped in the package as `RVZSharp.xml` for IntelliSense).
- `RVZSharp.Cli` — the `header`/`verify`/`convert` tool (DolphinTool-compatible surface,
  plus the legacy `info`/`decode` commands).
- `RVZSharp.Tests` — 313 synthetic tests (net8.0 + net9.0 + net10.0): unit (headers,
  tables, codecs, PRNG, packing, exceptions, region rebuild) and end-to-end round-trips of
  synthetic RVZ files built by `TestRvzBuilder`, plus writer round trips (every codec ×
  packing, GC + Wii, legacy → RVZ, split WBFS, scrubbing), package-facing API tests (path
  open, ReadFully, progress, cancellation).
- `RVZSharp.Slow.Tests` — 97 real-file tests (`RealRvzFileTests`) that decode real
  GameCube/Wii RVZ images byte-for-byte against their official No-Intro DAT SHA-1s.
  Kept out of the solution, so a plain `dotnet test` never runs them (~12 min); run
  explicitly with `dotnet test RVZSharp.Slow.Tests` (details in [docs/testing.md](docs/testing.md)).

## Real-world validation

The slow suite validates the decoder and writer against actual game images on a local
drive (`F:\Nintendo GameCube` / `F:\Nintendo Wii`):

- **60 decode tests** — 30 full-decode SHA-1 checks (15 GameCube + 15 Wii) plus an
  expected-ISO-size check per file, each compared byte-for-byte against its official
  No-Intro DAT entry (the canonical hash of the original disc image, from
  `References/rvz-1.0.3/testdata/*.dat`);
- **30 structural tests** — RVZ magic/version, legal chunk size, compression method and
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
of them (auto-detected by magic). The RVZ writer (`rvzsharp convert`) encodes any of them
back to RVZ (Zstd/Bzip2/LZMA1/LZMA2/None with Dolphin's level rules — including negative
Zstd "fast" levels — optional packing, chunks of 32 KiB–2 MiB powers of two or multiples of
2 MiB), with the same SHA-1s Dolphin produces. The codebase was audited against the
reference implementations (Dolphin `WIABlob`/`WIACompression` and the Go `rvz-1.0.3` tool)
and every finding was fixed or explicitly documented.

**Real-world validation is done**: 30 real GameCube/Wii RVZ files decode byte-for-byte to
their official No-Intro SHA-1s, and real GC/Wii images re-encode to RVZ and decode back to
the same hash. That work also found and fixed a production writer bug: when re-encoding a
real Wii game with the default **2 MiB chunk size**, the writer used the ISO ticket key
instead of the RVZ partition-table key (No-Intro dumps carry re-signed tickets whose key
differs), producing files the reader rejected. `RvzWriter` now prefers the container's
partition-table key and falls back to the ticket key for plain ISO inputs.
