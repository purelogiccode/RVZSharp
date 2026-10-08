# Testing

The test suite is split into **two projects**, so the default run is always the fast one:

- **`RVZSharp.Tests`** — **468 synthetic tests** (unit + end-to-end round trips), ~1
  minute per framework (`net8.0`, `net9.0`, `net10.0`). It is part of the solution.
- **`RVZSharp.Slow.Tests`** — **97 real-file tests** (full decode, structural checks,
  writer round trips against real game images), ~12 minutes when the games are mounted.
  It is deliberately kept **out of the solution**, so a plain `dotnet test` / solution run
  never executes it.

Throughput measurements live in **`RVZSharp.Benchmarks`** (BenchmarkDotNet; see
[Benchmarks](#benchmarks) below).

```bash
# fast suite (default; runs per target framework of the solution)
dotnet test CSharp_RVZSharp.sln -c Release

# a single class
dotnet test CSharp_RVZSharp.sln -c Release --filter "FullyQualifiedName~RvzWriterTests"

# slow / real-file suite (explicit opt-in; not part of the solution)
dotnet test RVZSharp.Slow.Tests -c Release

# a single framework (either project)
dotnet test RVZSharp.Slow.Tests -c Release --framework net8.0
```

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
4. **Reference-alignment regressions** (2025 audit): every finding from the
   comparison against Dolphin/Go is pinned by a test — LZMA1 end markers, raw-table group
   counts, TGC/WBFS magic offsets, >2 MiB chunk exception lists, zero-fill hash trees,
   overlapping-window hash exceptions, overlap/ordering validation, empty-table hashes,
   decompressed-size probes, split WBFS, scrubbing, truncated packing headers, and the CLI
   option surface.
5. **Mutation/fuzz robustness** (`ParserRobustnessTests`): every container is truncated,
   bit-flipped, extended and size-patched (80 mutations per format, seeded) and fed through
   `Blob.Open` + reads; only `RvzException` subclasses may escape. Set
   `RVZSHARP_FUZZ_ITERATIONS=2000` for a deeper local pass (CI keeps the default).
6. **Concurrency** (`ConcurrentReadTests`): parallel random `ReadAt` calls on GC/Wii
   containers (mixed with `ReadFully`) must match the reference ISO byte-for-byte.
7. **Continuous integration** (`.github/workflows/ci.yml`): builds the solution and runs the
   fast suite on `net8.0`/`net9.0`/`net10.0` with coverage uploaded as an artifact, packs
   the library (API-compat against 1.0.0 + embedded SBOM) and publishes a smoke-tested
   ReadyToRun CLI. The slow suite and differential tests stay opt-in on developer machines.
8. **Post-1.0.1 correctness review**: a commit-by-commit audit pinned the fixes below with
   regression tests — retail ticket title keys are common-key decrypted
   (`WiiVolumeTests`; the synthetic Wii builders now store encrypted keys like real discs),
   the WBFS header declares the size relative to the stream position and carries the
   disc-header copy (`WbfsWriterTests`), extract reads TMD/cert/H3 at partition-relative
   offsets and skips H3 for hashless discs, `convert -f iso --json` keeps stdout
   JSON-only, `convert --verify` honors Ctrl+C (exit 130), writer progress is monotonic
   (`ProgressReader`), and parallel decode/encode failures surface the original exception
   instead of `AggregateException` (`ParallelExecution`).

## Test files

| File | Covers |
|---|---|
| `WiaFileHeadTests`, `WiaDiscTests`, `TableParserTests` | container structs, tables, hash validation |
| `CompressionCodecTests`, `CompressionLzmaTests` | every codec round trip, props, LZMA1/LZMA2 framing |
| `ExceptionListParserTests` | exception-list parsing: multiple lists, 4-byte alignment of the last list, truncation errors |
| `ChunkDecoderTests`, `ChunkDecoderPackingTests` | group decoding, exception lists, packed chunks, every codec |
| `PackingTests` | segment streams, mixed literal/junk, skip semantics |
| `LaggedFibonacciGeneratorTests` | `GetSeed` at 11 offsets (incl. unaligned), random-data rejection, PRNG equivalence |
| `RvzPackingEncoderTests` | pack → decode round trips, literal shortcut, zero-junk header |
| `PartitionRegionBuilderTests` | hash tree, encryption, exceptions |
| `WiiHashCalculatorTests`, `WiiPartitionExtractorTests` | h0/h1/h2 layout, exception application, AES region round trips and corruption detection |
| `ScrubbedBlobTests`, `PlainBlobTests` | scrubbing of non-game partitions, plain-ISO reads and ownership |
| `BlobDetectionTests` | magic-byte auto-detection |
| `Adler32Tests`, `SpanReader`/`SectionStreamTests`, `NonDisposingStreamTests` | checksums, big-endian reads, section bounds, stream ownership |
| `GczBlobTests`, `CisoBlobTests`, `WbfsBlobTests`, `TgcBlobTests`, `NfsBlobTests` | legacy decoders |
| `WiaReaderTests`, `RvzReaderTests`, `RvzReaderMatrixTests` | full-container decoding across codecs/chunk sizes |
| `RvzWriterTests` | writer round trips: GC + Wii (FST split, corrupted hashes, small chunks), legacy → RVZ → ISO, zero-image, junk-only image, >2 MiB chunks, overlapping/odd partitions, scrubbing, raw-table group counts, `MaxThreads` determinism |
| `WiaWriterTests` | WIA round trips across all five codecs (GC + Wii with hash exceptions), 4/6 MiB chunks, magic/version, PURGE, option validation, `MaxThreads` determinism |
| `GczWriterTests` | GCZ round trips (GC + Wii), last-block zero padding, header fields, raw/compressed block storage, `MaxThreads` determinism, option validation |
| `CisoWriterTests` | CISO round trips (GC + Wii), presence map, absent all-zero blocks, scrub shrinking, header fields, option validation |
| `WbfsWriterTests` | WBFS round trips (Wii), shared zero cluster, header fields, disc-header copy, non-zero stream position, scrub, cluster-size/map limits, option validation |
| `WiiVolumeTests` | ticket title-key decryption (common key, IV = title ID) and partition discovery returning plaintext keys |
| `TgcWriterTests` | TGC round trips (GC), DOL/FST header fields and relocation, random access, GameCube-only validation |
| `DiscVerifierTests` | Wii hash-tree verification: valid unencrypted + encrypted partitions, corrupt data/hash areas, H3/TMD mismatches, truncation, GameCube/non-disc reports |
| `ParallelDecodeTests` | parallel `CopyTo` equals sequential for GC/Wii/WIA (multi-batch, multi-region chunks, exceptions), progress, cancellation, non-RVZ fallback |
| `ConcurrentReadTests` | thread-safe `ReadAt`: parallel random reads on GC/Wii match the reference ISO; concurrent `ReadFully` + `ReadAt` |
| `LruCacheTests` | decoded-unit LRU: hits, eviction, recency refresh, oversized values, concurrent misses |
| `ParserRobustnessTests` | mutation/fuzz: corrupt containers and magic-prefixed garbage fail only with `RvzException`; hostile table counts are capped |
| `DiscInfoTests` | disc metadata: game/maker ID, revision, name, region, country, title ID, fallbacks |
| `ScrubOptionTests` | `RvzWriteOptions.Scrub` zeroes non-game partitions while keeping the game partition byte-exact |
| `AsyncApiTests` | `ReadFullyAsync`/`CopyToAsync`/`WriteAsync` equal their synchronous forms, cancellation |
| `DiscFileSystemTests` | FST parsing (GC + decrypted Wii partitions), case-insensitive lookup, file reads, `PartitionReader` decryption, invalid FST rejection |
| `RVZSharp.Slow.Tests/RealRvzFileTests.cs` | 97 real-file tests (see below) |
| `RVZSharp.Slow.Tests/RealFileDecodeTests.cs` | env-var-driven real-file decode (`RVZ_REAL_FILE`/`RVZ_REAL_SHA1`) |
| `RVZSharp.Slow.Tests/RealLegacyFileTests.cs` | real GCZ/CISO/WBFS/TGC/WIA/NFS decode to their expected SHA-1 (`RVZ_REAL_GCZ` … `RVZ_REAL_NFS`/`RVZ_REAL_NFS_KEY`) |
| `RVZSharp.Slow.Tests/DifferentialToolTests.cs` | optional cross-checks against `dolphin-tool`/`wit`/`wwt` (`RVZ_DOLPHIN_TOOL`/`RVZ_WIT`/`RVZ_WWT` + `RVZ_DIFF_ISO`) |

## Real-file suite

`RVZSharp.Slow.Tests/RealRvzFileTests.cs` validates the library against actual game
images on a local drive
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

The 1.0.1 review also fixed the plain-ISO side of that story: the ticket's title key is
AES-CBC encrypted with the Wii common key on retail discs, and `WiiVolume.GetPartitions`
now decrypts it (Dolphin: `TicketReader::GetTitleKey`) instead of using the raw ciphertext,
so `DiscVerifier`/`verify --partitions`, `DiscFileSystem`/`extract` and RVZ writing work on
real encrypted images. The synthetic builders (`TestWiiIsoBuilder.WriteTicketKey`) store
common-key-encrypted keys so the fast suite exercises the same path.

Legacy-format real files are covered by `RealLegacyFileTests` through environment variables
(`RVZ_REAL_GCZ`, `RVZ_REAL_CISO`, `RVZ_REAL_WBFS`, `RVZ_REAL_TGC`, `RVZ_REAL_WIA`, and
`RVZ_REAL_NFS` + `RVZ_REAL_NFS_KEY`; each with an optional `<VAR>_SHA1` expectation), and
`DifferentialToolTests` optionally cross-checks our writers/reader against `dolphin-tool`
(RVZ, both directions) and `wit`/`wwt` (our CISO/TGC output) when those tools are installed
and `RVZ_DIFF_ISO` points at a real ISO. All of them no-op when unset.

## Synthetic builders (`RVZSharp.Tests/Helpers/`)

| Helper | Purpose |
|---|---|
| `TestRvzBuilder` | RVZ/WIA file builder (chunks, codecs, packing, partitions, exceptions) |
| `TestLegacyBuilders` | GCZ, CISO, WBFS, TGC, NFS builders |
| `TestWiiIsoBuilder` | realistic Wii ISO: disc header, partition table, RSA2048 ticket (title key common-key encrypted, like retail discs), encrypted partition data |
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

## Benchmarks

`RVZSharp.Benchmarks` (BenchmarkDotNet, net10.0) measures encode and decode throughput on a
16 MiB synthetic GameCube image (half random data, a quarter zeroes):

| Class | What it measures |
|---|---|
| `EncodeBenchmarks` | RVZ per codec (Zstd/LZMA2/Bzip2/None, packing on/off), WIA LZMA2, GCZ deflate |
| `DecodeBenchmarks` | full-image decode (one `DiscHasher` pass) for RVZ Zstd/LZMA2, WIA LZMA2 and GCZ |
| `ThreadScalingBenchmarks` | RVZ Zstd at `MaxThreads` 1/2/4/8/0 (processor count) |

```bash
# everything (takes a while — BenchmarkDotNet runs many iterations)
dotnet run -c Release --project RVZSharp.Benchmarks

# one class or method
dotnet run -c Release --project RVZSharp.Benchmarks -- --filter '*Encode*'

# list without running
dotnet run -c Release --project RVZSharp.Benchmarks -- --list flat
```

Results land in `BenchmarkDotNet.Artifacts/` (git-ignored). Always use `-c Release`:
Debug numbers are meaningless for codecs. The benchmark project is in the solution (so it
keeps compiling) but is not a test project — `dotnet test` ignores it.
