# RVZ container format

RVZ is the modern Dolphin disc container (WIA's successor). This page documents the on-disk
layout as implemented and verified by RVZSharp. The authoritative upstream description is
`References/dolphin-master/docs/WiaAndRvz.md`; the C++ code in
`References/dolphin-master/Source/Core/DiscIO/WIABlob.cpp` is the reference implementation.

All integers are big-endian.

## File layout

```
┌───────────────────────────────────────────┐
│ File head                (0x48 bytes)     │
├───────────────────────────────────────────┤
│ Disc struct              (0xDC bytes)     │
├───────────────────────────────────────────┤
│ Partition table          (uncompressed)   │
├───────────────────────────────────────────┤
│ Raw data table           (compressed)     │
├───────────────────────────────────────────┤
│ Group table              (compressed)     │
├───────────────────────────────────────────┤
│ Group data               (4-byte aligned) │
└───────────────────────────────────────────┘
```

## File head (0x48 bytes)

| Offset | Size | Field | Meaning |
|---|---|---|---|
| 0x00 | 4 | magic | `52 56 5A 01` (`"RVZ\x01"`); WIA uses `"WIA\x01"` |
| 0x04 | 4 | version | written as `0x01000000` |
| 0x08 | 4 | version_compatible | `0x00030000` (RVZ), `0x00080000` (WIA) |
| 0x0C | 4 | disc_size | size of the disc struct, `0xDC` |
| 0x10 | 20 | disc_hash | SHA-1 over the disc struct bytes |
| 0x24 | 8 | iso_file_size | decoded ISO length |
| 0x2C | 8 | rvz_file_size | container file length |
| 0x34 | 20 | file_head_hash | SHA-1 over bytes `0x00..0x34` of this head |

The reader accepts the file when `ImplementedVersion (0x01000000) >= version_compatible`.

## Disc struct (0xDC bytes)

| Offset | Size | Field | Meaning |
|---|---|---|---|
| 0x00 | 4 | disc_type | `1` = GameCube, `2` = Wii |
| 0x04 | 4 | compression | 0 None, 1 Purge (WIA only), 2 Bzip2, 3 LZMA, 4 LZMA2, 5 Zstd |
| 0x08 | 4 | compr_level | signed compression level |
| 0x0C | 4 | chunk_size | power of two, 0x8000–0x200000 |
| 0x10 | 0x80 | disc_header | first 0x80 bytes of the disc image (disc header) |
| 0x90 | 4 | num_partitions | count of partition-table entries |
| 0x94 | 4 | partition_entry_size | `0x30` |
| 0x98 | 8 | partition_entries_offset | file offset of the partition table |
| 0xA0 | 20 | partition_entries_hash | SHA-1 over `num_partitions × partition_entry_size` bytes |
| 0xB4 | 4 | num_raw_data_entries | count of raw-table entries |
| 0xB8 | 8 | raw_data_entries_offset | file offset of the raw table |
| 0xC0 | 4 | raw_data_entries_size | **compressed** size of the raw table |
| 0xC4 | 4 | num_groups | count of group-table entries |
| 0xC8 | 8 | group_entries_offset | file offset of the group table |
| 0xD0 | 4 | group_entries_size | **compressed** size of the group table |
| 0xD4 | 1 | compr_data_len | length of `compr_data` |
| 0xD5 | 7 | compr_data | codec properties (see [Compression](compression-packing.md)) |

The raw and group tables are compressed with the disc's own compression method (decompressed
size = `num_raw_data_entries × 24` and `num_groups × 12` respectively); their SHA-1s are
verified over the **decompressed** bytes. The partition table is stored uncompressed.

## Partition table

Each entry is 0x30 bytes:

| Offset | Size | Field |
|---|---|---|
| 0x00 | 16 | `part_key` — the partition's title key |
| 0x10 | 16 | data entry 0 (segment 0) |
| 0x20 | 16 | data entry 1 (segment 1) |

Each 16-byte data entry:

| Offset | Size | Field | Meaning |
|---|---|---|---|
| 0x00 | 4 | first_sector | disc sector where this segment starts |
| 0x04 | 4 | number_of_sectors | segment length in sectors (0x8000-byte units) |
| 0x08 | 4 | group_index | first group of this segment in the group table |
| 0x0C | 4 | number_of_groups | group count (informational; readers recompute) |

A partition with no FST still produces two data entries; an entry with
`number_of_sectors == 0` is skipped by readers.

## Raw data table

Each entry is 24 bytes:

| Offset | Size | Field |
|---|---|---|
| 0x00 | 8 | data_offset — disc-relative byte offset |
| 0x08 | 8 | data_size |
| 0x10 | 4 | group_index |
| 0x14 | 4 | padding |

Readers align `data_offset` down to the sector size and grow `data_size` by the same amount
(that is how the first raw entry, which starts at `0x80`, covers the disc header bytes).

## Group table

Each entry is 12 bytes:

| Offset | Size | Field |
|---|---|---|
| 0x00 | 4 | data_offset — file offset **divided by 4** |
| 0x04 | 4 | data_size; bit 31 = stored compressed |
| 0x08 | 4 | rvz_packed_size — packed segment-stream length, or 0 |

A `data_size` of 0 means a **zero group**: the chunk decodes to all zeroes and nothing is
stored.

## Groups

A group is the stored form of one chunk:

```
┌───────────────────────────┐
│ exception lists           │   (see below)
├───────────────────────────┤
│ main data                 │   literal payload or packed segment stream
└───────────────────────────┘
```

- For compression methods **> Purge** (Zstd/Bzip2/LZMA/LZMA2), the exception lists are
  prepended to the main data **before** compression and are part of the compressed stream.
- For **None** (and Purge in WIA), the lists are stored uncompressed before the (possibly
  compressed) main data, and the last list is padded to a 4-byte boundary.
- The group is flagged `compressed` only when the compressed size is smaller than the
  uncompressed size; otherwise the uncompressed bytes are stored.

### Exception lists

```
u16 count
count × { u16 hash_offset, 20 bytes SHA-1 }
```

- `hash_offset` addresses a 20-byte hash inside the region's 0x400-byte hash area:
  `block_index_in_chunk × 0x400 + offset_in_block`.
- Offsets are **chunk-relative**; the reader adds the chunk's offset within its 2 MiB region
  (`additional_offset = (chunkPayloadOffset % 0x1F0000) / 0x7C00 × 0x400`).
- The number of lists per chunk is `max(1, chunkPayloadSize / 0x1F0000)` (one for every
  supported chunk size).

## Chunking and regions

- The disc is divided into **chunks** of `chunk_size` bytes (power of two, 32 KiB–2 MiB).
- Raw data: one chunk per group.
- Partition data: regions of **64 sectors = 2 MiB**; a chunk maps to one region for 2 MiB
  chunks, or to a sub-region slice for smaller chunks.
- A Wii sector is 0x8000 bytes: 0x400 bytes of hash area + 0x7C00 bytes of data. See
  [Wii partitions](wii-partitions.md).

## Reading order (writer's output)

1. Validate the file head (magic, versions, `disc_hash`, `iso_file_size`).
2. Load and hash-check the three tables.
3. Serve reads by locating the covering area:
   - raw areas → decompress group → unpack segments (if `rvz_packed_size > 0`) → slice;
   - partition areas → decompress group → unpack segments → rebuild the region
     (decrypt payload, recompute hash tree, apply exceptions, AES re-encrypt).
