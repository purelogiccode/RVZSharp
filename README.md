# RVZSharp

A pure C# (.NET 10) library and CLI for decoding **Dolphin RVZ** disc images (GameCube/Wii).
RVZ is the successor of the WIA format; RVZSharp decodes RVZ files back to the original
disc image (`.iso`) **byte-for-byte**, including:

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
dotnet run --project src/RVZSharp.Cli -- info <file.rvz>
dotnet run --project src/RVZSharp.Cli -- decode <file.rvz> out.iso [--sha1 <expected-hex>]
```

## Project layout

- `src/RVZSharp` — the library: `Format` (container structs), `Io` (big-endian reading,
  section streams), `Compression` (codecs), `Chunks` (group decoding, exception lists),
  `Packing` (RVZ packing + PRNG), `Wii` (hash tree + region rebuild), `RvzReader`.
- `src/RVZSharp.Cli` — the `info`/`decode` tool.
- `tests/RVZSharp.Tests` — 120 tests: unit (headers, tables, codecs, PRNG, packing,
  exceptions, region rebuild) and end-to-end round-trips of synthetic RVZ files built by
  `TestRvzBuilder` (a minimal writer following Dolphin's converter rules).

## Real-world validation

Point the optional test at a real `.rvz` file to verify against its known ISO SHA-1
(e.g. from the No-Intro datfiles in `References/rvz-1.0.3/testdata/*.dat`):

```
RVZ_REAL_FILE=C:\path\to\game.rvz RVZ_REAL_SHA1=<expected> dotnet test tests/RVZSharp.Tests
```

## Status

Decoding is complete and covered by tests. Encoding (RVZ writing) is the next milestone;
the codec abstraction and the test builder are designed to support it.
