# CLI reference

```
rvzsharp info <file>
rvzsharp decode <file> <out.iso> [--sha1 <expected-hex>]
rvzsharp convert <file> <out.rvz> [--compression <none|zstd|bzip2|lzma|lzma2>]
                                   [--level <1-9>] [--chunk-size <kib>] [--no-packing]
```

Run the CLI with:

```bash
dotnet run --project src/RVZSharp.Cli -c Release -- <command> [args…]
```

## Input auto-detection

Every command opens its input through `Blob.Open`, which recognises formats by magic bytes:

| Magic bytes | Format |
|---|---|
| `52 56 5A 01` (`RVZ\x01`) | RVZ |
| `57 49 41 01` (`WIA\x01`) | WIA |
| `43 49 53 4F` (`CISO`) | CISO / WBI |
| `01 C0 0B B1` | GCZ |
| `57 42 46 53` (`WBFS`) | WBFS |
| `45 47 47 53` (`EGGS`) | NFS |
| `A2 38 0F AE` | TGC |
| anything else | plain ISO |

## `info`

Prints the container header and disc metadata:

```bash
rvzsharp info game.rvz
```

Example output:

```
file:            game.rvz
format:          RVZ
iso size:        4325376 bytes (0x420000)
version:         1.00
disc type:       GameCube (1)
compression:     Zstd (level 3)
chunk size:      0x200000
partitions:      0
```

For Wii discs, `partitions:` lists each partition (offset, sector range, key).

## `decode`

Writes the canonical ISO bytes to `out.iso`:

```bash
rvzsharp decode game.rvz game.iso
rvzsharp decode game.gcz game.iso --sha1 3f2d…c9
```

- `--sha1 <expected-hex>` — hashes the output as it is written and verifies it against the
  given SHA-1. The command fails if the hash does not match. This is the recommended way to
  validate a conversion.

## `convert`

Writes an RVZ file from any supported input:

```bash
rvzsharp convert game.iso game.rvz
rvzsharp convert game.wia game.rvz --compression lzma2 --level 5 --chunk-size 1024
rvzsharp convert game.gcz game.rvz --no-packing
```

| Option | Values | Default | Meaning |
|---|---|---|---|
| `--compression` | `none`, `zstd`, `bzip2`, `lzma`, `lzma2` | `zstd` | Compression method. `purge` is rejected — PURGE is WIA-only and cannot appear in an RVZ file. |
| `--level` | `1`–`9` | `3` | Compression level (Zstd accepts up to 22 internally). |
| `--chunk-size` | power of two, 32–2048 (KiB) | `2048` | Chunk size in KiB. Must be a power of two between 32 KiB and 2 MiB. |
| `--no-packing` | — | off | Disable the PRNG-junk packing stage (junk is stored literally). |

Conversion notes:

- **Wii discs** are detected from the disc header (Wii magic + hash/encryption flags) and
  converted with the partition optimization: partition data is stored *decrypted* together
  with hash exceptions, exactly like Dolphin's converter. Discs without valid partitions
  (or GC discs) are stored as raw data.
- **WBFS inputs** have a fixed 9.4 GiB logical size; converting one reads the whole logical
  image, which is slow. Prefer `decode` + `convert game.iso` when practical.
- **NFS inputs** require the file to live in a directory named `content` and the AES key in
  the sibling `code/htk.bin` (see [Legacy formats](format/legacy.md#nfs)).
- The output is verified internally: all tables carry SHA-1 checksums and the reader
  validates them on open.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | usage error, unknown format, or verification failure (`--sha1` mismatch) |

Errors are printed to stderr with the failing stage (e.g.
`The partition table SHA-1 does not match its contents.`).
