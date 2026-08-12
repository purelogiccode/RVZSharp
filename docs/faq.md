# FAQ

## Formats

**What is RVZ?**
Dolphin's modern disc container (introduced 2022, successor to WIA): partition data is
stored decrypted with hash exceptions, PRNG junk is packed with a recovered seed, and every
table carries SHA-1 checksums. RVZ files are typically smaller than the source formats and
decode byte-exactly to the original disc.

**What is the difference between RVZ and WIA?**
WIA is the older container (Purge/Bzip2/LZMA/LZMA2, no Zstd, no junk packing, version
compatible 0x00080000). RVZ adds Zstd, chunk packing and the `rvz_packed_size` field, and
drops Purge. RVZSharp reads both; writes RVZ.

**Can Dolphin open files created by RVZSharp?**
The writer mirrors Dolphin's `ConvertToWIAOrRVZ` byte-for-byte at the container level
(same headers, tables, exception offsets, group layout), so yes — Dolphin should open
converted files. Cross-checking with real Dolphin is part of the open validation work.

**What are GCZ/CISO/WBFS/TGC/NFS?**
Legacy GameCube/Wii image formats (compressed GCZ; simple block-compressed CISO/WBI;
Wii Backup File System; Tiny GameCube images; encrypted Wii U–era EGGS/NFS images). All
are decoded to the same canonical ISO view — see [Legacy formats](format/legacy.md).

## Usage

**How do I verify a conversion?**
```bash
rvzsharp decode game.rvz game.iso --sha1 $(sha1sum game.iso | cut -d' ' -f1)
```
or, for an original source image: compute the SHA-1 of the source ISO once, then use
`--sha1` on every decode. The suite itself proves byte-exactness via round trips.

**Why is WBFS conversion slow?**
WBFS has a fixed ~9.4 GiB logical size; converting reads the whole logical image even when
the file is mostly empty clusters. Prefer `decode` then `convert game.iso`.

**Why does NFS need a `content` directory?**
Dolphin treats NFS files as `…/content/hif_000000.nfs` with the AES key in
`…/code/htk.bin`. RVZSharp follows the same convention; use `Blob.Open(stream, nfsKey, …)`
or the library API to supply the key directly.

**Which compression should I use?**
`zstd` (default) for most cases; `lzma2` for the best ratio on compressible data; `none`
for speed. Levels 1–9 (Zstd up to 22).

## Technical

**Does RVZSharp decrypt games?**
It decrypts *partition data with the key found in the image itself* (the ticket's title
key) purely to re-encode it compactly — the decoded ISO is byte-identical to the original
encrypted disc. No console keys are used or required.

**Why does the junk look periodic every 32 KiB?**
The padding PRNG's stream position is defined as `offset % 0x8000`; junk at any offset is
the generator output at that remainder. That's the format's definition (Dolphin, the Go
reader and RVZSharp all agree), and it's what makes seed-based packing possible.

**What happens if junk detection fails?**
`GetSeed` is best-effort: non-PRNG data is stored literally. The output stays fully valid
— just less compressed.

**Why `CSharp_RVZSharp.slnx` and not `.sln`?**
The project uses the XML solution format; the legacy `.sln` parser in the .NET CLI rejects
it (`MSB5010`). Keep the `.slnx` extension.

**How are warnings handled?**
`TreatWarningsAsErrors` is enabled — the build must stay at zero warnings.

**Where do the format details come from?**
Dolphin's C++ source (`References/dolphin-master`, especially
`Source/Core/DiscIO/WIABlob.cpp` and `WIACompression.cpp`) is the source of truth; the Go
reader (`References/rvz-1.0.3`) and `docs/WiaAndRvz.md` are cross-references. See
[Testing](testing.md) for how the semantics are pinned by tests.
