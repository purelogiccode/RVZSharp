## License and attribution

RVZSharp is copyright (c) 2025-2026 Peterson Fernandes and Pure Logic Code, licensed under
the **GNU General Public License, version 2 or later** (`LICENSE`). The RVZ/WIA container
logic is derived from Dolphin's DiscIO module ([Dolphin](https://github.com/dolphin-emu/dolphin),
GPL-2.0-or-later), so RVZSharp is distributed under the same terms Dolphin itself uses.

## SharpCompress (MIT) — LZMA/LZMA2 decoder

The files under `RVZSharp/Compression/Lzma/` are adapted from
[SharpCompress](https://github.com/adamhathcock/sharpcompress)
(`src/SharpCompress/Compressors/LZMA/`), Copyright (c) 2011-2025 Adam Hathcock and contributors,
licensed under the MIT License:

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

Adaptations: decode-only paths retained; namespaces renamed to `RVZSharp.Compression.Lzma`;
`SharpCompressException`/`InvalidFormatException` replaced with local `IOException`-derived
exceptions; the async/encoder code paths were removed. The decoder core (range coder, LZMA state
machine) originates from the public-domain LZMA SDK by Igor Pavlov.

## NuGet packages (runtime dependencies of the package)

| Package | Used for | License |
|---|---|---|
| LZMA-SDK 22.1.1 | LZMA1/LZMA2 **encoder** (`LzmaEncoder`) | MIT (7-Zip LZMA SDK, © Igor Pavlov) |
| SharpZipLib 1.4.2 | BZIP2 encoder/decoder | MIT |
| ZstdSharp.Port 0.8.8 | Zstandard encoder/decoder | MIT |

## Reference material (NOT shipped)

The `References/` directory is **not** part of the NuGet package and contains no runtime code:

- `References/dolphin-master/` — Dolphin source code, **GPL-2.0-or-later**; the source of the
  format logic this library implements (see License and attribution above).
- `References/rvz-1.0.3/` — the Go RVZ reader (© Matt Dainty, **BSD 3-Clause**); used as a
  cross-check reference, including the `testdata/` No-Intro DAT SHA-1s used by the test suite.