# Library API

All types live in the `RVZSharp` assembly (namespace `RVZSharp` and sub-namespaces).

## Overview

| Namespace | Purpose |
|---|---|
| `RVZSharp.Blobs` | `IBlobReader`, `Blob` (factory), `BlobType` |
| `RVZSharp` | `RvzReader`, `RvzWriter`, `RvzWriteOptions` |
| `RVZSharp.Format` | container structs: `WiaFileHead`, `WiaDisc`, `WiaPartEntry`, `WiaRawDataEntry`, `GroupEntry`, `CompressionType` |
| `RVZSharp.Chunks` | group decoding (`ChunkDecoder`), `HashExceptionEntry` |
| `RVZSharp.Compression` | codecs (read side) and encoders (write side) |
| `RVZSharp.Packing` | RVZ packing: `RvzPackingDecoder`, `RvzPackingEncoder`, `LaggedFibonacciGenerator` |
| `RVZSharp.Wii` | Wii partition machinery: `PartitionRegionBuilder`, `WiiHashCalculator`, `WiiVolume`, `WiiPartitionExtractor` |

## The blob abstraction

Every disc format is exposed through one interface:

```csharp
public interface IBlobReader : IDisposable
{
    BlobType Type { get; }
    long Length { get; }          // decoded ISO length in bytes
    int BlockSize { get; }        // natural block size of the format
    int ReadAt(long position, Span<byte> buffer);  // returns bytes read
}
```

Open a file with auto-detection:

```csharp
using var file = File.OpenRead("game.gcz");
using IBlobReader blob = Blob.Open(file, filePath: "game.gcz", leaveOpen: true);
Console.WriteLine($"{blob.Length} bytes, {Blob.GetName(blob.Type)}");
```

- `Blob.Open(Stream, string? filePath, bool leaveOpen)` — detects the format from the magic
  bytes. `filePath` is used by format-specific checks (e.g. NFS requires a `content`
  directory and a sibling `code/htk.bin`).
- `Blob.Open(Stream, ReadOnlySpan<byte> nfsKey, bool leaveOpen)` — opens an NFS stream with
  an explicit AES key, bypassing the on-disk key lookup.
- `BlobType` values: `Rvz`, `Wia`, `Gcz`, `Ciso`, `Wbfs`, `Tgc`, `Nfs`, `Plain`, `Unknown`.

## Reading RVZ / WIA

```csharp
using var file = File.OpenRead("game.rvz");
using var reader = RvzReader.Open(file, leaveOpen: true);   // or OpenWia(...) for WIA

Console.WriteLine(reader.Length);          // ISO size
Console.WriteLine(reader.Disc.ChunkSize);
Console.WriteLine(reader.Partitions.Length);

byte[] iso = reader.ReadFully();           // the whole disc as ISO bytes
Span<byte> sector = stackalloc byte[0x8000];
reader.ReadAt(0x1234, sector);             // random access
```

Properties:

| Member | Meaning |
|---|---|
| `FileHead` | the 0x48-byte file head (magic, versions, hashes, sizes) |
| `Disc` | the 0xDC-byte disc struct (type, compression, chunk size, tables) |
| `Partitions` | parsed Wii partition entries (key + two data ranges) |
| `RawDataEntries` | raw data ranges |
| `GroupEntries` | group table (offsets, sizes, packed sizes) |
| `IsWia` | whether the container is WIA |
| `BlockSize` | the chunk size |
| `Length` | decoded ISO length |

`ReadAt` throws `RvzFormatException` if a region of the disc is not covered by the file
(truncated container).

## Writing RVZ

```csharp
using var input = File.OpenRead("game.iso");
using var blob = Blob.Open(input, filePath: "game.iso", leaveOpen: true);
using var output = File.Create("game.rvz");

var options = new RvzWriteOptions
{
    Compression = CompressionType.Zstd,
    CompressionLevel = 5,
    ChunkSize = (int)WiaDisc.GroupSize,   // 2 MiB
    Packing = true,
};

RvzWriter.Write(blob, output, options);
```

`RvzWriteOptions`:

| Member | Default | Notes |
|---|---|---|
| `Compression` | `Zstd` | `None`, `Bzip2`, `Lzma`, `Lzma2`, `Zstd`. `Purge` throws `RvzUnsupportedException` (WIA-only method). |
| `CompressionLevel` | `3` | 1–9 (Zstd allows up to 22). |
| `ChunkSize` | `0x200000` | Power of two, 32 KiB–2 MiB. |
| `Packing` | `true` | Enable PRNG-junk detection/packing. |

The writer owns the output stream's position but does **not** dispose it.

## Compression API

Read side (decoders) — used internally by `RvzReader`:

```csharp
ICompressionDecoder decoder = CompressionCodecFactory.Create(CompressionType.Lzma2);
using Stream decompressor = decoder.CreateDecompressor(file, props, inputSize, outputSize);
```

Write side (encoders) — used internally by `RvzWriter`:

```csharp
var (encoder, props) = CompressionEncoderFactory.Create(CompressionType.Zstd, level: 3);
byte[] compressed = encoder.Compress(payload);
```

`ICompressionEncoder` also exposes `AddPrecedingData(...)`, which PURGE uses to fold the
exception lists into its SHA-1 trailer.

## Packing API

```csharp
// Encode a chunk's segments (junk detection + seed recovery):
var mainData = new List<byte>();
uint packedSize = 0;
RvzPackingEncoder.Pack(payload, dataOffset: 0, bytesPerChunk: payload.Length, chunks: 1,
    allowJunkReuse: true, compression: true, mainData, ref packedSize);

// Decode a packed stream:
using var decoder = new RvzPackingDecoder(stream, dataOffset: 0);
decoder.Read(buffer, 0, buffer.Length);
```

`LaggedFibonacciGenerator` is public for advanced use:

```csharp
var (seed, bytesReconstructed) = LaggedFibonacciGenerator.GetSeed(data, size, dataOffset % 0x8000);
var lfg = new LaggedFibonacciGenerator();
lfg.SetSeed(seed);
lfg.ForwardBytes(offset % 0x8000);
lfg.GetBytes(count, output);
```

## Wii partition machinery

- `PartitionRegionBuilder(key)` — rebuilds one 2 MiB region (64 sectors) of encrypted
  partition data from decrypted payload + hash exceptions; used by the reader. `Finish()`
  returns the encrypted 2 MiB block.
- `WiiHashCalculator` — SHA-1 hash-tree construction (`h0`/`h1`/`h2`) and exception
  application.
- `WiiVolume` — disc-header parsing, partition-table discovery, ticket reading
  (title key), FST fields.
- `WiiPartitionExtractor` — writer side: reads encrypted regions from the input, decrypts
  them, and diffs the recalculated hash tree against the original to produce
  `HashExceptionEntry` lists.

See [Wii partitions](format/wii-partitions.md) for the underlying format.

## Errors

| Exception | Raised when |
|---|---|
| `RvzFormatException` | structural problems: truncated file, bad chunk size, unsupported method, missing disc coverage |
| `RvzHashMismatchException` | a SHA-1 (file head, disc struct, partition table, raw/group tables) does not match |
| `RvzUnsupportedException` | a feature the container needs is not supported (e.g. PURGE inside RVZ) |

All three derive from `RvzSharpException` (which derives from `Exception`).
