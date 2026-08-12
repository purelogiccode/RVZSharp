# Third-party notices

## SharpCompress (MIT) — LZMA/LZMA2 decoder

The files under `src/RVZSharp/Compression/Lzma/` are adapted from
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

## NuGet packages

| Package | License |
|---|---|
| ZstdSharp.Port (zstd, pure managed) | MIT |
| SharpZipLib (bzip2) | MIT |
| LZMA-SDK (test-only, LZMA1 test vectors) | public domain / BSD-style |
