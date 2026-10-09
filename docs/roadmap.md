# Roadmap & status

## Status

| Phase | Milestones | Status |
|---|---|---|
| 1 — RVZ reader | container parsing, tables, codecs, packing, Wii partition rebuild, CLI `info`/`decode` | ✅ done |
| 2 — Legacy decoders | blob abstraction + magic detection; WIA, GCZ, CISO/WBI, WBFS, TGC, NFS | ✅ done |
| 3 — RVZ writer | `RvzWriter`, encoders, junk packing, CLI `convert` | ✅ done |
| 4 — Distribution | NuGet package (net8.0/9.0/10.0), multi-target tests, progress/cancellation API | ✅ done |
| 5 — Reference alignment | audit against dolphin-master + rvz-1.0.3; every finding fixed or documented | ✅ done |
| 6 — Real-world validation | 276 real-file tests (`RVZSharp.Slow.Tests`) against 90 GameCube/Wii RVZ games (No-Intro SHA-1) incl. writer round-trips — found & fixed the 2 MiB ticket-key writer bug | ✅ done |
| 7 — Writer performance | parallel group compression (`MaxThreads` / `--threads`, Dolphin's worker-pool model), `convert --verify` hash comparison | ✅ done |
| 8 — GCZ writer | `GczWriter` / `convert -f gcz`: 16 KiB zlib blocks, raw-block fallback, per-block Adler-32, parallel block deflate | ✅ done |
| 9 — Filesystem & concurrency | `DiscFileSystem` FST parser + `PartitionReader`, CLI `extract` (list/extract/single/partition/gameonly, system data), parallel full-image decode (`--threads`), async API (`CopyToAsync`/`ReadFullyAsync`/`WriteAsync`), `-c purge` for WIA | ✅ done |
| 10 — API ergonomics | Dolphin-default compression level 5, `DiscInfo` metadata API, split plain ISO (`.part0.iso`), path overloads (`RvzWriter.Write`/`RvzReader.Open`), seekable `BlobStream`, `Scrub` writer option | ✅ done |
| 11 — Robustness & performance | thread-safe `ReadAt` with a 16 MiB LRU decoded-unit cache, binary-search area lookup, ArrayPool group reads; parser caps + mutation/fuzz robustness suite; Native AOT/trimming validated (library annotated, CLI trimmed + AOT publish smoke-tested) | ✅ done |
| 12 — Formats & verification | `CisoWriter`/`WbfsWriter`/`TgcWriter` (+ `convert -f ciso\|wbfs\|tgc`); `DiscVerifier` Wii h0/h1/h2/h3 + TMD/H3 verification (`verify --partitions`); env-var real legacy-file tests + optional `dolphin-tool`/`wit`/`wwt` differential tests | ✅ done |
| 13 — Tooling & packaging | GitHub Actions CI (build + fast tests on net8.0/9.0/10.0, coverage artifact, pack + API validation, CLI publish) and Dependabot; `PackageValidationBaselineVersion` (1.0.0) diffing the public API; SPDX SBOM embedded in the nupkg; CLI `--json`, `-` stdin/stdout and shell completions; per-tag GitHub Releases with CLI bundles + NuGet push; `docs/` mirrored to the wiki and deployed to Pages | ✅ done (1.1.0 published 2026-10-09) |
| 14 — Correctness review | commit-by-commit audit before 1.1.0: retail ticket-key decryption with the Wii common key (`WiiVolume.GetTitleKey`), extract TMD/cert/H3 partition-relative offsets + H3 guard, WBFS header size/copy fixes, `convert --json` stdout hygiene, Ctrl+C exit 130, monotonic writer progress, original-exception propagation from parallel loops — each pinned by a regression test | ✅ done |
| 15 — Audit follow-up | second commit-by-commit audit: zero-group decode of all-zero partition chunks, extract path-traversal guards (`-s`/image names cannot escape `-o`), FST validation hardening, `decode -h`, help-run telemetry/update-prompt skips, stdin-spool cancellation in every command, release/wiki workflow fixes — each pinned by a regression test | ✅ done |

## Supported

- Read: RVZ, WIA, GCZ, CISO/WBI, WBFS, TGC, NFS, plain ISO — auto-detected, random-access.
- Write: RVZ, WIA, GCZ, CISO/WBI, WBFS and TGC from any of the above (None/Zstd/Bzip2/LZMA1/
  LZMA2 with Dolphin's level rules incl. negative Zstd "fast" levels; PURGE for WIA; chunk
  sizes 32 KiB–2 MiB powers of two or multiples of 2 MiB above that for RVZ, multiples of
  2 MiB for WIA; GCZ/CISO with power-of-two block sizes; WBFS clusters ≥ 32 KiB; optional
  RVZ packing; `--sha1`-verifiable output). CISO/WBFS store all-zero blocks absent (shrinks
  scrubbed images); TGC is GameCube-only.
- `--scrub`: zeroes the data of non-game Wii partitions (update/channel) before converting.
- Wii partition optimization with hash exceptions, FST split, zero groups, PRNG-junk
  packing with seed recovery; partition keys are common-key decrypted from the ticket
  (`WiiVolume.GetTitleKey`) or taken from the container's partition table.
- Progress/cancellation on both encode (`RvzWriter.Write`/`WiaWriter.Write`) and decode
  (`IBlobReader.CopyTo`/`ReadFully`); `DiscHasher` computes CRC-32/MD5/SHA-1 in one pass;
  `DiscVerifier` walks Wii partition h0/h1/h2/h3 hash trees and the TMD/H3 tables
  (Dolphin's VolumeVerifier), reporting per-partition issues.
- Parallel group compression in the writer (`MaxThreads`, CLI `--threads`; output is
  byte-identical to sequential), parallel full-image decode for RVZ/WIA, `convert --verify`
  (input vs output hashes), and an async API (`CopyToAsync`, `ReadFullyAsync`, `WriteAsync`).
- File system access: `DiscFileSystem` parses the GameCube/Wii FST (case-insensitive lookup,
  file streaming) and the CLI `extract` command lists/extracts the tree plus the standard
  system data per partition (DolphinTool-compatible layout).
- Real-world validation: 90 real GC/Wii RVZ images decode byte-for-byte to their official
  No-Intro SHA-1s; real images re-encode to RVZ (default 2 MiB chunks) and decode back to
  the same hash. See [testing.md](testing.md#real-file-suite) for the suite details.

## Known limitations

| Limitation | Detail |
|---|---|
| WBFS conversion is slow | WBFS reports a fixed 9.4 GiB logical image; converting reads all of it (mostly zero clusters). `decode` + `convert` on the ISO is faster in practice. |
| PURGE output | PURGE is WIA-only; `WiaWriter` and the CLI (`-c purge`) support it, but RVZ readers reject PURGE containers. |
| NFS key location | the AES key must come from `code/htk.bin` next to the `content/hif_000000.nfs` file (or be supplied via the library API). |
| Sequential random access | full-image decode can use a worker pool (RVZ/WIA, `--threads`), but individual `ReadAt` calls and the other formats decode sequentially. |

## Open questions

1. **Real-file validation** — ✅ resolved for RVZ: 90 real GameCube/Wii games decode
   byte-for-byte to their official No-Intro SHA-1s, and real images re-encode to RVZ and
   decode back. Legacy-format real files (GCZ/CISO/WBFS/TGC/NFS/WIA) are covered by
   env-var-driven tests in the slow suite (`RVZ_REAL_GCZ` etc.) plus optional
   `dolphin-tool`/`wit`/`wwt` differential tests; running them still needs the real files
   and tools on the machine.
2. **Performance targets** — ✅ resolved for writing and full-image reads: group
   packing/compression and RVZ/WIA chunk decoding both run on worker pools (`MaxThreads`);
   per-range `ReadAt` remains sequential by design.

## Possible next steps

- Run the legacy/differential slow tests against a real collection of GCZ/CISO/WBFS/TGC/NFS
  files and `dolphin-tool`/`wit`/`wwt` on a machine that has them.
- Keep the 1.1.x line current: dependency bumps via Dependabot and patch releases from
  `master` when a regression is reported.
