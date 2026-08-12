# Legacy formats

All legacy formats are decoded to the canonical ISO view through `IBlobReader`. Detection
is by magic bytes (see [CLI reference](../usage-cli.md#input-auto-detection)). Layout
details below follow Dolphin's `DiscIO` sources.

## GCZ

A compressed GameCube image, invented by GCZTool and supported by Dolphin. The header is
32 bytes; all integers little-endian.

```
0x00  magic                  u32 — 0xB10BC001 (bytes 01 C0 0B B1)
0x08  compressed_data_size   u64 — size of the data area after the tables
0x10  disc_size              u64 — uncompressed ISO size
0x18  block_size             u32 — 0x4000 for standard images
0x1C  num_blocks             u32
0x20  block pointer table:   8 bytes per block
      { u64 LE offset | bit 63 = uncompressed flag }
      (offset relative to the start of the compressed data; bit 63 set = block stored
       uncompressed, clear = deflate-compressed)
      then the hash table:   u32 LE Adler-32 per block
```

Header area total = `0x20 + 12 · num_blocks`; the size fields are validated in `ulong` so a
header with the top bit set cannot wrap past the bounds check. Each 0x4000-byte block of
ISO data is deflate-compressed independently; blocks with size 0 decode as zeroes.
RVZSharp decompresses with the BCL `ZLibStream`.

## CISO / WBI

A simple block-compressed format used by old homebrew tools.

```
0x0000  magic       "CISO"
0x0004  block_size  u32 LE — 0x8000 for standard images
0x0008  block map   0x7FF8 bytes — 1 byte per block: 1 = block present, 0 = zero-fill
0x8000  present blocks, concatenated in order
```

The decoded length is always `0x7FF8 × block_size` (the map covers 0x7FF8 blocks,
regardless of the `file_size` other tools write). Present blocks are served sequentially;
absent blocks decode as zeroes.

## WBFS

The Wii Backup File System container (a FAT-like structure in the file). Header is
512 bytes; multi-byte integers big-endian.

```
0x000  magic            "WBFS"
0x004  hd_sector_count  u32 BE — total sectors of the raw device image
0x008  hd_sector_shift  u8  — raw sector size = 1 << shift
0x009  cluster_shift    u8  — cluster size  = 1 << shift (≥ 0x8000; 2 MiB for standard files)
0x00A  disc_table[0]    u8  — slot 0 present?
0x00C  disc_table…      disc offsets in raw sectors
0x100  disc info table  (DiscHeaderSize = 256 bytes)
…     disc tables:     the wlba table at `raw_sector_size + 0x100`:
        u16 BE cluster index per 2 MiB cluster of the disc
```

- `WiiDataSize` is fixed at **9,399,549,952 bytes** (~9.4 GiB), so a WBFS blob always
  reports that logical size; clusters beyond the file are served as zeroes.
- The file length must equal `hd_sector_count × hd_sector_size`.
- Cluster indices are **u16 big-endian**; the wlba table holds one entry per 2 MiB cluster
  of the disc, starting at `hd_sector_size + 0x100`.
- RVZSharp opens the disc in slot 0.

## TGC

"Tiny GameCube" images: the disc without its empty tail, plus a header. RVZSharp follows
Dolphin: the virtual image is the **file minus the TGC header**
(`Length = file.Length − tgc_header_size`), and reads are served with three on-the-fly
patches. Header fields are u32 big-endian.

```
0x000  magic                 A2 38 0F AE
0x008  tgc_header_size       (removed from the front when decoding)
0x010  fst_real_offset       offset of the FST within the file
0x014  fst_size
0x01C  dol_real_offset
0x024  file_area_real_offset
0x034  file_area_virtual_offset
```

Read patches:

- **Disc header rewrite** — the DOL and FST offset fields at `0x420` / `0x424` of the
  virtual image are rewritten to `real_offset − tgc_header_size`, so the virtual disc
  header points into the decoded image.
- **FST relocation** — the FST is replaced by a relocated copy: every file entry (12 bytes,
  first byte 0 = file rather than directory) has its offset field shifted by
  `file_area_real_offset − file_area_virtual_offset − tgc_header_size` with **u32-wrapping**
  arithmetic (Dolphin relies on the wrap cancelling out). Entry count is clamped to the
  FST's actual size; a missing/unreadable FST is tolerated as empty, like Dolphin.

No decompression is involved — reads are served straight from the file with the patches
applied, and hostile header values are clamped before any allocation.

## NFS

Wii U–era encrypted images (the "EGGS" container). The whole image is AES-128-CBC
encrypted with a console key; data is organised in LBA ranges. Header is 0x200 bytes;
multi-byte integers big-endian.

```
0x000  magic        "EGGS"
0x010  range count  u32 BE (capped at 61)
0x014  LBA ranges:  { start u32 BE, num u32 BE } × count,
       each unit = one 0x8000-byte block
```

Decoding:

- The AES key comes from the sibling file `code/htk.bin` (16 bytes) next to the `content`
  directory — NFS only opens when the file's directory is named `content` (Dolphin's
  convention). `Blob.Open(stream, nfsKey, …)` bypasses the lookup.
- The logical size is `0x200 + total_blocks × 0x8000`; each 0x8000-byte block is decrypted
  with **IV = 8 zero bytes + 8-byte big-endian block index** (index counted over the whole
  image), except block 0 which uses the "0x61 hack" (the IV is derived from the header byte
  at 0x61, matching the real disc data).
- Images larger than one file continue in `hif_000000.nfs`, `hif_000001.nfs`, …; each file
  holds 0xFA00000 bytes (0x1F40 blocks), and the last 0x200 bytes of every full file belong
  to the next file's header region (the original disc layout has no gap there).
- Reads beyond the last range (or beyond the files) decode as zeroes.
