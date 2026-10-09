[![CI](https://github.com/purelogiccode/RVZSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/purelogiccode/RVZSharp/actions/workflows/ci.yml)
[![Docs](https://github.com/purelogiccode/RVZSharp/actions/workflows/pages.yml/badge.svg)](https://purelogiccode.github.io/RVZSharp/)
[![NuGet](https://img.shields.io/nuget/v/RVZSharp?color=blue)](https://www.nuget.org/packages/RVZSharp/)
[![NuGet downloads](https://img.shields.io/nuget/dt/RVZSharp?color=blue)](https://www.nuget.org/packages/RVZSharp/)
[![Release](https://img.shields.io/github/v/release/purelogiccode/RVZSharp?color=blue&label=release)](https://github.com/purelogiccode/RVZSharp/releases)
[![License](https://img.shields.io/badge/license-GPL--2.0--or--later-green)](https://github.com/purelogiccode/RVZSharp/blob/master/LICENSE)
[![Platforms](https://img.shields.io/badge/platform-Windows_%7C_Linux_%7C_macOS-lightgrey)](https://github.com/purelogiccode/RVZSharp/releases)

# RVZSharp Documentation

RVZSharp is a .NET 8 / 9 / 10 library and command-line tool for **GameCube and Wii disc
images**.
It reads the modern **RVZ** container (and its predecessor **WIA**), decodes the classic
legacy formats (**GCZ, CISO/WBI, WBFS, TGC, NFS**) into a canonical ISO view, and **writes
RVZ, WIA and GCZ files** from any of them — mirroring the behaviour of the reference
implementations in
[Dolphin](https://github.com/dolphin-emu/dolphin) (C++) and the Go
[rvz](https://github.com/Vali0004/rev/raw) reader.

| | |
|---|---|
| Target frameworks | `net8.0`, `net9.0`, `net10.0` |
| Solution file | `CSharp_RVZSharp.sln` |
| Tests | 609 fast library tests (every framework, ~1 min) + 57 CLI tests + 276 real-file slow tests — the solution runs fast-only by default (`dotnet test CSharp_RVZSharp.sln -c Release`); run the slow suite explicitly with `dotnet test RVZSharp.Slow.Tests -c Release` |
| Read support | RVZ, WIA, GCZ, CISO/WBI, WBFS, TGC, NFS, plain ISO |
| Write support | RVZ (None, Zstd, Bzip2, LZMA1, LZMA2; optional PRNG-junk packing), WIA (None, PURGE, Bzip2, LZMA1, LZMA2) and GCZ (zlib deflate) |
| Reference sources | `References/dolphin-master/` (C++), `References/rvz-1.0.3/` (Go) |

## Documentation map

| Page | What it covers |
|---|---|
| [What's new](whats-new.md) | Release highlights (1.1.0, 1.0.0) |
| [Getting started](getting-started.md) | Prerequisites, build, test, first commands |
| [Packaging & distribution](packaging.md) | NuGet package contents, build, publish, versioning |
| [CLI reference](usage-cli.md) | `convert`, `header`, `verify`, `extract` (+ legacy `info`/`decode`) — options and examples |
| [Library API](usage-library.md) | `Blob`, `RvzReader`, `RvzWriter`, `WiaWriter`, `GczWriter`, `DiscFileSystem`, `DiscHasher`, codecs, packing API |
| [Architecture](architecture.md) | Module map, read/write pipelines, design decisions |
| [RVZ container format](format/rvz.md) | File head, disc struct, tables, groups, chunking |
| [Compression & packing](format/compression-packing.md) | Codec details and the Lagged-Fibonacci junk packing |
| [Wii partitions](format/wii-partitions.md) | Encryption, hash tree, hash exceptions, tickets |
| [Legacy formats](format/legacy.md) | GCZ, CISO/WBI, WBFS, TGC, NFS byte layouts |
| [Testing](testing.md) | Test strategy and synthetic image builders |
| [Release notes](release-notes-1.1.0.md) | 1.1.0 and [1.0.0](release-notes-1.0.0.md) announcement content (GitHub release posts) |
| [Roadmap & status](roadmap.md) | Milestones, limitations, open questions |
| [FAQ](faq.md) | Common questions |

## Conventions used in this wiki

- Byte offsets and sizes are **hexadecimal** unless stated otherwise (`0x…`).
- Multi-byte integers are **big-endian** (network order) unless a page says otherwise.
- The format pages describe the on-disk layout as implemented by Dolphin and verified by
  this project's tests; they are an implementation companion to `References/dolphin-master/docs/WiaAndRvz.md`.

## Wiki and Pages

These pages are published twice from the same files, automatically:

- **GitHub Pages** (`.github/workflows/pages.yml`) builds this folder with Jekyll on
  every `master` push touching `docs/`; `index.md` renders this page as the home page
  and the side menu comes from [`_data/nav.yml`](_data/nav.yml) rendered by
  [`_layouts/default.html`](_layouts/default.html). Internal links use the `.md` form,
  which Jekyll converts to `.html`.
- **GitHub Wiki** (`.github/workflows/wiki.yml`) mirrors these pages to the wiki root
  on the same trigger (`README.md` → `Home.md`, `format/*.md` → `format/…` subpages,
  [`_Sidebar.md`](_Sidebar.md) kept as the wiki sidebar, Jekyll-only files dropped);
  relative links are rewritten to the extensionless wiki form. The wiki is fully
  generated — edit these files, never the wiki pages.

Releases (bundled CLI zips, NuGet publishing) and the automation setup are documented
in [Packaging & distribution](packaging.md).

## Feature overview

- **Blob abstraction** — every format is opened through the same
  `IBlobReader` interface; the format is auto-detected from its magic bytes, so `info`,
  `decode` and `convert` accept any supported file.
- **Canonical ISO view** — all readers expose the decoded disc as a random-access stream of
  ISO bytes, so a GCZ, a WIA and an RVZ of the same disc are interchangeable inputs.
- **RVZ/WIA/GCZ writing** — the RVZ/WIA writer stores Wii partition data *decrypted* with
  hash exceptions (the same space-saving trick Dolphin uses), detects and packs PRNG junk
  with a recovered seed (RVZ), and emits fully checksummed tables (SHA-1 everywhere Dolphin
  puts them); GCZ is written as Dolphin-compatible zlib blocks with per-block Adler-32s.
- **File system access** — `DiscFileSystem` parses the GameCube/Wii FST (case-insensitive
  lookup, file streaming through decrypted partition views), and the CLI `extract` command
  lists/extracts the tree and the standard system data per partition.
- **Parallel and async** — RVZ/WIA chunk decoding and group compression run on bounded
  worker pools (`--threads`; output byte-identical for any thread count), and the async API
  (`CopyToAsync`/`ReadFullyAsync`/`WriteAsync`) wraps the CPU-bound work for UI consumers.
- **Verifiable** — every conversion is byte-exact: the test suite round-trips synthetic
  discs through every codec, packing setting and chunk size, decodes **90 real GameCube/Wii
  RVZ files** byte-for-byte against their official No-Intro SHA-1s, re-encodes real GC/Wii
  images back to RVZ, and `DiscHasher`/the CLI can verify decoded output (`--sha1`,
  `verify`).
