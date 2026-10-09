# What's new

Release highlights, newest first. The full 1.0.0 announcement is in
[Release notes 1.0.0](release-notes-1.0.0.md).

## 1.1.0 (unreleased)

A feature and correctness release. The public API stays compatible with 1.0.0 (checked at
pack time against the published package via `PackageValidationBaselineVersion`).

### Formats & verification

- **Legacy writers** — `CisoWriter`, `WbfsWriter` and `TgcWriter` complete the container
  matrix and are exposed as `convert -f ciso|wbfs|tgc`; all-zero blocks/clusters are stored
  absent, so scrubbed images shrink.
- **Wii verification** — `DiscVerifier` walks every partition's h0/h1/h2/h3 hash tree plus
  the TMD/H3 tables and reports per-partition issues (`verify --partitions`).
- **Retail ticket keys** — partition keys are now decrypted from the ticket with the Wii
  common key (Dolphin: `TicketReader::GetTitleKey`), so `verify --partitions`, `extract`
  and RVZ writing work on real encrypted discs; RVZ/WIA inputs prefer the container's
  authoritative partition key. `WiiVolume.GetTitleKey(ticket)` exposes the decryption.
- **Extract fixes** — `tmd.bin`/`cert.bin`/`h3.bin` are read at their partition-relative
  offsets (they previously used the wrong base), `h3.bin` is skipped for hashless discs,
  and partition headers are printed by `extract --list`.
- **WBFS fixes** — `hd_sector_count` is relative to the stream position, and the disc
  header copy is written at offset `0x200` for tools that identify discs without decoding.

### CLI

- `--json` for `convert` and `verify`, `-` stdin/stdout piping, and
  `rvzsharp completions bash|zsh|fish|powershell`. Logs and progress go to stderr, so
  stdout stays machine-readable (including `convert -f iso --json`).
- `convert --verify` honors Ctrl+C (exit code 130) during hashing and verification, and
  writer progress is monotonic, reaching exactly 1.0 when the last byte is read.

### Performance & robustness

- Thread-safe `ReadAt` with a 16 MiB LRU decoded-unit cache, binary-search area lookup and
  ArrayPool group reads; parallel group compression and parallel RVZ/WIA full-image decode
  (`--threads`, byte-identical output for any thread count); parser caps plus a
  mutation/fuzz robustness suite; Native AOT/trimming validated.
- `IBlobReader.Length`, the async API (`CopyToAsync`/`ReadFullyAsync`/`WriteAsync`), split
  plain ISO (`game.part0.iso`), `BlobStream`, `DiscInfo`, `Scrub` and path overloads.
- Parallel decode/encode failures now surface the original exception
  (e.g. `RvzFormatException`) instead of `AggregateException`.

### Tooling & packaging

- GitHub Actions CI (fast tests on `net8.0`/`net9.0`/`net10.0` with coverage, pack with API
  validation + embedded SPDX 2.2 SBOM, smoke-tested ReadyToRun CLI artifact) and Dependabot.
- **468 fast tests** on each target framework plus 276 real-file slow tests (90 RVZ games).

## 1.0.0 (2026-08-15)

First stable release: byte-exact RVZ/WIA decoding and encoding, GCZ writing, legacy decoders
(GCZ, CISO/WBI, WBFS incl. split files, TGC, NFS, plain ISO), Dolphin-compatible CLI and a
fully documented library API. See [Release notes 1.0.0](release-notes-1.0.0.md).
