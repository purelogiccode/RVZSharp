# Testing

The suite has **351 tests** and runs on every target framework (`net8.0`, `net9.0`,
`net10.0`). Tests are categorized with the xUnit trait `Category`:

- **fast** — 254 synthetic tests (~30 seconds per framework), tagged implicitly by *not*
  being marked slow;
- **slow** — 97 real-file tests (full decode, structural checks, writer round trips against
  real game images), tagged `[Trait(TestCategories.Category, TestCategories.Slow)]` (see
  `TestCategories.cs`).

```bash
# full suite
dotnet test CSharp_RVZSharp.slnx -c Release

# fast subset only (skips the real-file suite, ~30 s per framework)
dotnet test CSharp_RVZSharp.slnx -c Release --filter "Category!=Slow"

# slow / real-file subset only
dotnet test CSharp_RVZSharp.slnx -c Release --filter "Category=Slow"
```

A single framework can be selected with `--framework net8.0` (etc.).

## Strategy

The suite runs against **synthetic discs** built in memory (cross-checked against the
reference implementations' semantics) **and**, when a local library of real game images is
mounted (`F:\Nintendo GameCube`, `F:\Nintendo Wii`), against **real RVZ files** validated
byte-for-byte against their official No-Intro SHA-1s:

1. **Synthetic builders** generate byte-exact images (RVZ/WIA, all legacy formats, and
   realistic Wii ISOs with tickets, partition tables and encrypted data).
2. **Round trips** prove byte-exactness: build → write → read → compare.
3. **Format semantics** were validated against Dolphin's C++ (`References/dolphin-master`)
   and the Go reader (`References/rvz-1.0.3`) — including a Python prototype used during
   development to pin down the PRNG seed-recovery algorithm before the C# port.
4. **Reference-alignment regressions** (2025 audit, see TODO.md): every finding from the
   comparison against Dolphin/Go is pinned by a test — LZMA1 end markers, raw-table group
   counts, TGC/WBFS magic offsets, >2 MiB chunk exception lists, zero-fill hash trees,
   overlapping-window hash exceptions, overlap/ordering validation, empty-table hashes,
   decompressed-size probes, split WBFS, scrubbing, truncated packing headers, and the CLI
   option surface.

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
| `WiaReaderTests`, `RvzReaderTests`, `RvzReaderMatrixTests`, `RealFileDecodeTests` | full-container decoding across codecs/chunk sizes; env-var-driven real-file decode (`RVZ_REAL_FILE`/`RVZ_REAL_SHA1`, Slow) |
| `RvzWriterTests` | writer round trips: GC + Wii (FST split, corrupted hashes, small chunks), legacy → RVZ → ISO, zero-image, junk-only image, >2 MiB chunks, overlapping/odd partitions, scrubbing, raw-table group counts |
| `RealRvzFileTests` | 97 real-file tests (Slow): 30 full-decode SHA-1 (15 GameCube + 15 Wii vs No-Intro DAT), 30 structural, 3 region/random-access, 2 writer round-trips — self-skipping when the games aren't mounted |
| `SectionStreamTests` | section bounds under external seeks |

## Real-file suite

`RealRvzFileTests.cs` validates the library against actual game images on a local drive
(`F:\Nintendo GameCube` / `F:\Nintendo Wii`). The expected ISO SHA-1s come from the official
No-Intro DAT files in `References/rvz-1.0.3/testdata/`, so a passing test proves the decoder
reproduces the original disc image byte-for-byte:

- **30 full-decode SHA-1 tests** — 15 GameCube + 15 Wii RVZ files, decoded entirely and
  compared to their No-Intro DAT SHA-1, plus an expected-ISO-size check per file;
- **30 structural tests** — RVZ magic, version, legal chunk size, compression method,
  group-table sanity on every file;
- **3 region/random-access tests** — full-read hashing, `ReadAt` vs `ReadFully` across chunk
  boundaries, out-of-range clamping;
- **2 writer round-trips** — a real GameCube and a real Wii RVZ are re-encoded to RVZ with
  default options and decoded back to the same SHA-1.

Every test no-ops (early-returns) when its file is absent, so the suite stays green on
machines without the games. Running the real Wii round-trip against genuine images exposed
and pinned a production writer bug (default 2 MiB chunks used the ISO ticket key instead of
the RVZ partition-table key on re-signed No-Intro tickets); `RvzWriter` now prefers the
container key and falls back to the ticket key for plain ISO inputs.

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
- **Chunk sizes**: 2 MiB default, 32 KiB / 64 KiB small chunks, 6 MiB (multiple of 2 MiB).

## Gotchas encoded in tests

- WBFS `wlba` entries are **u16 BE** — tests use real 2 MiB clusters so indices never
  overflow.
- NFS only opens from a `content` directory with `code/htk.bin` present — tests set that up.
- The junk PRNG's stream position is `offset % 0x8000`; test junk is generated at the
  offset where it will be placed, including unaligned offsets.
- Exception offsets in files are chunk-relative; the small-chunk tests pin the reader's
  `additional_offset` conversion.
