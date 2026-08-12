# Compression & packing

## Compression methods

`compression` field of the disc struct:

| ID | Name | Read support | Write support | Notes |
|---|---|---|---|---|
| 0 | None | ✔ | ✔ | identity |
| 1 | Purge | ✔ (WIA) | ✖ (RVZ) | WIA-only; rejected by RVZ readers |
| 2 | Bzip2 | ✔ | ✔ | level 1–9 |
| 3 | LZMA | ✔ | ✔ | LZMA1, 7-Zip properties |
| 4 | LZMA2 | ✔ | ✔ | raw LZMA2 chunk framing |
| 5 | Zstd | ✔ | ✔ | level 1–22 (default 3) |

### `compr_data` (disc struct offset 0xD5)

| Method | Properties |
|---|---|
| None / Purge / Bzip2 / Zstd | empty |
| LZMA | 5 bytes: `lc + lp·9 + pb·45` (7-Zip default `0x5D`) + 4-byte little-endian dictionary size |
| LZMA2 | 1 byte: dictionary-size property (2^(prop/2+12) style, 7-Zip table) |

The writer's LZMA dictionary size depends on the level: `1<<18` (≤1), `1<<20` (≤3),
`1<<23` (≤5), `1<<25` (≤7), `1<<27` otherwise; position bits 2, literal context 3,
literal position 0.

### Stream formats written by RVZSharp

- **LZMA1**: the 7-Zip encoder's output (range-coder data) after the 5-byte properties;
  the properties travel in `compr_data`, not in the stream. The encoder runs with a known
  size (no end marker) — both Dolphin and RVZSharp decode with a known output size.
- **LZMA2**: a sequence of chunks, each `[control][unpack_size-1:2][pack_size-1:2][props:1]
  [LZMA1 stream]`, ended by a `0x00` control byte. Chunk payloads are capped at 0xF800
  bytes and each chunk resets the dictionary (control `0xE0 | (size-1)>>16`). The single
  props byte (0x5D) must match the `compr_data` property.

## Group compression semantics

- Lists + data are compressed together for methods > Purge; otherwise the lists are stored
  raw (padded to 4) ahead of the compressed stream.
- The `compressed` flag (bit 31 of the group size) is set only when compression shrank the
  data.
- All-zero chunks with no exceptions become **zero groups** (size 0) — nothing is stored.

## RVZ packing (junk detection)

Wii discs are full of *padding* — pseudo-random junk produced by Nintendo's generator.
Instead of storing it, RVZ stores a **seed** that regenerates the junk, plus segment
headers. Packing is optional (`Packing` option / `--no-packing`); a file without packing is
perfectly valid.

### The junk PRNG

- Lagged Fibonacci generator: `k = 521`, `j = 32`, XOR combination.
- Seed: 17 big-endian `u32` words (68 bytes). Words 17..520 are derived by
  `word[i] = (word[i-17] << 23) ^ (word[i-16] >> 9) ^ word[i-1]`, then 4 warm-up advances.
- Output byte order per word: `>>24`, `>>18` (not `>>16` — this is the famous oddity), `>>8`,
  `>>0`.
- The stream position is defined as **`offset % 0x8000`**: junk at disc offset *P* equals
  the generator output starting at position `P % 0x8000`. This is why discs' junk looks
  periodic per 32 KiB.

### Segment stream

A packed chunk is a sequence of segments; `rvz_packed_size` is the total bytes of the
segment stream (headers included):

| Segment | Layout |
|---|---|
| literal | `u32 BE size` (no MSB) + `size` literal bytes |
| junk | `u32 BE size | 0x80000000` + 68-byte seed (junk regenerated, not stored) |

Special case: if a chunk contains **no junk at all**, the writer stores the whole chunk
literally **without any headers** and `rvz_packed_size` stays 0 — readers treat 0 as "no
packing".

The reader processes segments sequentially and skips the PRNG by the *running* data offset
(`(chunkStart + bytesEmitted) % 0x8000`) before each junk segment.

### Seed recovery (`GetSeed`)

`LaggedFibonacciGenerator.GetSeed(data, size, dataOffsetMod)` implements Dolphin's reverse
algorithm:

1. Quick filter: the first 521 words must satisfy the generator's bit-structure property
   (bits 22–23 == bits 20–21 of the swapped word); random data fails almost always.
2. Fill the 521-word state from the junk and rewind it to position 0 (partial `Backward`
   for the word offset, full `Backward` per 521-word cycle).
3. Reconstruct the 17-word seed (undoing the transform and recovering bits 16–17 from the
   neighbouring words), re-derive words 17..520 and verify them.
4. Re-advance to the junk position and count how many bytes the seed actually reproduces.

`GetSeed` is **best-effort**: junk regions that fail the checks (or diverge) are stored
literally. The writer scans forward in 32 KiB-aligned steps with a running data offset
(`offset % 0x8000`), records zero runs as *zero-junk* when uncompressed, and emits segments
per chunk.

Recovered seeds are *canonical*: the writer's generator and the reader's PRNG agree on them
even though their internal word formats differ (the reconstruction makes the seed invariant
under the output transform).

## Why Zstd by default?

Dolphin's default for RVZ is Zstandard — best ratio/speed trade-off for game data, and the
only method that consistently beats LZMA2 on random-ish data. LZMA2 is the strongest
compressor for highly compressible data; Bzip2 is a middle ground; None is useful for
speed-critical round trips.
