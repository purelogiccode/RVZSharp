# Testing

The suite has **221 tests** and runs in ~30 seconds:

```bash
dotnet test CSharp_RVZSharp.slnx -c Release
```

## Strategy

The project has no real game images (testdata files are Git-LFS pointers), so every test
runs against **synthetic discs** built in memory, plus cross-checks against the reference
implementations' semantics:

1. **Synthetic builders** generate byte-exact images (RVZ/WIA, all legacy formats, and
   realistic Wii ISOs with tickets, partition tables and encrypted data).
2. **Round trips** prove byte-exactness: build → write → read → compare.
3. **Format semantics** were validated against Dolphin's C++ (`References/dolphin-master`)
   and the Go reader (`References/rvz-1.0.3`) — including a Python prototype used during
   development to pin down the PRNG seed-recovery algorithm before the C# port.

## Test files

| File | Covers |
|---|---|
| `WiaFileHeadTests`, `WiaDiscTests`, `TableParserTests` | container structs, tables, hash validation |
| `CompressionCodecTests`, `CompressionLzmaTests` | every codec round trip, props, LZMA1/LZMA2 framing |
| `ChunkDecoderTests`, `ChunkDecoderPackingTests` | group decoding, exception lists, packed chunks, every codec |
| `PackingTests` | segment streams, mixed literal/junk, skip semantics |
| `LaggedFibonacciGeneratorTests` | `GetSeed` at 11 offsets (incl. unaligned), random-data rejection, PRNG equivalence |
| `RvzPackingEncoderTests` | pack → decode round trips, literal shortcut, zero-junk header |
| `PartitionRegionBuilderTests` | hash tree, encryption, exceptions |
| `BlobDetectionTests` | magic-byte auto-detection |
| `GczBlobTests`, `CisoBlobTests`, `WbfsBlobTests`, `TgcBlobTests`, `NfsBlobTests` | legacy decoders |
| `WiaReaderTests`, `RvzReaderTests`, `RvzReaderMatrixTests` | full-container decoding across codecs/chunk sizes |
| `RvzWriterTests` | writer round trips: GC + Wii (FST split, corrupted hashes, small chunks), legacy → RVZ → ISO, zero-image, junk-only image |

## Synthetic builders (`tests/RVZSharp.Tests/Helpers/`)

| Helper | Purpose |
|---|---|
| `TestRvzBuilder` | RVZ/WIA file builder (chunks, codecs, packing, partitions, exceptions) |
| `TestLegacyBuilders` | GCZ, CISO, WBFS, TGC, NFS builders |
| `TestWiiIsoBuilder` | realistic Wii ISO: disc header, partition table, RSA2048 ticket, encrypted partition data |
| `ReferencePrng` | the junk PRNG used to generate padding in tests (matches the reader's semantics) |
| `TestCompressor` | reference encoders (deflate, bzip2, LZMA1/LZMA2, Zstd, Purge) |

## Key round-trip matrix

`RvzWriterTests` converts synthetic discs to RVZ and decodes them back, byte-for-byte:

- **Formats**: plain ISO; legacy GCZ / TGC / NFS / WIA / CISO (WBFS omitted — its fixed
  9.4 GiB logical size makes a full round trip impractical).
- **Compression**: None, Zstd, Bzip2, LZMA, LZMA2.
- **Packing**: on and off.
- **Discs**: GameCube (random + zero + junk regions), Wii with corrupted hash areas
  (forcing exceptions), Wii with an FST split, Wii with junk inside partition data,
  Wii with small chunk sizes (exception splitting), all-zero ISO.
- **Chunk sizes**: 2 MiB default, 32 KiB / 64 KiB small chunks.

## Gotchas encoded in tests

- WBFS `wlba` entries are **u16 BE** — tests use real 2 MiB clusters so indices never
  overflow.
- NFS only opens from a `content` directory with `code/htk.bin` present — tests set that up.
- The junk PRNG's stream position is `offset % 0x8000`; test junk is generated at the
  offset where it will be placed, including unaligned offsets.
- Exception offsets in files are chunk-relative; the small-chunk tests pin the reader's
  `additional_offset` conversion.
