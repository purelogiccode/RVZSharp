# Roadmap & status

## Status

| Phase | Milestones | Status |
|---|---|---|
| 1 — RVZ reader | container parsing, tables, codecs, packing, Wii partition rebuild, CLI `info`/`decode` | ✅ done |
| 2 — Legacy decoders | blob abstraction + magic detection; WIA, GCZ, CISO/WBI, WBFS, TGC, NFS | ✅ done |
| 3 — RVZ writer | `RvzWriter`, encoders, junk packing, CLI `convert` | ✅ done |
| 4 — Distribution | NuGet package (net8.0/9.0/10.0), multi-target tests, progress/cancellation API | ✅ done |
| 5 — Reference alignment | audit against dolphin-master + rvz-1.0.3; every finding fixed or documented | ✅ done |
| 6 — Real-world validation | 97 real-file tests (`RVZSharp.Slow.Tests`) against 30 GameCube/Wii RVZ games (No-Intro SHA-1) incl. writer round-trips — found & fixed the 2 MiB ticket-key writer bug | ✅ done |
| 7 — Writer performance | parallel group compression (`MaxThreads` / `--threads`, Dolphin's worker-pool model), `convert --verify` hash comparison | ✅ done |
| 8 — GCZ writer | `GczWriter` / `convert -f gcz`: 16 KiB zlib blocks, raw-block fallback, per-block Adler-32, parallel block deflate | ✅ done |

## Supported

- Read: RVZ, WIA, GCZ, CISO/WBI, WBFS, TGC, NFS, plain ISO — auto-detected, random-access.
- Write: RVZ, WIA and GCZ from any of the above (None/Zstd/Bzip2/LZMA1/LZMA2 with Dolphin's
  level rules incl. negative Zstd "fast" levels; PURGE for WIA; chunk sizes 32 KiB–2 MiB
  powers of two or multiples of 2 MiB above that for RVZ, multiples of 2 MiB for WIA; GCZ
  with power-of-two block sizes; optional RVZ packing; `--sha1`-verifiable output).
- `--scrub`: zeroes the data of non-game Wii partitions (update/channel) before converting.
- Wii partition optimization with hash exceptions, FST split, zero groups, PRNG-junk
  packing with seed recovery.
- Progress/cancellation on both encode (`RvzWriter.Write`/`WiaWriter.Write`) and decode
  (`IBlobReader.CopyTo`/`ReadFully`); `DiscHasher` computes CRC-32/MD5/SHA-1 in one pass.
- Parallel group compression in the writer (`MaxThreads`, CLI `--threads`; output is
  byte-identical to sequential) and `convert --verify` (input vs output hashes).
- Real-world validation: 30 real GC/Wii RVZ images decode byte-for-byte to their official
  No-Intro SHA-1s; real images re-encode to RVZ (default 2 MiB chunks) and decode back to
  the same hash. See [testing.md](testing.md#real-file-suite) for the suite details.

## Known limitations

| Limitation | Detail |
|---|---|
| WBFS conversion is slow | WBFS reports a fixed 9.4 GiB logical image; converting reads all of it (mostly zero clusters). `decode` + `convert` on the ISO is faster in practice. |
| PURGE output | PURGE is WIA-only; `WiaWriter` supports it (`CompressionType.Purge`), but the CLI's `-c` choices mirror DolphinTool and do not expose it. RVZ readers reject PURGE containers. |
| No `extract` command | DolphinTool's `extract` requires a disc filesystem (FST) implementation; the CLI validates the arguments and reports it as unsupported. |
| NFS key location | the AES key must come from `code/htk.bin` next to the `content/hif_000000.nfs` file (or be supplied via the library API). |
| Single-threaded reads | decoding is sequential; only the writer compresses groups in parallel (`MaxThreads`). |

## Open questions

1. **Real-file validation** — ✅ resolved for RVZ: 30 real GameCube/Wii games decode
   byte-for-byte to their official No-Intro SHA-1s, and real images re-encode to RVZ and
   decode back. Legacy-format real files (GCZ/CISO/WBFS/TGC/NFS/WIA) are still only
   validated against synthetic images.
2. **Performance targets** — ✅ resolved for writing: group packing/compression runs on a
   worker pool (`MaxThreads`); reading is still sequential.

## Possible next steps

- `extract` command: FST parser + file/directory extraction, listing, and game-only mode.
- Async API surface (`CopyToAsync`, `WriteAsync`) for UI consumers.
- Cross-checks against `wit`/`wwt` output for shared formats.
