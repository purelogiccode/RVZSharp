# RVZSharp

A pure C# (.NET 10) library and CLI for decoding **Dolphin RVZ** disc images (GameCube/Wii).
RVZ is the successor of the WIA format; RVZSharp decodes RVZ files back to the original
disc image (`.iso`) **byte-for-byte**, including:- all five compression methods: NONE, BZIP2, LZMA, LZMA2, Zstandard (100% managed codecs —
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
dotnet run --project src/RVZSharp.Cli -- info <file.rvz|.wia|.gcz|.ciso|.wbfs|.tgc|.nfs|.iso>
dotnet run --project src/RVZSharp.Cli -- decode <file> out.iso [--sha1 <expected-hex>]
dotnet run --project src/RVZSharp.Cli -- convert <file.rvz|.wia|.gcz|.ciso|.wbfs|.tgc|.nfs|.iso> out.rvz \
    [--compression none|zstd|bzip2|lzma|lzma2] [--level <1-9>] [--chunk-size <kib>] [--no-packing]
```

`convert` accepts **any** readable blob (a plain ISO or one of the legacy formats) and
writes an RVZ file, mirroring Dolphin's converter: Wii partitions are stored decrypted with
hash exceptions, raw data as-is, PRNG junk is packed with a recovered seed (Lagged
Fibonacci `GetSeed`), and the tables carry all SHA-1 checksums.

## Documentation

The full documentation lives in [`docs/`](docs/README.md) — a multi-page wiki covering the
[CLI](docs/usage-cli.md), the [library API](docs/usage-library.md),
[architecture](docs/architecture.md), the [RVZ container format](docs/format/rvz.md),
[compression & packing](docs/format/compression-packing.md),
[Wii partitions](docs/format/wii-partitions.md), the
[legacy formats](docs/format/legacy.md), [testing](docs/testing.md),
[roadmap](docs/roadmap.md) and a [FAQ](docs/faq.md).

## Project layout

- `src/RVZSharp` — the library: `Format` (container structs), `Io` (big-endian reading,
  section streams), `Compression` (codecs + `ICompressionEncoder`), `Chunks` (group decoding,
  exception lists), `Packing` (RVZ packing + PRNG, encoder and decoder), `Wii` (hash tree +
  region rebuild, partition extraction for the writer), `RvzReader`, `RvzWriter`.
- `src/RVZSharp.Cli` — the `info`/`decode`/`convert` tool.
- `tests/RVZSharp.Tests` — 221 tests: unit (headers, tables, codecs, PRNG, packing,
  exceptions, region rebuild) and end-to-end round-trips of synthetic RVZ files built by
  `TestRvzBuilder`, plus writer round trips (every codec × packing, GC + Wii, legacy → RVZ).

## Real-world validation

Point the optional test at a real `.rvz` file to verify against its known ISO SHA-1
(e.g. from the No-Intro datfiles in `References/rvz-1.0.3/testdata/*.dat`):

```
RVZ_REAL_FILE=C:\path\to\game.rvz RVZ_REAL_SHA1=<expected> dotnet test tests/RVZSharp.Tests
```

## Status

RVZ **and** the legacy disc formats (WIA, GCZ, CISO/WBI, WBFS, TGC, NFS) are decoded
byte-for-byte and covered by tests; the CLI `info`/`decode` commands accept any of them
(auto-detected by magic). The RVZ writer (`rvzsharp convert`) encodes any of them back to
RVZ (Zstd/Bzip2/LZMA1/LZMA2/None, optional packing, 32 KiB–2 MiB chunks), with the same
SHA-1s Dolphin produces. Remaining work: validation against real game images (see
`docs/roadmap.md`).
