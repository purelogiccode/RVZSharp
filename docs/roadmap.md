# Roadmap & status

## Status

| Phase | Milestones | Status |
|---|---|---|
| 1 — RVZ reader | container parsing, tables, codecs, packing, Wii partition rebuild, CLI `info`/`decode` | ✅ done |
| 2 — Legacy decoders | blob abstraction + magic detection; WIA, GCZ, CISO/WBI, WBFS, TGC, NFS | ✅ done |
| 3 — RVZ writer | `RvzWriter`, encoders, junk packing, CLI `convert` | ✅ done |
| 4 — Distribution | NuGet package (net8.0/9.0/10.0), multi-target tests, progress/cancellation API | ✅ done |
| 5 — Reference alignment | audit against dolphin-master + rvz-1.0.3; every finding fixed or documented (see TODO.md) | ✅ done |
| 6 — Real-world validation | 97 real-file tests (`RVZSharp.Slow.Tests`) against 30 GameCube/Wii RVZ games (No-Intro SHA-1) incl. writer round-trips — found & fixed the 2 MiB ticket-key writer bug | ✅ done |

## Supported

- Read: RVZ, WIA, GCZ, CISO/WBI, WBFS, TGC, NFS, plain ISO — auto-detected, random-access.
- Write: RVZ from any of the above (None/Zstd/Bzip2/LZMA1/LZMA2 with Dolphin's level rules
  incl. negative Zstd "fast" levels; chunk sizes 32 KiB–2 MiB powers of two or multiples of
  2 MiB above that; optional packing; `--sha1`-verifiable output).
- `--scrub`: zeroes the data of non-game Wii partitions (update/channel) before converting.
- Wii partition optimization with hash exceptions, FST split, zero groups, PRNG-junk
  packing with seed recovery.
- Real-world validation: 30 real GC/Wii RVZ images decode byte-for-byte to their official
  No-Intro SHA-1s; real images re-encode to RVZ (default 2 MiB chunks) and decode back to
  the same hash. See [testing.md](testing.md#real-file-suite) for the suite details.

## Known limitations

| Limitation | Detail |
|---|---|
| WBFS conversion is slow | WBFS reports a fixed 9.4 GiB logical image; converting reads all of it (mostly zero clusters). `decode` + `convert` on the ISO is faster in practice. |
| PURGE output | PURGE is WIA-only; `convert --compression purge` is rejected, and RVZ readers reject PURGE containers. |
| No WIA/GCZ writers | `convert -f wia` / `-f gcz` fail with a clear error (only `iso` and `rvz` output exist). |
| No `extract` command | DolphinTool's `extract` requires a disc filesystem (FST) implementation; the CLI validates the arguments and reports it as unsupported. |
| NFS key location | the AES key must come from `code/htk.bin` next to the `content/hif_000000.nfs` file (or be supplied via the library API). |
| Single-threaded | reading and writing are sequential; no multithreading yet. |

## Open questions

1. **Real-file validation** — ✅ resolved for RVZ: 30 real GameCube/Wii games decode
   byte-for-byte to their official No-Intro SHA-1s, and real images re-encode to RVZ and
   decode back. Legacy-format real files (GCZ/CISO/WBFS/TGC/NFS/WIA) are still only
   validated against synthetic images.
2. **Performance targets** — if large collections must be converted, parallel group
   processing (Dolphin uses a thread pool for exactly this) would give near-linear speedup.

## Possible next steps

- Parallel compression in the writer (per-group worker pool, like Dolphin's
  `MultithreadedCompressor`).
- `--verify` mode: convert and immediately decode-compare (or rely on `--sha1`).
- WIA writer (shares ~90% of the RVZ writer; adds Purge support) and GCZ writer.
- `extract` command: FST parser + file/directory extraction, listing, and game-only mode.
- Streaming progress reporting for long conversions.
- Cross-checks against `wit`/`wwt` output for shared formats.
