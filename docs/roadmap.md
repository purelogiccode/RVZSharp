# Roadmap & status

## Status

| Phase | Milestones | Status |
|---|---|---|
| 1 — RVZ reader | container parsing, tables, codecs, packing, Wii partition rebuild, CLI `info`/`decode` | ✅ done |
| 2 — Legacy decoders | blob abstraction + magic detection; WIA, GCZ, CISO/WBI, WBFS, TGC, NFS | ✅ done |
| 3 — RVZ writer | `RvzWriter`, encoders, junk packing, CLI `convert` | ✅ done |
| 4 — Distribution | NuGet package (net8.0/9.0/10.0), multi-target tests, progress/cancellation API | ✅ done |
| 5 — Real-world validation | test against real game images | ⏳ open |

## Supported

- Read: RVZ, WIA, GCZ, CISO/WBI, WBFS, TGC, NFS, plain ISO — auto-detected, random-access.
- Write: RVZ from any of the above (None/Zstd/Bzip2/LZMA1/LZMA2, levels 1–9, chunk sizes
  32 KiB–2 MiB, optional packing, `--sha1`-verifiable output).
- Wii partition optimization with hash exceptions, FST split, zero groups, PRNG-junk
  packing with seed recovery.

## Known limitations

| Limitation | Detail |
|---|---|
| WBFS conversion is slow | WBFS reports a fixed 9.4 GiB logical image; converting reads all of it (mostly zero clusters). `decode` + `convert` on the ISO is faster in practice. |
| PURGE output | PURGE is WIA-only; `convert --compression purge` is rejected, and RVZ readers reject PURGE containers. |
| Chunk sizes above 2 MiB | not writable (Dolphin's converter doesn't expose them either); readable if present. |
| Single-threaded | reading and writing are sequential; no multithreading yet. |
| NFS key location | key must be discoverable (`code/htk.bin` next to `content/`) or supplied via `Blob.Open(stream, nfsKey, …)`. |
| No WIA/GCZ writer | `convert -f wia`/`-f gcz` fail with a clear error; only `iso` and `rvz` output exist. |
| No `extract` command | DolphinTool's `extract` requires a disc filesystem (FST) implementation; the CLI validates the arguments and reports it as unsupported. |
| Zstd "fast" levels | Dolphin's CLI accepts negative Zstd levels; RVZSharp accepts 1–22. |

## Open questions

1. **Real-file validation** — do you have real GCZ / CISO / WBFS / TGC / NFS / WIA / RVZ
   files? Byte-exact decode of real images is the highest-value remaining validation step
   (`decode --sha1` makes it trivial to check).
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
