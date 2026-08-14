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
dotnet run --project src/RVZSharp.Cli -- header -i <file.rvz|.wia|.gcz|.ciso|.wbfs|.tgc|.nfs|.iso>
dotnet run --project src/RVZSharp.Cli -- verify -i <file> [-a crc32|md5|sha1]
dotnet run --project src/RVZSharp.Cli -- convert -i <file> -o <out> -f iso|rvz \
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

- `src/RVZSharp` — the library: `Format` (container structs), `Io` (big-endian reading,
  section streams), `Compression` (codecs + `ICompressionEncoder`), `Chunks` (group decoding,
  exception lists), `Packing` (RVZ packing + PRNG, encoder and decoder), `Wii` (hash tree +
  region rebuild, partition extraction for the writer), `RvzReader`, `RvzWriter`.
- `src/RVZSharp.Cli` — the `info`/`decode`/`convert` tool.
- `tests/RVZSharp.Tests` — 255 tests (net8.0 + net9.0 + net10.0): unit (headers, tables, codecs,
  PRNG, packing, exceptions, region rebuild) and end-to-end round-trips of synthetic RVZ files
  built by `TestRvzBuilder`, plus writer round trips (every codec × packing, GC + Wii,
  legacy → RVZ, split WBFS, scrubbing) and package-facing API tests (path open, ReadFully,
  progress, cancellation).

## Real-world validation

Point the optional test at a real `.rvz` file to verify against its known ISO SHA-1
(e.g. from the No-Intro datfiles in `References/rvz-1.0.3/testdata/*.dat`):

```
RVZ_REAL_FILE=C:\path\to\game.rvz RVZ_REAL_SHA1=<expected> dotnet test tests/RVZSharp.Tests
```

## Status

RVZ **and** the legacy disc formats (WIA, GCZ, CISO/WBI, WBFS incl. split files, TGC, NFS)
are decoded byte-for-byte and covered by tests; the CLI `info`/`decode` commands accept any
of them (auto-detected by magic). The RVZ writer (`rvzsharp convert`) encodes any of them
back to RVZ (Zstd/Bzip2/LZMA1/LZMA2/None with Dolphin's level rules — including negative
Zstd "fast" levels — optional packing, chunks of 32 KiB–2 MiB powers of two or multiples of
2 MiB), with the same SHA-1s Dolphin produces. The codebase was audited against the
reference implementations (Dolphin `WIABlob`/`WIACompression` and the Go `rvz-1.0.3` tool)
and every finding was fixed or explicitly documented; see `TODO.md` for the record.
Remaining work: validation against real game images (see `docs/roadmap.md`).
