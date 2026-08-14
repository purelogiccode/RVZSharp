# Library usage guide

`RVZSharp` is a pure managed library (no native code) for **.NET 8, .NET 9 and .NET 10**.
It reads and writes Dolphin **RVZ** disc images, reads **WIA** and the legacy GameCube/Wii
formats (**GCZ, CISO/WBI, WBFS, TGC, NFS**), and exposes every format through one interface
that serves the original disc bytes.

```
Install-Package RVZSharp          # Package Manager
dotnet add package RVZSharp       # .NET CLI
```

All public types live in the `RVZSharp` assembly; the main namespaces are:

| Namespace | Contents |
|---|---|
| `RVZSharp.Blobs` | `IBlobReader`, `Blob` (factory), `BlobType`, per-format readers |
| `RVZSharp` | `RvzReader`, `RvzWriter`, `RvzWriteOptions` |
| `RVZSharp.Format` | container structs: `WiaFileHead`, `WiaDisc`, `WiaPartEntry`, `GroupEntry`, `CompressionType` |
| `RVZSharp.Chunks` | `ChunkDecoder`, `HashExceptionEntry` |
| `RVZSharp.Compression` | codec factories: `CompressionCodecFactory`, `CompressionEncoderFactory` |
| `RVZSharp.Packing` | `RvzPackingDecoder`, `RvzPackingEncoder`, `LaggedFibonacciGenerator` |
| `RVZSharp.Wii` | `PartitionRegionBuilder`, `WiiHashCalculator`, `WiiVolume` |

---

## Quickstart

```csharp
using RVZSharp;
using RVZSharp.Blobs;
using RVZSharp.Format;

// 1. Open any disc image (format is auto-detected from the magic bytes).
using var blob = Blob.Open(@"C:\games\my-game.rvz");

// 2. Read the whole disc as ISO bytes.
byte[] iso = blob.ReadFully();          // fine for small images

// 3. Or convert it to RVZ with a different codec.
using var output = File.Create(@"C:\games\my-game-v2.rvz");
RvzWriter.Write(blob, output, new RvzWriteOptions
{
    Compression = CompressionType.Zstd,
    CompressionLevel = 5,
    ChunkSize = 0x200000,               // 2 MiB
});
```

That is the entire core API: open anything, read ISO bytes, write RVZ.

---

## Opening disc images

Everything starts with `Blob.Open`, which sniffs the first four bytes and returns the right
reader:

| Magic bytes | Format | Reader type |
|---|---|---|
| `52 56 5A 01` (`RVZ\x01`) | RVZ | `RvzReader` |
| `57 49 41 01` (`WIA\x01`) | WIA | `RvzReader` |
| `43 49 53 4F` (`CISO`) | CISO/WBI | `CisoBlob` |
| `01 C0 0B B1` | GCZ | `GczBlob` |
| `57 42 46 53` (`WBFS`) | WBFS | `WbfsBlob` |
| `45 47 47 53` (`EGGS`) | NFS | `NfsBlob` |
| `AE 0F 38 A2` | TGC | `TgcBlob` |
| anything else | plain ISO | `PlainBlob` |

### Overloads

```csharp
// From a path (the reader owns and closes the file stream).
using IBlobReader blob = Blob.Open(@"C:\games\game.gcz");

// From an existing stream.
using var stream = File.OpenRead(@"C:\games\game.iso");
using IBlobReader blob = Blob.Open(stream, filePath: @"C:\games\game.iso", leaveOpen: false);

// NFS with an explicit AES key (bypasses the code/htk.bin lookup).
byte[] key = ReadKeyFromSomewhere();          // 16 bytes
using IBlobReader blob = Blob.Open(@"C:\content\hif_000000.nfs", key);
```

Notes:

- `Blob.Open(Stream, ...)` requires a **seekable** stream (`ArgumentException` otherwise).
- The `filePath` argument is consulted for NFS (locating `code/htk.bin` and the
  `hif_00000X.nfs` continuation files) and for **split WBFS** images (`game.wbfs` +
  `game.wbf1…` parts are opened like Dolphin; the declared size is checked against the sum
  of the parts). NFS files must be named `hif_000000.nfs` and live in a directory named
  `content`; use the key overload to bypass the on-disk lookup.

### Scrubbing

`ScrubbedBlob.Create(blob)` wraps any disc image and zeroes the data of every non-game Wii
partition (update/channel) — the safe subset of Dolphin's `DiscScrubber` that needs no
filesystem (FST) parser. It returns `null` for discs that cannot be scrubbed (non-Wii, or
no game partition). The CLI's `convert -s/--scrub` uses it.
- `leaveOpen` controls whether disposing the reader also disposes the stream.
- Any file that is not a recognized container is opened as a **plain ISO** — opening
  arbitrary files succeeds, reads just serve the file bytes.

### Lifetime

All readers implement `IDisposable`. Dispose them to release the underlying stream; the
readers themselves hold no other unmanaged resources.

---

## The `IBlobReader` contract

```csharp
public interface IBlobReader : IDisposable
{
    BlobType Type { get; }      // detected format
    long Length { get; }        // decoded ISO size in bytes
    int BlockSize { get; }      // natural block size, 0 when the format has none
    int ReadAt(long position, Span<byte> buffer);
    byte[] ReadFully();         // default implementation (streams via ReadAt)
}
```

Every format — RVZ, GCZ, WBFS, a raw ISO — decodes to the same thing: the original disc
image bytes, randomly accessible.

```csharp
using var blob = Blob.Open(path);

// Random access: read one 0x8000-byte sector.
Span<byte> sector = stackalloc byte[0x8000];
blob.ReadAt(0x1234 * 0x8000, sector);

// Whole-image decode.
byte[] iso = blob.ReadFully();
```

- `ReadAt` returns the number of bytes actually read; it reads fewer bytes only at the end
  of the image. Reads are **not** required to be sequential.
- `BlockSize` is the format's natural block (GCZ: 0x4000, RVZ: chunk size, CISO: 0x8000,
  WBFS: cluster size, NFS: 0x8000, TGC/plain: 0).
- `ReadFully()` is a default interface method: it works on every reader and is overridden
  where a faster path exists (`RvzReader`).

### Streaming pattern (large images)

GameCube/Wii images are 0.5–9.4 GiB. To avoid holding the whole image in memory, stream it:

```csharp
using var blob = Blob.Open(path);
using var output = File.Create(@"C:\games\out.iso");
var buffer = new byte[1 << 20];
long position = 0;
while (position < blob.Length)
{
    int read = blob.ReadAt(position, buffer);
    if (read <= 0)
    {
        throw new IOException($"Decoding stopped at 0x{position:X}.");
    }

    output.Write(buffer, 0, read);
    position += read;
}
```

---

## Reading RVZ / WIA in detail

`RvzReader` exposes the container metadata plus the decoded bytes:

```csharp
using var file = File.OpenRead("game.rvz");
using var reader = RvzReader.Open(file, leaveOpen: true);   // RVZ
using var reader = RvzReader.OpenWia(file, leaveOpen: true); // WIA

reader.IsWia;              // true for WIA containers
reader.Length;             // decoded ISO size
reader.BlockSize;          // the chunk size
reader.FileHead;           // WiaFileHead: magic, versions, SHA-1s, sizes
reader.Disc;               // WiaDisc: disc type, codec, chunk size, table locations
reader.Partitions;         // WiaPartEntry[]: key + two data ranges per partition
reader.RawDataEntries;     // WiaRawDataEntry[]: raw data ranges
reader.GroupEntries;       // GroupEntry[]: group table
```

Opening **validates the whole container**: magic, versions, the file-head and disc-struct
SHA-1s, the partition/raw/group table hashes, and structural rules (chunk size, group
coverage). A damaged file throws `RvzHashMismatchException` or `RvzFormatException` at
`Open`, never later.

WIA files (read-compatible version `0x00080000`) support the PURGE codec and lack Zstd and
packing; `RvzReader` handles both formats transparently.

---

## Writing RVZ

```csharp
RvzWriter.Write(IBlobReader input, Stream output, RvzWriteOptions? options = null,
    IProgress<double>? progress = null, CancellationToken cancellationToken = default);
```

The writer mirrors Dolphin's converter:

- **Wii discs** (disc header magic + hash/encryption flags set) are stored with the
  partition optimization: partition data is written *decrypted* with SHA-1 hash exceptions,
  split at the FST end — this is what makes RVZ small.
- **GameCube discs** (and Wii discs without hashes/encryption) are stored as raw data.
- **PRNG junk** (the pseudo-random padding in Wii data) is detected and packed with a
  recovered Lagged-Fibonacci seed.
- All tables carry SHA-1 checksums; the output is fully self-describing and validated by
  any conforming reader (including Dolphin).

### `RvzWriteOptions`

| Member | Default | Notes |
|---|---|---|
| `Compression` | `Zstd` | `None`, `Bzip2`, `Lzma`, `Lzma2`, `Zstd`. `Purge` throws `RvzUnsupportedException` (WIA-only). |
| `CompressionLevel` | `3` | `Bzip2`/`Lzma`/`Lzma2`: 1–9. `Zstd`: −131072..22 (negative levels = fast modes, 0 = default). |
| `ChunkSize` | `0x200000` | Power of two between 0x8000 (32 KiB) and 0x200000 (2 MiB), or a multiple of 0x200000 above that (Dolphin's rule). |
| `Packing` | `true` | Set `false` to store junk literally (larger file, no packing overhead). |

```csharp
var options = new RvzWriteOptions
{
    Compression = CompressionType.Lzma2,   // best ratio on compressible data
    CompressionLevel = 9,
    ChunkSize = 0x10000,                   // 64 KiB chunks
    Packing = true,
};
```

The writer does **not** dispose the output stream; the caller owns it.

### Progress and cancellation

Long conversions (multi-GiB images) can be monitored and canceled:

```csharp
using var cts = new CancellationTokenSource();
var progress = new Progress<double>(fraction =>
    Console.Error.Write($"\rconverting… {fraction,6:P1}"));

try
{
    RvzWriter.Write(blob, output, options,
        progress: progress, cancellationToken: cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("\ncanceled.");
}
```

- `progress` receives a fraction in `[0, 1]` of the input bytes processed, reported
  per group read; the sequence is monotonic and ends at `1.0`.
- Cancellation is observed between reads and throws `OperationCanceledException`.

### Verifying your output

Re-open what you wrote and compare hashes:

```csharp
using var check = Blob.Open(outputPath);
using var sha1 = System.Security.Cryptography.SHA1.Create();
// stream check.ReadAt(...) through sha1 and compare with the source image's hash
```

---

## Compression codecs

Read side — decode any method found in a file:

```csharp
ICompressionDecoder decoder = CompressionCodecFactory.Create(CompressionType.Lzma2);
using Stream stream = decoder.CreateDecompressor(file, props, inputSize, outputSize);
```

Write side — compress like the writer does:

```csharp
var (encoder, props) = CompressionEncoderFactory.Create(CompressionType.Zstd, level: 5);
byte[] compressed = encoder.Compress(payload);
```

- `props` is the codec property blob stored in the disc struct's `compr_data` field
  (LZMA/LZMA2 dictionary settings; empty for the others).
- `ICompressionEncoder.AddPrecedingData(...)` is PURGE-specific (the exception lists that
  precede the compressed stream and are covered by its SHA-1 trailer).
- Supported methods: `None`, `Purge` (decode only, WIA), `Bzip2`, `LZMA`, `LZMA2`, `Zstd`.

---

## RVZ packing API

Packing is what makes RVZ files small: Wii junk is a Lagged-Fibonacci PRNG stream, so a
68-byte seed regenerates it. The writer detects junk and emits segment streams; the reader
reconstructs it.

```csharp
// Encode a chunk (returns the segment stream + its packed size).
var mainData = new List<byte>();
uint packedSize = 0;
RvzPackingEncoder.Pack(payload, dataOffset: 0, bytesPerChunk: payload.Length, chunks: 1,
    allowJunkReuse: true, compression: true, mainData, ref packedSize);

// Decode a packed stream.
using var decoder = new RvzPackingDecoder(stream, dataOffset: 0);
decoder.Read(buffer, 0, buffer.Length);
```

`LaggedFibonacciGenerator` is public for advanced use — including seed recovery:

```csharp
var (seed, bytesReconstructed) = LaggedFibonacciGenerator.GetSeed(
    data, data.Length, dataOffset % LaggedFibonacciGenerator.BlockSize);
var lfg = new LaggedFibonacciGenerator();
lfg.SetSeed(seed);
lfg.ForwardBytes(dataOffset % LaggedFibonacciGenerator.BlockSize);
lfg.GetBytes(count, output);
```

In normal use you never touch these — they are needed only when implementing a custom
container that embeds packed chunks.

---

## Wii partition machinery

These types back the partition optimization:

- `WiiVolume` — disc-header parsing, partition-table discovery, ticket reading (title key
  at ticket + 0x1BF), FST offsets, `IsWiiDisc` / `HasWiiHashes` / `HasWiiEncryption`.
- `PartitionRegionBuilder(key)` — rebuilds one encrypted 2 MiB region from decrypted
  payload + hash exceptions; used by `RvzReader.ReadAt`. `Finish()` returns the encrypted
  region bytes.
- `WiiHashCalculator` — SHA-1 hash-tree construction (`h0`/`h1`/`h2`) and hash-exception
  computation.
- `WiiPartitionExtractor` — writer side: reads encrypted regions from the input, decrypts
  them (AES-128-CBC), recomputes the hash tree, and diffs it against the original to
  produce the exception lists.

```csharp
foreach (var partition in WiiVolume.GetPartitions(blob))
{
    Console.WriteLine($"partition @ 0x{partition.Offset:X}, type {partition.Type}, "
        + $"{partition.DataSize} data bytes, key {Convert.ToHexString(partition.Key)}");
}
```

---

## Error handling

| Exception | Raised when |
|---|---|
| `RvzFormatException` | structural problems: truncated file, bad chunk size, unsupported method, missing disc coverage, decode stalls |
| `RvzHashMismatchException` | a SHA-1 (file head, disc struct, partition/raw/group tables) does not match |
| `RvzUnsupportedException` | a needed feature is not supported (e.g. PURGE inside RVZ, unknown codec) |
| `ArgumentException` | programmer errors: non-seekable stream, invalid chunk size |
| `OperationCanceledException` | `RvzWriter.Write` canceled via `CancellationToken` |

All `Rvz*Exception` types derive from `RvzException` (which derives from `Exception`), so
one catch covers the format-specific failures:

```csharp
try
{
    using var blob = Blob.Open(path);
    byte[] iso = blob.ReadFully();
}
catch (RvzException e)   // format + hash problems
{
    Console.Error.WriteLine($"The image is damaged or unsupported: {e.Message}");
}
```

Hashing is **verified eagerly**: RVZ/WIA containers validate all checksums when opened, so
a corrupt file fails fast at `Blob.Open`, not halfway through your reads.

---

## Recipes

**Convert any file to RVZ with progress, then decode back:**

```csharp
using var blob = Blob.Open(inputPath);
using var output = File.Create(outputPath);
RvzWriter.Write(blob, output, new RvzWriteOptions { Compression = CompressionType.Zstd },
    progress: new Progress<double>(p => Console.WriteLine($"{p:P1}")));
```

**Read the game title and region without decoding the disc:**

```csharp
using var blob = Blob.Open(path);
Span<byte> header = stackalloc byte[0x80];
blob.ReadAt(0, header);
string gameId = System.Text.Encoding.ASCII.GetString(header[..6]);
```

**List Wii partitions of any container:**

```csharp
using var blob = Blob.Open(path);
foreach (var partition in WiiVolume.GetPartitions(blob))
{
    Console.WriteLine($"0x{partition.Offset:X8}  type={partition.Type}  "
        + $"{partition.DataSize} bytes");
}
```

**Stream a 9.4 GiB WBFS to ISO without buffering:** use the streaming pattern above —
`WbfsBlob` reports its fixed logical size and serves zero clusters without allocating them.

---

## Thread safety

- `IBlobReader` instances are **not thread-safe**: `ReadAt` mutates internal caches
  (`RvzReader` caches the last raw chunk and partition region). Use one reader per thread,
  or synchronize access.
- `RvzWriter.Write` is single-threaded by design; progress reports are raised on the
  calling thread.

## Compatibility notes

- Target frameworks: `net8.0`, `net9.0`, `net10.0` — the same assembly API on all three
  (enforced by `EnablePackageValidation` at pack time).
- Dependencies: `LZMA-SDK` (encoding), `SharpZipLib` (BZip2), `ZstdSharp.Port`
  (Zstandard) — all pure managed, all netstandard-compatible.
- The RVZ/WIA container logic is derived from Dolphin; the library is
  **GPL-2.0-or-later**. See `LICENSE` and `THIRD-PARTY-NOTICES.md` in the package.
