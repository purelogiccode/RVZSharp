# Architecture

```
┌────────────────────────────────────────────────────────────────────┐
│                          CLI (RVZSharp.Cli)                        │
│            header │ verify │ convert │ extract  —  Blob.Open     │
└───────────────┬────────────────────────────────────────────────────┘
                │ IBlobReader
┌───────────────▼────────────────────────────────────────────────────┐
│                         Library (RVZSharp)                         │
│                                                                     │
│  Blobs ── RvzReader ── Chunks/Compression/Packing/Wii  (read path)  │
│  Blobs ── RvzWriter/WiaWriter ── WiaRvzWriter core     (write path) │
│  Blobs ── GczWriter / CisoWriter / WbfsWriter / TgcWriter (writers) │
│  Blobs ── DiscHasher / DiscVerifier ── Wii hash trees   (verify)    │
└────────────────────────────────────────────────────────────────────┘
```

## Module map

| Module | Files | Responsibility |
|---|---|---|
| `Blobs/` | `Blob`, `BlobType`, `IBlobReader`, `PlainBlob`, `GczBlob`, `CisoBlob`, `WbfsBlob`, `TgcBlob`, `NfsBlob` | Format detection and per-format random-access decoding to ISO bytes |
| `Models/` | `WiaFileHead`, `WiaDisc`, `WiaPartEntry`, `WiaRawDataEntry`, `GroupEntry`, `HashExceptionEntry`, `DiscHashes`, `WiaRvzFormat`, `CompressionType` | Container structs (RVZ/WIA) |
| `Chunks/` | `ChunkDecoder`, `TableParser` | Group decompression, exception-list parsing, table loading |
| `Compression/` | `CompressionCodecFactory`, `CompressionEncoderFactory`, `ICompressionDecoder`, `ICompressionEncoder`, codecs, `Lzma/` (vendored 7-Zip decoder) | Read-side decompression and write-side compression |
| `Packing/` | `RvzPackingDecoder`, `RvzPackingEncoder`, `LaggedFibonacciGenerator`, `LaggedFibonacciPrng` | RVZ junk packing: segment streams and PRNG seed recovery |
| `Wii/` | `PartitionRegionBuilder`, `WiiHashCalculator`, `WiiVolume`, `WiiPartitionExtractor`, `PartitionReader` | Wii partition encryption, hash tree, exceptions, decrypted partition views |
| `Files/` | `DiscFileSystem`, `DiscFileInfo` | GameCube/Wii FST parsing, case-insensitive lookup, file data streaming |
| `Verification/` | `DiscVerifier` | Dolphin VolumeVerifier equivalent: partition headers, TMD/H3 tables and the h0/h1/h2/h3 hash tree walk |

## Read path

```
IBlobReader (any format)          canonical ISO bytes
        │
        ▼
RvzReader.Open                    validates file head + disc struct hashes,
                                  loads partition/raw/group tables (hash-checked)
        │
        ▼
ReadAt(position)                  finds the data area covering `position`
        │
        ├─ raw area ──► group by chunk ──► decompress ──► unpack segments (if packed)
        │                                                         │
        └─ partition area ─► chunk payload (decrypted data)        │
                             + hash exceptions                     │
                             │                                     │
                             ▼                                     ▼
                     PartitionRegionBuilder                RvzPackingDecoder
                     (recompute h0/h1/h2, apply            (LFG PRNG, seed +
                     exceptions, AES-128-CBC               skip offset % 0x8000)
                     re-encrypt, zero-fill tail)
```

Key points:

- Raw areas are chunked by `chunk_size`; partition areas by 2 MiB regions (64 sectors).
- Groups carry `rvz_packed_size`: `0` means "no packing headers", anything else means the
  group data starts with a segment stream (see
  [Compression & packing](format/compression-packing.md)).
- The reader keeps small caches (last raw chunk, last partition region) — cache keys include
  the segment index so split partitions cannot collide.
- Exception offsets are stored **chunk-relative**; the reader adds the chunk's offset
  within its 2 MiB region before matching them to sectors.

## Write path

```
IBlobReader (any format)                    Stream (RVZ/WIA out)
        │                                        ▲
        ▼                                        │
WiiVolume detection ──► data areas in disc order │
   (Wii magic, 0x60/0x61 flags, partition table) │
        │                                        │
        ├─ raw area ──► chunk payload            │
        │              │                         │
        │              ▼                         │
        │        RvzPackingEncoder (junk scan,   │
        │        GetSeed, segment stream; RVZ)   │
        │              │                         │
        ├─ partition area ─► WiiPartitionExtractor
        │   (decrypt region, diff hash tree ──► exceptions,
        │    split/merge into chunks; one exception list
        │    per 2 MiB region a chunk covers)
        │              │
        │              ▼
        │        pack (RVZ) + compress each group
        │        (zero group when all-zero and no exceptions;
        │         WIA always stores the codec output)
        │              │
        ▼              ▼
tables (partition = plain, raw + group = compressed;
        RVZ 12-byte / WIA 8-byte group entries)
layout iteration until the group-table size converges
        │
        ▼
file head (SHA-1 over disc struct, sizes, head hash)
```

Key points:

- Partitions are split at the **FST end** (aligned up to 2 MiB): the area before it and the
  area after it become two data entries, matching Dolphin's `ConvertToWIAOrRVZ`.
- The first raw area starts at `0x80` (the disc header is stored in the disc struct's
  `disc_header` field) and is **read from sector-aligned offset 0**; the raw table's group
  count covers the grown read size so Dolphin-style readers that trust `number_of_groups`
  see a consistent table.
- Groups whose payload is all zeroes (and that have no exceptions) become **zero groups**
  (stored size 0) — this is what makes mostly-empty discs compress to kilobytes.
- The group table's compressed size depends on the offsets inside it, so the writer
  iterates the layout until the table size converges (typically 2 iterations), then emits
  the file with all offsets aligned to 4 bytes.
- `GczWriter` is independent of this pipeline (Dolphin: `ConvertToGCZ`): the image is read
  in power-of-two blocks, each block is deflated (stored raw when deflate does not fit or
  saves fewer than 10 bytes), the per-block Adler-32 covers the stored bytes, and the
  header/block tables are written last through a backward seek.
- `CisoWriter`, `WbfsWriter` and `TgcWriter` are independent too: CISO writes only non-zero
  blocks and fills the presence map with a backward seek; WBFS maps each disc cluster to a
  volume cluster and points every all-zero cluster at one shared zero cluster; TGC copies
  the GameCube ISO verbatim after a header carrying the relocated DOL/FST offsets.

## Verification

`DiscVerifier.Verify` (Dolphin: `VolumeVerifier`) parses each partition header, validates
the TMD structure and the H3 table against the TMD content hash, then walks every data
sector: it decrypts the hash area (zero IV) and data (IV = ciphertext at 0x3D0), recomputes
h0 from the 31 data blocks, compares the embedded h1/h2 slots and the h3 entry, and reports
every mismatch with a severity. GameCube discs have no hash trees and are reported valid.

## Design decisions

| Decision | Rationale |
|---|---|
| Dolphin C++ is the layout truth | `References/dolphin-master/` — the RVZ/WIA formats were invented there; the Go reader and `docs/WiaAndRvz.md` are cross-checks |
| Canonical ISO view for all formats | one consumer (CLI, writer) works for every container |
| GCZ uses BCL `ZLibStream` (read) and SharpZipLib `Deflater` (write) | GCZ is deflate; SharpZipLib is already a dependency for bzip2, and `Deflater` can be reset per block like Dolphin's `deflateReset` |
| Parallel decode serializes only file reads | RVZ/WIA chunks and partition regions are independent; workers read stored bytes under a lock (short I/O) and decompress from memory in parallel, then write in disc order — byte-identical output, near-linear speedup for LZMA/LZMA2 |
| FST offsets are read from the decrypted view | Dolphin's `GetFSTOffset` is a partition-relative (decrypted) read; `PartitionReader` provides that view for the parser, the writer's split point and `extract` |
| LZMA decoder is vendored 7-Zip | compact, self-contained, no external native code |
| LZMA-SDK for encoding | pure-managed public-domain encoder (runtime dependency, version 22.1.1) |
| Writer stores partitions decrypted + exceptions | the defining RVZ space optimization; identical to Dolphin |
| Junk packing is best-effort | `GetSeed` fails cleanly on non-PRNG data and the writer falls back to literal bytes — output stays valid |
| Chunk sizes per Dolphin | powers of two from 32 KiB to 2 MiB, or multiples of 2 MiB above that |
| PURGE only in WIA output | PURGE is a WIA-only method (`WiaWriter`); RVZ output rejects it and RVZ readers reject PURGE containers |

## Format-version handling

`WiaFileHead` carries `version` (written `0x01000000`) and `version_compatible`
(`0x00030000` for RVZ, `0x00080000` for WIA). The reader accepts a file when
`ImplementedVersion >= VersionCompatible`, so newer readers can open older files and
vice versa.
