# CLI reference

The command-line tool accepts the **same command surface as Dolphin's `dolphin-tool`**:
`convert`, `verify`, `header`, `extract` — with the same flags, defaults and error messages.
The legacy `info` and `decode` commands are kept as RVZSharp extensions.

```
rvzsharp convert -i <FILE> -o <FILE> [-u <dir>] [-f iso|gcz|wia|rvz] [-s]
                 [-b <block_size>] [-c none|zstd|bzip2|lzma|lzma2] [-l <level>]
rvzsharp header -i <FILE> [-j] [-b] [-c] [-l]
rvzsharp verify -i <FILE> [-u <dir>] [-a crc32|md5|sha1]
rvzsharp extract -i <FILE> [-o <dir>] [-p <name>] [-s <path>] [-l <path>] [-q] [-g]
rvzsharp info <FILE>                              (legacy alias of header)
rvzsharp decode <FILE> <OUT> [--sha1 <hex>]       (decode any blob to a plain ISO)
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

## `convert`

Converts a disc image to another container format (DolphinTool semantics):

```
convert -i <FILE> -o <FILE> [-u <dir>] [-f iso|gcz|wia|rvz] [-s]
        [-b <block_size>] [-c none|zstd|bzip2|lzma|lzma2] [-l <level>]
```

| Option | Meaning |
|---|---|
| `-i`, `--input` | path to the input disc image (any supported format). Required. |
| `-o`, `--output` | path to the destination file. Required. |
| `-u`, `--user` | user folder path; accepted for DolphinTool compatibility (RVZSharp needs no user directory). |
| `-f`, `--format` | container format: `iso`, `gcz`, `wia`, `rvz`. Required. |
| `-b`, `--block_size` | block size in **bytes**. Required for GCZ/WIA/RVZ. |
| `-c`, `--compression` | compression method for WIA/RVZ: `none`, `zstd`, `bzip2`, `lzma`, `lzma2`. Required for WIA/RVZ. |
| `-l`, `--compression_level` | compression level. Required unless `-c none`. |
| `-s`, `--scrub` | scrub junk data — **not supported** by RVZSharp (errors out). |

Block-size validation follows Dolphin's `IsDiscImageBlockSizeValid`:

| Format | Valid block sizes |
|---|---|
| `iso` | ignored |
| `gcz` | power of two |
| `wia` | ≥ 2 MiB and a multiple of 2 MiB |
| `rvz` | ≥ 32 KiB; below 2 MiB a power of two; above 2 MiB a multiple of 2 MiB |

Compression levels: `bzip2`/`lzma`/`lzma2` accept 1–9; `zstd` accepts 1–22 (Dolphin's CLI
also allows negative "fast" levels, which this implementation does not). A block size
outside Dolphin's preferred range (32 KiB–2 MiB) prints a warning and continues.

Notes:

- **`-f iso`** writes a plain, fully decoded ISO (the same operation as the legacy `decode`).
- **`-f rvz`** uses the RVZ writer: Wii partitions stored decrypted with hash exceptions,
  PRNG junk packing, fully checksummed tables. `-b` becomes the chunk size; `-b` values
  above 2 MiB are rejected by this implementation (the writer supports powers of two from
  32 KiB to 2 MiB).
- **`-f gcz` / `-f wia`** are not implemented yet — the command fails with a clear error
  (only `iso` and `rvz` output exist).
- `-s` (scrub) is not implemented — the command fails rather than silently producing
  different output than Dolphin.

Legacy positional form (RVZSharp extension, still works):

```
rvzsharp convert <input> <output.rvz> [--compression <method>] [--level <n>]
                 [--chunk-size <kib>] [--no-packing]
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
Problems Found: No
```

- With `-a <algo>`, prints just that digest in lowercase hex — handy for scripting
  (`verify -i game.rvz -a sha1` matches the `--sha1` value of `decode`).
- `rchash` is accepted by the parser but errors out (RetroAchievements hashes are not
  implemented).
- Hashing is done over the **decoded** image (RVZ/WIA groups are decompressed and Wii
  partition regions rebuilt), so the digests match the plain ISO.
- `Problems Found` mirrors Dolphin's report header; RVZSharp reports `No` — container
  integrity problems fail loudly at open time instead.

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
