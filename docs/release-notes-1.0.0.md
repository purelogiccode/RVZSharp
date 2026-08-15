# RVZSharp 1.0.0 Release Notes

A pure managed **.NET** library and CLI for reading/writing **Dolphin RVZ** disc images and
decoding the legacy GameCube/Wii formats — byte-for-byte, cross-checked against real games
and the official No-Intro SHA-1s.

```
dotnet add package RVZSharp
```

## What's included

- **RVZ/WIA reader** — full container validation (magic, versions, every SHA-1, structure
  rules) and random-access decoding back to the original disc image, byte-for-byte
- **Legacy decoders** — GCZ, CISO/WBI, WBFS (incl. split files), TGC, NFS, plain ISO — all
  auto-detected through one `Blob.Open(...)`
- **RVZ writer** — converts any supported image to RVZ like Dolphin does: all five codecs
  (None, BZip2, LZMA1, LZMA2, Zstd incl. negative "fast" levels), chunk sizes
  32 KiB–4 MiB, PRNG-junk packing with seed recovery, `--scrub`, progress reporting and
  cancellation
- **Wii partitions** — SHA-1 hash trees (h0/h1/h2), hash exceptions, AES-128-CBC —
  decrypted/re-encrypted exactly like Dolphin produces
- **Targets**: `net8.0`, `net9.0`, `net10.0` — 100% managed, no native code
- **Fully documented API** — XML docs on every public/internal type and member (shipped in
  the package)

## Quick start

```csharp
using RVZSharp;
using RVZSharp.Blobs;

using var blob = Blob.Open(@"game.rvz");   // any format, auto-detected
using var reader = RvzReader.Open(file);
var iso = reader.ReadFully();              // byte-exact ISO

using var output = File.Create(@"game.rvz");
RvzWriter.Write(Blob.Open(@"game.iso"), output,
    new RvzWriteOptions { Compression = CompressionType.Zstd, CompressionLevel = 5 });
```

CLI (`rvzsharp`):

```
rvzsharp header  -i game.rvz
rvzsharp verify  -i game.rvz -a sha1
rvzsharp convert -i game.iso -o game.rvz -f rvz -c zstd -l 5 -b 131072
```

## Trust, verified

- **410 tests** — 313 synthetic round-trips (~30 s) plus a 97-test real-file suite decoding
  actual GameCube/Wii RVZ images against their official No-Intro DAT SHA-1s and re-encoding
  them back to the same hash, on machines with the games mounted
  (`F:\Nintendo GameCube` / `F:\Nintendo Wii`)
- Audited against Dolphin's implementation and the `rvz-1.0.3` Go reference; every
  divergence pinned by a regression test
- Cross-platform: single-file self-contained binaries for Windows and Linux (x64 + arm64);
  the library runs on any platform the runtime supports

## Install & documentation

| | |
|---|---|
| NuGet | [`RVZSharp 1.0.0`](https://www.nuget.org/packages/RVZSharp/1.0.0) |
| Docs | the `docs/` wiki — format specs, library API, CLI reference |
| Repository | [github.com/purelogiccode/RVZSharp](https://github.com/purelogiccode/RVZSharp) |

## License

**GPL-2.0-or-later** — the RVZ/WIA format logic derives from
[Dolphin](https://github.com/dolphin-emu/dolphin), and the library is licensed under the
same terms Dolphin uses. All dependencies and third-party components (MIT LZMA decoder port,
MIT/public-domain runtime codecs, BSD Go reference) are credited in
`THIRD-PARTY-NOTICES.md`.

## Assets

| File | Size |
|---|---|
| `rvzsharp_v1.0.0_win-x64.zip` | 31.97 MB |
| `rvzsharp_v1.0.0_win-arm64.zip` | 30.57 MB |
| `rvzsharp_v1.0.0_linux-x64.zip` | 31.92 MB |
| `rvzsharp_v1.0.0_linux-arm64.zip` | 30.08 MB |