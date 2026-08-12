# Wii partitions

Wii disc images contain encrypted partitions. RVZ stores partition data **decrypted** to
make it compress well, plus *hash exceptions* — the minimal set of original hash values
that differ from a freshly recomputed hash tree. This page describes the machinery;
`PartitionRegionBuilder` (reader) and `WiiPartitionExtractor` (writer) implement it.

## Disc header facts

| Offset | Field | Meaning |
|---|---|---|
| 0x18 | u32 BE | `0x5D1C9EA3` = Wii disc (partition table offset, shifted <<2, normally 0x10000 → 0x40000) |
| 0x1C | u32 BE | partition table entry count |
| 0x60 | byte | `0` = hash trees present |
| 0x61 | byte | `0` = partition data encrypted |
| 0x1C (GC) | u32 BE | `0xC2339F3D` = GameCube DVD magic (no partitions) |

The writer only applies partition conversion when the disc has Wii magic **and** hashes
**and** encryption; otherwise everything is stored as raw data (still byte-exact).

## Partition table (at 0x40000)

Four groups, each `[count u32 BE][table_offset u32 BE << 2]` at `0x40000 + 8·group`; each
entry is `[partition_offset u32 BE << 2][type u32 BE]`.

The partition header (at `partition_offset`) holds:

| Offset | Field |
|---|---|
| 0x00 | ticket: signature type — `0x10001` (RSA2048) |
| 0x1BF | 16-byte title key |
| 0x2B8 | data_offset (u32 BE, << 2) |
| 0x2BC | data_size (u32 BE, << 2) |
| 0x424 | FST offset within the partition (u32 BE, << 2) |
| 0x428 | FST size (u32 BE, << 2) |

The partition data lives at `partition_offset + data_offset`, `data_size` bytes long.
Invalid partitions (wrong alignment, zero/undersized size) are encoded as raw data instead.

## Sector layout

```
0x0000 ┌───────────────────────┐
       │ hash area   (0x400)   │  AES-128-CBC, IV = 16 zero bytes
0x0400 ├───────────────────────┤
       │ data area   (0x7C00)  │  AES-128-CBC, IV = ciphertext at 0x3D0
0x3D0  │ … (IV source) …       │
0x8000 └───────────────────────┘
```

A region is 64 sectors (2 MiB) of encrypted data. The final region of a partition is
usually partial; missing sectors are **zero-filled** (both for hashing and for output), and
the corresponding disc bytes beyond the partition are covered by raw data areas instead.

## Hash tree

Inside the 0x400-byte hash area:

| Offset | Size | Content |
|---|---|---|
| 0x000 | 0x26C | `h0`: 31 × SHA-1 over each 0x7C00 data block of the sector |
| 0x26C | 0x14 | padding |
| 0x280 | 0xA0 | `h1`: 8 × SHA-1, one per sector of the 8-sector group; `h1[j] = SHA1(h0 of sector 8g+j)` |
| 0x320 | 0x20 | padding |
| 0x340 | 0xA0 | `h2`: 8 × SHA-1, one per 8-sector group of the region; `h2[g] = SHA1(h1 array of group g)` |
| 0x3E0 | 0x20 | padding |

The `h1`/`h2` arrays are replicated into every sector's hash area of their group/region.
For sectors beyond the partition end, `h0` is hashed over 0x26C zero bytes (this convention
is shared by Dolphin, the Go reader and RVZSharp).

## Hash exceptions

When the writer stores decrypted data, the original encrypted hash area is *not* stored.
Instead it compares, per sector, every 20-byte stride of the six fields above
(`h0`, padding, `h1`, padding, `h2`, padding — with partial final strides) between the
**decrypted original** hashes and the **recalculated** tree, and stores an exception for
each difference:

```
u16 hash_offset = block_index_in_chunk × 0x400 + offset_in_block
20 bytes        = the original hash
```

- Exceptions are only recorded for sectors that actually exist (the zero-filled tail never
  produces exceptions for itself; its `h1`/`h2` mismatches on the *real* sectors do, which
  is expected and correct).
- Stored offsets are **chunk-relative**; the reader converts them to region-relative with
  `additional_offset = (chunkPayloadOffset % 0x1F0000) / 0x7C00 × 0x400` before matching a
  sector (`offset >> 10 == sector % 64`).

The reader rebuilds a sector as: decrypt payload → recompute tree → apply exceptions →
AES-128-CBC re-encrypt (hash area with zero IV, data area with the encrypted hash bytes at
0x3D0 as IV) — byte-identical to the original disc.

## FST split

Dolphin's converter splits each partition into two data entries at the end of the FST
(rounded up to a 2 MiB boundary relative to the partition data start). The split keeps the
FST and its referenced files in the first entry; RVZSharp replicates it:

```
split_point = data_start + alignUp(fst_end - data_start, 2 MiB)
segment 0   = [data_start, split_point)
segment 1   = [split_point, data_end)
```

Both segments are stored decrypted with their own exception lists and group ranges.
