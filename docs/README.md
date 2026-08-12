# RVZSharp Documentation

RVZSharp is a .NET 10 library and command-line tool for **GameCube and Wii disc images**.
It reads the modern **RVZ** container (and its predecessor **WIA**), decodes the classic
legacy formats (**GCZ, CISO/WBI, WBFS, TGC, NFS**) into a canonical ISO view, and **writes
RVZ files** from any of them — mirroring the behaviour of the reference implementations in
[Dolphin](https://github.com/dolphin-emu/dolphin) (C++) and the Go
[rvz](https://github.com/Vali0004/rev/raw) reader.

| | |
|---|---|
| Target framework | .NET 10 (`net10.0`) |
| Solution file | `CSharp_RVZSharp.slnx` (XML solution — do **not** rename to `.sln`) |
| Tests | 221, all green (`dotnet test CSharp_RVZSharp.slnx -c Release`) |
| Read support | RVZ, WIA, GCZ, CISO/WBI, WBFS, TGC, NFS, plain ISO |
| Write support | RVZ (None, Zstd, Bzip2, LZMA1, LZMA2; optional PRNG-junk packing) |
| Reference sources | `References/dolphin-master/` (C++), `References/rvz-1.0.3/` (Go) |

## Documentation map

| Page | What it covers |
|---|---|
| [Getting started](getting-started.md) | Prerequisites, build, test, first commands |
| [CLI reference](usage-cli.md) | `info`, `decode`, `convert` — options and examples |
| [Library API](usage-library.md) | `Blob`, `RvzReader`, `RvzWriter`, codecs, packing API |
| [Architecture](architecture.md) | Module map, read/write pipelines, design decisions |
| [RVZ container format](format/rvz.md) | File head, disc struct, tables, groups, chunking |
| [Compression & packing](format/compression-packing.md) | Codec details and the Lagged-Fibonacci junk packing |
| [Wii partitions](format/wii-partitions.md) | Encryption, hash tree, hash exceptions, tickets |
| [Legacy formats](format/legacy.md) | GCZ, CISO/WBI, WBFS, TGC, NFS byte layouts |
| [Testing](testing.md) | Test strategy and synthetic image builders |
| [Roadmap & status](roadmap.md) | Milestones, limitations, open questions |
| [FAQ](faq.md) | Common questions |

## Conventions used in this wiki

- Byte offsets and sizes are **hexadecimal** unless stated otherwise (`0x…`).
- Multi-byte integers are **big-endian** (network order) unless a page says otherwise.
- The format pages describe the on-disk layout as implemented by Dolphin and verified by
  this project's tests; they are an implementation companion to `References/dolphin-master/docs/WiaAndRvz.md`.

## Feature overview

- **Blob abstraction** — every format is opened through the same
  `IBlobReader` interface; the format is auto-detected from its magic bytes, so `info`,
  `decode` and `convert` accept any supported file.
- **Canonical ISO view** — all readers expose the decoded disc as a random-access stream of
  ISO bytes, so a GCZ, a WIA and an RVZ of the same disc are interchangeable inputs.
- **RVZ writing** — the writer stores Wii partition data *decrypted* with hash exceptions
  (the same space-saving trick Dolphin uses), detects and packs PRNG junk with a recovered
  seed, and emits fully checksummed tables (SHA-1 everywhere Dolphin puts them).
- **Verifiable** — every conversion is byte-exact: the test suite round-trips synthetic
  discs through every codec, packing setting and chunk size, and the CLI can verify decoded
  output with `--sha1`.
