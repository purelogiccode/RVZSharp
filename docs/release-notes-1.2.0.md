# RVZSharp 1.2.0 Release Notes

RVZSharp 1.2.0 is a **security, robustness and tooling release**. The public API stays
compatible with 1.0.0 (checked at pack time against the published package via
`PackageValidationBaselineVersion`).

## Highlights

### CLI defaults match Dolphin

`convert` no longer requires every option to be spelled out:

- `-f` defaults to **RVZ** (DolphinTool's default).
- `-b` defaults to **128 KiB** for GCZ/RVZ and **2 MiB** for WIA/CISO/WBFS.
- `-c` defaults to **Zstandard** for RVZ and **LZMA2** for WIA.
- `-l` defaults to **5**.

A plain `rvzsharp convert -i game.iso -o game.rvz` now produces a valid RVZ file. Explicit
options still override every default, and `convert -h` documents the values.

### Hostile-input hardening

Every parser now treats the container as untrusted input:

- WIA/RVZ disc-struct sizes are bounded before allocation, and compressor-data bounds are
  validated before the SHA-1 is checked.
- TGC extraction reads TMD/certificate/H3 sizes as **unsigned 32-bit** values (files above
  2 GiB no longer silently produce empty files), caps the relocated FST, and computes DOL
  segment ends without `uint` wrap-around.
- GCZ rejects oversized block tables and unrepresentable disc sizes before allocating the
  offset/hash arrays.
- NFS caps continuation files and validates every file index before reading a block.
- FST parsing enforces directory-depth, path-length and total-path-memory budgets.
- `SectionStream` and `MultiPartStream` reject overflowing sections, invalid positions and
  checked seek arithmetic.
- Raw-data table sizes that overflow when sector-aligned are rejected.
- PRNG seed/size/count arguments and PURGE output sizes are validated.
- CLI extraction and listing stop at a bounded directory depth, so a hostile FST cannot
  overflow the process stack.

### Diagnostics and bug reports

- Every **warning, error and fatal** Serilog event is forwarded to the bug-report API;
  expected user/environment failures stay at Information and never become bug reports.
- Bug reports always contain the required **Environment Details**, **Error Details** and
  **Exception Details** sections, with every variable field bounded so the 4,000-character
  API limit cannot truncate a required section.
- Unhandled exceptions and unobserved task exceptions are captured at the top level and
  flushed before exit.
- Log initialization tolerates an unwritable temp directory, and bug-report delivery is
  best-effort even when the transport faults.

### CI, documentation and packaging

- CodeQL `security-extended`, dependency review, cross-platform CI, and a DocFX API
  reference published under `/api/`.
- New `SECURITY.md` and contributing/security documentation; `docs/` is published to both
  GitHub Pages and the GitHub Wiki with a side menu.
- **613 library tests** on each of `net8.0`/`net9.0`/`net10.0`, plus **97 CLI tests** and the
  276-test real-file slow suite.

## CLI downloads (1.2.0 bundles)

The 1.2.0 bundles are **framework-dependent single-file** executables: install the
[.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) once, then unzip and
run. The .NET runtime is not embedded.

| Asset | Platform |
|---|---|
| `rvzsharp_v1.2.0_win-x64.zip` | Windows 10/11, x64 |
| `rvzsharp_v1.2.0_win-arm64.zip` | Windows 11, arm64 |
| `rvzsharp_v1.2.0_linux-x64.zip` | Linux, x64 |
| `rvzsharp_v1.2.0_linux-arm64.zip` | Linux, arm64 |
| `rvzsharp_v1.2.0_osx-x64.zip` | macOS, Intel |
| `rvzsharp_v1.2.0_osx-arm64.zip` | macOS, Apple Silicon |

Each bundle holds the single-file executable (`RVZSharp` / `RVZSharp.exe`) plus `LICENSE`,
`README.md`, `WhatsNew.md` and `THIRD-PARTY-NOTICES.md`. Verify downloads against
`SHA256SUMS.txt` on the release page. The `rvzsharp_v<version>_<rid>.zip` naming is the
convention documented for update downloads and matches the GitHub release assets.

## NuGet

The library package is published as **RVZSharp 1.2.0** for `net8.0`, `net9.0` and
`net10.0`, with the SPDX 2.2 SBOM embedded:

```bash
dotnet add package RVZSharp --version 1.2.0
```

## Upgrade notes

- No public API breaks relative to 1.0.0.
- CLI defaults changed: scripts that relied on `convert` failing when `-f`, `-b`, `-c` or
  `-l` was omitted should pass the options explicitly.
- The CLI bundles still require the .NET 10 runtime; use the NuGet package from a .NET 8 or
  .NET 9 application when you need library support on those frameworks.
