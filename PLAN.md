# RVZSharp — Implementation Plan

Pure C# library to **encode and decode** Dolphin RVZ disc images, built on **.NET 10** (`net10.0`).

- **Phase 1 (DONE): full RVZ decoding** — Milestones M0–M8, 120 tests green.
- **Phase 2 (this plan): decode legacy GameCube/Wii formats** (GCZ, WIA, CISO, WBFS, TGC, NFS) to a
  canonical ISO view, so those files can later be re-encoded to RVZ.
- **Phase 3 (planned): RVZ encoding** (`RvzWriter` + `rvzsharp convert`) — consumes any decoded
  image (ISO or legacy format) and produces an RVZ file.

## 1. Goal & scope

- Parse and validate the RVZ container (headers, tables, hashes).
- Decompress group data with all 5 RVZ compression methods: NONE, BZIP2, LZMA, LZMA2, Zstandard.
- Decode the RVZ packing scheme (Lagged Fibonacci PRNG padding reconstruction).
- Rebuild Wii partition sectors (SHA-1 hash trees h0/h1/h2, hash exceptions, AES-128-CBC encryption) so the
  decoded output is **byte-identical to the original disc image (ISO)** — same as Dolphin's `Convert` / the Go `rvz` reader.
- Public API: streaming `Stream` (sequential) + random-access read (`Read`/`Seek`/`Length`) with chunk caching.
- CLI tool: `rvzsharp info <file.rvz>` and `rvzsharp decode <file.rvz> <out.iso> [--sha1 <expected>]`.

Phase 2 scope (decode legacy formats → canonical ISO view, enabling later re-encoding to RVZ):
- A generic blob abstraction (Dolphin `BlobReader` equivalent) with magic-based auto-detection.
- Decoders: **WIA** (same core as RVZ), **GCZ**, **CISO**, **WBFS**, **TGC**, **NFS**, plus
  (optional) Dolphin's split-file and directory blobs.
- Each decoder reproduces the original disc image (ISO) bytes; Wii formats keep their encrypted
  partition layout as-is (no hash/encryption work needed for legacy formats other than WIA).

Out of scope for now:
- Phase 3 RVZ encoding (planned, see §9) — not started until Phase 2 decoders are green.
- "Decrypted read" API (Dolphin `ReadWiiDecrypted`) — not needed to reproduce an ISO.

## 2. Format & reference sources (already studied)

| Source | File | What it gives us |
|---|---|---|
| Format spec | `References/dolphin-master/docs/WiaAndRvz.md` | Full WIA + RVZ layout, packing + PRNG algorithm, exception semantics |
| Dolphin reader | `References/dolphin-master/Source/Core/DiscIO/WIABlob.cpp` (RVZ=true) | Validation rules, chunk reading, exception lists, hash exceptions, AES IV scheme |
| Dolphin codecs | `References/dolphin-master/Source/Core/DiscIO/WIACompression.cpp` | LZMA props decoding, zstd/bzip2 stream setup, RVZ pack decompressor |
| Dolphin structs | `References/dolphin-master/Source/Core/DiscIO/WIABlob.h` | Struct sizes (0x48 head, 0xDC disc), version constants, enums |
| Go reader | `References/rvz-1.0.3/reader.go`, `part.go`, `raw.go` | Clean sequential-read reference incl. partition rebuild (h0/h1/h2, AES-CBC, IV at 0x3D0) |
| Go codecs | `References/rvz-1.0.3/internal/{zstd,lzma,lzma2,packed,padding}` | LZMA1 props+size-header trick, RVZ packing decode, PRNG seeding/warmup/skip |

Key facts baked into the design (all big-endian):
- File head `0x48`: magic `RVZ\x01`, version, version_compatible, disc_size, disc_hash(SHA-1), iso_file_size(u64), rvz_file_size(u64), file_head_hash.
- Disc struct (0xDC): disc_type (1=GC, 2=Wii), compression (0..5), compr_level (signed!), chunk_size, dhead[0x80], n_part, part_t_size, part_off, part_hash, n_raw_data, raw_data_off, raw_data_size (compressed size of raw table), n_groups, group_off, group_size (compressed size of group table), compr_data_len, compr_data[7].
- Partition table is **uncompressed**; raw-data table and group table are **compressed** with the disc's method+props.
- Group entry (RVZ): `data_off4` (×4), `data_size` MSB = "compressed with disc method" (0 → method NONE), low 31 bits = size (0 = all-zero group), `rvz_packed_size` (0 = not packed).
- Exception lists: partition chunks start with `u16 n_exceptions` + n × (u16 offset + 20-byte SHA-1). For NONE method, pad after last list to 4-byte boundary. Chunks < 2 MiB → 1 list per chunk; 2 MiB chunks → `chunk_size / 2MiB` lists.
- RVZ packing: 4-byte BE size; MSB set → 68-byte PRNG seed + size bytes of LF(521, 32, xor) output; else size literal bytes. PRNG skip = `offset % 0x8000` (offset relative to disc start for raw data, partition-data start for partitions).
- Wii sector rebuild (per 0x8000 sector): 31 blocks × 0x400 data at 0x400; h0 = 31 SHA-1 + 0x14 pad; h1 = 8 SHA-1 over h0 + 0x20 pad; h2 = 8 SHA-1 over h1 + 0x20 pad (all at 0x000..0x400); exceptions replace hashes before encryption; AES-128-CBC hash area with zero IV, then data area with IV = encrypted bytes at 0x3D0.

## 3. Solution layout

```
D:\Sincronizar\source\repos\CSharp_RVZSharp\
├── CSharp_RVZSharp.sln
├── Directory.Build.props          # net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors
├── .editorconfig / .gitignore / README.md / PLAN.md
├── src\
│   ├── RVZSharp\                  # class library (the deliverable)
│   │   ├── RvzReader.cs           # public entry: Open(Stream) → parsed + validated file
│   │   ├── RvzStream.cs           # Stream impl: sequential + Seek/Position, chunk cache
│   │   ├── Formats\               # WiaFileHead, WiaDisc, WiaPartEntry(+DataEntry), WiaRawDataEntry, RvzGroupEntry, HashExceptionEntry (Span-based parse)
│   │   ├── Io\                    # BigEndianReader, hashing helpers
│   │   ├── Compression\           # ICompressionCodec + None/Zstd/Bzip2/Lzma1/Lzma2 + factory (props from compr_data)
│   │   ├── Chunks\                # ChunkDecoder (decompress → exceptions → packing → payload), ExceptionListParser
│   │   ├── Packing\               # RvzPackingDecoder, LaggedFibonacciPrng
│   │   ├── Wii\                   # WiiSectorBuilder (h0/h1/h2, exceptions, AES-CBC)
│   │   └── RvzException.cs        # typed errors (bad magic, hash mismatch, unsupported…)
│   └── RVZSharp.Cli\              # console: info / decode commands, progress, optional SHA-1 check
└── tests\
    └── RVZSharp.Tests\            # xUnit
        ├── Helpers\TestRvzBuilder.cs      # in-memory RVZ writer used by integration tests
        ├── Helpers\ReferencePrng.cs       # independent PRNG implementation (mirrors Go padding) for cross-checks
        ├── Helpers\ReferenceWiiSector.cs  # independent sector-builder (mirrors Go part.go) for cross-checks
        └── *.Tests.cs
```

### Dependencies (all 100% managed, MIT/public-domain — "pure C#", no native code)

| Need | Package | Version | License |
|---|---|---|---|
| Zstandard (RFC 8878, incl. streaming decode) | `ZstdSharp.Port` | 0.8.8 | MIT |
| BZip2 | `SharpZipLib` | 1.4.2 | MIT |
| LZMA1 + LZMA2 raw streams (7-Zip props format — matches RVZ `compr_data` exactly) | `LZMA-SDK` | 22.1.1 | public domain/BSD-style |
| AES-128, SHA-1 | `System.Security.Cryptography` (BCL) | — | — |

Fallbacks if a package's API doesn't fit (checked at M3): LZMA → `Faithlife.Lzma` (also a 7-Zip SDK port); zstd → `ZstdSharp` (non-Port). All codecs sit behind `ICompressionCodec` so the choice is swappable without touching the rest of the library.

## 4. Milestones & steps (each step ends with tests green)

### M0 — Scaffold
1. `git init`, solution + 3 projects (`net10.0`, xUnit), `Directory.Build.props`, `.editorconfig`, `.gitignore`.
2. Placeholder test passes: `dotnet build` + `dotnet test` green.

### M1 — Binary I/O + file head (`0x48`)
3. `BigEndianReader` over `Stream`/`ReadOnlySpan`; SHA-1 helpers.
4. `WiaFileHead` parse + validation: magic `RVZ\x01`, version compatibility (Dolphin rule: `RVZ_VERSION >= file.version_compatible && RVZ_VERSION_READ_COMPATIBLE <= file.version`), `rvz_file_size` == stream length, `file_head_hash` over first 0x34 bytes.
   Tests: valid head; bad magic; wrong version; hash mismatch; truncated file.

### M2 — Disc struct + tables
5. `WiaDisc` parse (0xDC, `disc_size >= 0xD5` Dolphin rule) + `disc_hash` verification; validate disc_type ∈ {1,2}, compression ∈ {0,2,3,4,5} (Purge rejected for RVZ), chunk_size (≥ 32 KiB, power of two; or multiple of 2 MiB), `compr_data_len` ≤ 7 and matches method.
6. Partition table (uncompressed at `part_off`, `part_t_size ≥ 0x30`, `part_hash` over `n_part × part_t_size`); raw-data table (compressed at `raw_data_off`/`raw_data_size`) + sector-boundary alignment fixup (round offset down / grow size); group table (compressed at `group_off`/`group_size`).
   Tests: synthetic tables; each compression method for the compressed tables; truncated/oversized `part_t_size`; hash mismatch; raw-entry alignment (first raw entry 0x80/0x4FF80 → 0/0x50000).

### M3 — Compression codecs
7. `ICompressionCodec` (props bytes → streaming `Decompressor`); `NoneCodec`; `ZstdCodec` (`ZstdSharp.Port.DecompressionStream`); `Bzip2Codec` (`BZip2InputStream`); `Lzma1Codec` + `Lzma2Codec` (LZMA-SDK `Decoder.SetDecoderProperties(compr_data)` + `Code`, raw streams — no size header needed, unlike the Go port).
8. Props decoding unit tests: LZMA `lc/lp/pb` unpacking (7-Zip rules), LZMA2 dict-size table, `prop > 40` rejection, wrong prop length per method.
   Tests per codec: compress known payloads with the same library's encoder → decode via our codec → byte-equal; decompress truncated/corrupt input → clean error.

### M4 — Groups, exception lists, RVZ packing
9. `RvzGroupEntry` parsing; `ChunkDecoder`: all-zero groups (`data_size==0`), NONE-stored groups (MSB clear), compressed groups (MSB set); expected-size bounds checking (Dolphin's exact-size rules); NONE padding to 4 after exception lists.
10. `RvzPackingDecoder` + `LaggedFibonacciPrng` (521 words, seed 17 words BE, fill rule, 4 warm-up advances, output byte order `>>24, >>18, >>8, >>0`, skip `offset % 0x8000`).
    Tests: packing round-trips (literal segments, padded segments, mixed); PRNG cross-checked against `ReferencePrng` (Go-port) with fixed seeds; skip-offset behavior; zero-groups; exception lists (multiple lists per 2 MiB chunk, small-chunk single lists, NONE padding).

### M5 — Wii partition reconstruction
11. `WiiSectorBuilder`: per 0x8000 sector → 31×0x400 data at 0x400, h0/h1/h2 hashes + padding layout, hash exceptions applied post-hash/pre-encryption, AES-128-CBC (zero IV for hash area; IV = ciphertext at 0x3D0 for data area).
    Tests: sector rebuilt from synthetic data == `ReferenceWiiSector` output (fixed seeds); exceptions applied correctly; cross-check with the Go `part.go` algorithm on random data.

### M6 — Reader API + CLI
12. `RvzReader.Open(Stream)` (parses M1–M2 eagerly, validates everything), `RvzStream` (`Stream` subclass): sequential `Read`, `Seek`/`Position`, `Length == iso_file_size`, one-chunk cache.
13. CLI: `rvzsharp info` (dump all header/table fields), `rvzsharp decode` (stream out with progress; optional `--sha1` verification).
    Tests: integration — full decode of synthetic RVZ files (below) == original ISO bytes; `Seek` + partial reads across chunk boundaries; `info` smoke test.

### M7 — Integration & real-world validation
14. `TestRvzBuilder` (in-memory RVZ writer): matrix of synthetic files —
    - GC disc: NONE / ZSTD / BZIP2 / LZMA1 / LZMA2 × {packing on/off} × {2 MiB, 32 KiB chunks} × {zero-groups, partial last chunk}
    - Wii disc: ZSTD + LZMA2 × {exceptions present/absent} × {2 segments, small chunks}
    - corrupt files: bad magic/hashes/sizes → typed errors, no hangs.
    Each decodes to byte-identical ISO (round-trip through the builder).
15. Real-world (optional, user-provided files): `RealFileDecodeTests` runs only when `RVZ_REAL_FILE` env var is set; SHA-1 of the decoded ISO is compared against the No-Intro datfiles already present in `References/rvz-1.0.3/testdata/*.dat` (they contain per-game SHA-1s). Also useful: decode with the Go `rvz` tool or Dolphin to cross-check on a machine that has them.

### M8 — Docs & packaging (small)
16. README (API usage, CLI usage, format notes), XML doc comments, `dotnet pack` for `RVZSharp.nupkg`, encoding design notes for Phase 2.

## 4b. Phase 2 — Decode legacy formats → canonical ISO (to enable re-encoding to RVZ)

User request (2026-08): decode legacy GameCube/Wii formats — **GCZ, WIA, CISO/WBI, WBFS** (and the
other Dolphin blob formats **TGC, NFS**) — so those files can later be re-encoded to RVZ.
Format facts below were verified against `References/dolphin-master/Source/Core/DiscIO/`
(Blob.cpp, CISOBlob.cpp, WbfsBlob.cpp, TGCBlob.cpp, NFSBlob.cpp, WIABlob.h) and
wit.wiimm.de documentation (GCZ/CISO/WBFS layout specs).

### M9 — Blob abstraction + auto-detection
17. `IBlobReader` (Dolphin `BlobReader` equivalent): `Length`, `ReadAt(offset, span)`,
    `BlockSize`, `BlobType` + factory `Blob.Open(stream)` sniffing magics
    (`RVZ\x01`, `WIA\x01`, GCZ, CISO, WBFS, TGC, NFS, EGGS). `RvzReader` implements it;
    CLI `info`/`decode` accept any blob type.
    Tests: detection table (each magic incl. negative cases), RVZ passthrough unchanged.

### M10 — WIA decoder (reuse the RVZ core)
18. WIA = the same `WIARVZFileReader<false>` template in Dolphin — differences from RVZ:
    magic `WIA\x01`; compression ∈ {NONE, Purge, BZIP2, LZMA, LZMA2} (no Zstandard);
    group entry is 8 bytes (no MSB compression flag, no `rvz_packed_size`); chunk size must be a
    multiple of 2 MiB (no small chunks); no RVZ packing. Add `WiaGroupEntry` (8 B), `PurgeCodec`
    (u32 offset/size segment runs + SHA-1 trailer over lists+segments), version constants
    (`WIA_VERSION_READ_COMPATIBLE = 0x00080000`), magic acceptance in `WiaFileHead`.
    Tests: synthetic WIA builder (extend `TestRvzBuilder` with a WIA mode) round-trips per codec
    incl. Purge; corrupted files; RVZ/WIA cross-rejection.

### M11 — GCZ decoder
19. GCZ (Dolphin `Blob.cpp`: `GCZ_MAGIC`, `ConvertToGCZ`; layout cross-checked with wit docs):
    header (magic, version, block size, block count, per-block offset table), each block
    (16 KiB) optionally zlib/Deflate-compressed; uncompressed blocks stored raw. Implement with
    `System.IO.Compression.ZLibStream` (BCL — no new dependency).
    Tests: synthetic GCZ builder round-trip (mixed raw/compressed blocks, odd last block).

### M12 — CISO decoder
20. CISO (`CISOBlob.cpp`): header (magic, block size, block count, block map), fixed blocks
    (0x8000), map entry 1 = block present (sequential file offsets), 0 = absent → zero-filled.
    Dolphin's reader does not decompress CISO blocks (plain copy).
    Tests: synthetic CISO (present/absent blocks, partial last block) round-trip.

### M13 — WBFS decoder
21. WBFS (`WbfsBlob.cpp`): header (`WBFS` magic, sector size/count, disc table), 2 MiB cluster
    allocation; unused sectors omitted → zero-filled on decode. Supports standalone `.wbfs`
    files (and the on-disc WBFS partition layout if cheap).
    Tests: synthetic WBFS (sparse disc: full, partial and empty clusters) round-trip.

### M14 — TGC, NFS (+ optional split-file/directory blobs)
22. TGC (`TGCBlob.cpp`): header at 0 (magic, `tgc_header_size`, DOL offsets), disc data follows
    the header; DOL extraction uses the header's real offset. NFS (`NFSBlob.cpp`, magic
    `EGGS`): multi-file container with a lower-bound data size. Optional: `SplitFileBlob`
    (.dol/.d01) and `DirectoryBlob` (folder → virtual ISO).
    Tests: synthetic TGC (with/without DOL relocation) and NFS round-trips; optional blobs if
    implemented.

## 4c. Phase 3 — RVZ encoding (planned; start after Phase 2 is green)

### M15 — RvzWriter + `rvzsharp convert`
23. Writer mirroring `TestRvzBuilder` (promoted into the library): chunking per Dolphin rules
    (aligned raw entries, per-2 MiB partition regions), exception-list generation (diff of
    recalculated vs original hashes), RVZ packing writer (junk detection via the Lagged
    Fibonacci `GetSeed` reverse algorithm from Dolphin's `LaggedFibonacciGenerator.cpp`, seed
    + segment writing), table/header assembly with all SHA-1s.
24. Encoders behind `ICompressionEncoder`: zstd (`ZstdSharp.CompressionStream`), bzip2
    (`BZip2OutputStream`), LZMA1/LZMA2 (`LZMA-SDK` becomes a runtime dependency, or vendor the
    7-Zip encoder), NONE. `rvzsharp convert <legacy|iso> out.rvz [--compression zstd] [--chunk-size ...]`.
    Tests: convert ISO → RVZ → decode → byte-identical (per codec, GC + Wii); convert every
    Phase-2 legacy format to RVZ and back; real-file spot checks.

## 5. Verification commands (used at every step)

```
dotnet build CSharp_RVZSharp.slnx -c Release
dotnet test  CSharp_RVZSharp.slnx
dotnet run --project src/RVZSharp.Cli -- info <file.rvz|legacy>
dotnet run --project src/RVZSharp.Cli -- decode <file.rvz|legacy> out.iso --sha1 <expected>   # Phase 2: any blob
dotnet run --project src/RVZSharp.Cli -- convert <file.rvz|legacy|iso> out.rvz               # Phase 3
```

## 6. Risks & mitigations

| Risk | Mitigation |
|---|---|
| No real RVZ files available locally (testdata `.rvz` files are Git-LFS pointers, not data) | Synthetic builder (M7) + optional real-file tests + No-Intro datfiles for SHA-1 reference |
| LZMA-SDK API may not expose raw LZMA2 streaming as needed | Verified at M3; fallback `Faithlife.Lzma`; codec abstraction isolates the change |
| RVZ packing / exceptions are easy to get subtly wrong (byte order, skip offsets, padding) | Independent reference implementations in tests (mirroring Go/Dolphin code) + round-trip ISO equality |
| Different validators disagree (Go is stricter than Dolphin: `disc_size == 0xDC`) | Follow Dolphin's rules (accepting superset), document divergences |
| Performance (multi-GB discs) | Streaming decode, single-chunk cache, `ReadExactly`/span APIs; parallel decompression deferred as an optimization step |
| Legacy format specs are thin (GCZ layout lives in Blob.cpp; wit docs are the cross-reference) | Read Dolphin's implementation as truth, wit.wiimm.de as cross-check; document every layout decision; synthetic builders + real-file SHA-1 checks |
| WIA Purge codec is WIA-only and unused by RVZ | Implement + test it in M10; it is small (segment runs + SHA-1 trailer) |
| Phase 3 LZMA encoding needs an encoder (LZMA-SDK is currently test-only) | Promote LZMA-SDK to a runtime dependency or vendor the 7-Zip encoder; both are pure managed |
| Legacy→RVZ conversion doubles the test surface | Round-trip property: convert → decode → compare with the original ISO for every legacy format |

## 7. Open questions for the user

1. Codec dependency strategy — resolved: managed NuGet packages (ZstdSharp.Port, SharpZipLib, LZMA-SDK). ✓
2. Real-world validation files — does the user have `.rvz` files to test against (M7 step 15)?
3. Legacy test files — does the user have real GCZ/CISO/WBFS/TGC/NFS/WIA files (M11–M14 real-file checks)?
4. Phase 3 scope — start RVZ encoding (M15) right after Phase 2, or after the user reviews the Phase 2 results?
