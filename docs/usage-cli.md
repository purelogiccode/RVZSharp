# CLI reference

The command-line tool accepts the **same command surface as Dolphin's `dolphin-tool`**:
`convert`, `verify`, `header`, `extract` — with the same flags, defaults and error messages.
The legacy `info` and `decode` commands are kept as RVZSharp extensions.

```
rvzsharp convert -i <FILE> -o <FILE> [-u <dir>] [-f iso|gcz|wia|rvz] [-s]
                 [-b <block_size>] [-c none|zstd|bzip2|lzma|lzma2] [-l <level>]
rvzsharp header -i <FILE> [-j] [-b] [-c] [-l]
rvzsharp verify -i <FILE> [-u <dir>] [-a crc32|md5|sha1]
rvzsharp extract -i <FILE> [-o <dir>] [-p <name>] [-s <path>] [-l] [-q] [-g]
rvzsharp info <FILE>                              (legacy alias of header)
rvzsharp decode <FILE> <OUT> [--sha1 <hex>]       (decode any blob to a plain ISO)
```

Run the CLI with:

```bash
dotnet run --project RVZSharp.Cli -c Release -- <command> [args…]
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
| `AE 0F 38 A2` | TGC |
| anything else | plain ISO |

(`WBFS` inputs may be split across `game.wbfs` + `game.wbf1…` continuation files, like
Dolphin; the parts are found from the file path.)

## `convert`

Converts a disc image to another container format (DolphinTool semantics):

```
convert -i <FILE> -o <FILE> [-u <dir>] [-f iso|gcz|wia|rvz] [-s]
        [-b <block_size>] [-c none|zstd|bzip2|lzma|lzma2] [-l <level>]
        [--threads <int>] [--verify]
```

| Option | Meaning |
|---|---|
| `-i`, `--input` | path to the input disc image (any supported format). Required. |
| `-o`, `--output` | path to the destination file. Required. |
| `-u`, `--user` | user folder path; accepted for DolphinTool compatibility (RVZSharp needs no user directory). |
| `-f`, `--format` | container format: `iso`, `gcz`, `wia`, `rvz`. Required. |
| `-b`, `--block_size` | block size in **bytes**. Required for GCZ/WIA/RVZ. |
| `-c`, `--compression` | compression method for WIA/RVZ: `none`, `zstd` (RVZ only), `bzip2`, `lzma`, `lzma2`. Required for WIA/RVZ; ignored for GCZ (always zlib deflate). |
| `-l`, `--compression_level` | compression level. Required unless `-c none`. |
| `-s`, `--scrub` | zero the data of non-game Wii partitions (update/channel) before converting; for `-f rvz`/`-f iso`/`-f gcz` a warning notes that scrubbing gains little. |
| `--threads` | compression threads (RVZSharp extension). `0` (default) uses the processor count; the output is byte-identical for any value. |
| `--verify` | after writing, decode the output and compare its CRC-32/MD5/SHA-1 with the input (RVZSharp extension); prints `Verification: OK (<sha1>)` or fails. With `-s` the scrubbed input is the reference. |

Block-size validation follows Dolphin's `IsDiscImageBlockSizeValid`:

| Format | Valid block sizes |
|---|---|
| `iso` | ignored |
| `gcz` | power of two |
| `wia` | ≥ 2 MiB and a multiple of 2 MiB |
| `rvz` | ≥ 32 KiB; below 2 MiB a power of two; above 2 MiB a multiple of 2 MiB |

Compression levels: `bzip2`/`lzma`/`lzma2` accept 1–9; `zstd` accepts −131072..22
(negative levels select Zstd's fast modes, 0 means the default — the same range as
Dolphin's CLI). A block size outside Dolphin's preferred range (32 KiB–2 MiB) prints a
warning and continues.

Notes:

- **`-f iso`** writes a plain, fully decoded ISO (the same operation as the legacy `decode`).
- **`-f rvz`** uses the RVZ writer: Wii partitions stored decrypted with hash exceptions,
  PRNG junk packing, fully checksummed tables. `-b` becomes the chunk size; below 2 MiB it
  must be a power of two, at/above 2 MiB a multiple of 2 MiB (Dolphin's rule).
- **`-f wia`** uses the WIA writer: Wii partitions stored decrypted with hash exceptions,
  fully checksummed tables. `-b` must be a multiple of 2 MiB; `zstd` is rejected (WIA
  supports `none`, `bzip2`, `lzma` and `lzma2`; the library's `WiaWriter` also supports
  `CompressionType.Purge`, which DolphinTool's CLI does not expose).
- **`-f gcz`** uses the GCZ writer: blocks of `-b` bytes (any power of two; 16 KiB is the
  classic size, Dolphin's GUI defaults to 128 KiB), each block deflated at level 9 and stored
  raw when compression saves fewer than 10 bytes, with a per-block Adler-32 of the stored
  bytes. `-c`/`-l` are ignored (GCZ is always zlib). Converting a Wii disc without `-s`
  prints Dolphin's "may not offer space advantages over ISO" warning.
- **`--threads`** (RVZSharp extension) controls the writer's compression pool (RVZ/WIA
  group compression and packing, GCZ block deflate). The default `0` uses the processor
  count; results are appended in disc order, so the output file is byte-identical for any
  thread count.
- **`--verify`** (RVZSharp extension) hashes the input before writing and re-decodes the
  written file afterwards, comparing CRC-32, MD5 and SHA-1. It works for every `-f` value.
- `-s` (scrub) requires a Wii disc with a game partition; other inputs fail with
  Dolphin's "Unable to process disc image. Try again without --scrub."

Legacy positional form (RVZSharp extension, still works):

```
rvzsharp convert <input> <output.rvz> [--compression <method>] [--level <n>]
                 [--chunk-size <bytes>] [--no-packing] [--threads <n>]
```

## `header`

Prints container and disc information (DolphinTool semantics):

```
header -i <FILE> [-j] [-b] [-c] [-l]
```

| Option | Meaning |
|---|---|
| `-i`, `--input` | path to the disc image. Required. |
| `-j`, `--json` | print the information as JSON and exit (overrides the other options). |
| `-b`, `--block_size` | print only the block size of GCZ/WIA/RVZ formats (`N/A` if none). |
| `-c`, `--compression` | print only the compression method (`N/A` if none). |
| `-l`, `--compression_level` | print only the compression level (`N/A` if none). |

With no options, the full report matches DolphinTool's layout:

```
Block Size: 131072
Compression Method: Zstandard
Compression Level: 5
Internal Name: TEST GAME TITLE
Revision: 48
Game ID: GALE01
Title ID: 000100014D474545
Region: NTSC-U
Country: USA
```

- `Block Size` / `Compression Method` / `Compression Level` come from the container
  (method strings match Dolphin: `Deflate` for GCZ, `Zstandard`/`bzip2`/`LZMA`/`LZMA2`/
  `Purge` for WIA/RVZ; omitted when absent).
- The game-data section follows Dolphin's `VolumeDisc` field reads: game ID (6 bytes at
  offset 0), revision (byte 7), internal name (0x60 bytes at 0x20), title ID (u64 at the
  game partition's ticket + 0x1DC, Wii only), region (GC: u32 at 0x458, Wii: u32 at
  0x4E000) and country (game-ID byte 3, mapped with Dolphin's `CountryCodeToCountry`).
- The section is omitted entirely for files that are not GC/Wii disc images.

## `verify`

Hashes the decoded disc content (DolphinTool semantics):

```
verify -i <FILE> [-u <dir>] [-a crc32|md5|sha1]
```

- With no `-a`, prints the full report:

```
CRC32: ee01e1c6
MD5: a5547d8fa856c04da2d0147d59176365
SHA1: 2fe83205d928407f049be5d2181cfb6e5ca44465
```

- With `-a <algo>`, prints just that digest in lowercase hex — handy for scripting
  (`verify -i game.rvz -a sha1` matches the `--sha1` value of `decode`).
- `rchash` is not offered (Dolphin only provides it when built with RetroAchievements
  support); `-a rchash` is an invalid choice.
- The input must be a GC/Wii disc image (checked by the disc magic); other files fail
  with "The input file is not a GC/Wii disc.".
- Hashing is done over the **decoded** image (RVZ/WIA groups are decompressed and Wii
  partition regions rebuilt), so the digests match the plain ISO.
- Unlike Dolphin's structural verifier, the digests verify decodability + content; exit
  code 1 on any decode failure (Dolphin exits 0 after recording problems).

## `extract`

DolphinTool-compatible option surface (`-i`, `-o`, `-p`, `-s`, `-l`, `-q`, `-g`), but the
command is **not implemented** (no disc filesystem support yet) — it validates the input
and fails with a clear error.

## Legacy commands

- `info <FILE>` — alias of `header` with the older RVZSharp layout (container version,
  disc type, partitions, raw areas, groups).
- `decode <FILE> <OUT> [--sha1 <hex>]` — decode any blob to a plain ISO; `--sha1` verifies
  the output hash while writing (the `convert -f iso` equivalent with verification).

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success (also for `-h`/`--help` on a command) |
| 1 | usage error, unknown option, unsupported feature, open/verification failure |

Errors are printed to stderr in DolphinTool's style (`Error: No input set`,
`Error: Block size must be set for GCZ/RVZ/WIA`, …).
