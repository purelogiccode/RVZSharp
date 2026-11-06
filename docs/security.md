# Security

RVZSharp processes untrusted disc images and is published as a library and a CLI. This page
summarises how to report a vulnerability, which versions are supported, and the protections
built into the repository.

## Supported versions

| Version | Supported |
|---|---|
| 1.2.x | Yes - active development |
| 1.1.x | Security fixes only |
| 1.0.x | Security fixes only |
| Older | No |

## Reporting a vulnerability

Do **not** open a public issue for a security problem. Use GitHub private vulnerability
reporting:

<https://github.com/purelogiccode/RVZSharp/security/advisories/new>

Please include the affected version, operating system, a minimal reproducer or crafted image,
the impact, and any suggested fix. We acknowledge reports within 5 business days and aim to
ship a coordinated fix within 90 days.

The full policy is in [`SECURITY.md`](https://github.com/purelogiccode/RVZSharp/blob/master/SECURITY.md)
at the repository root.

## What is in scope

- Memory safety, integer overflow, unbounded allocation and denial-of-service issues in the
  readers/writers (RVZ, WIA, GCZ, CISO/WBI, WBFS, TGC, NFS, ISO).
- Path traversal or arbitrary file writes during `extract`.
- Container validation that accepts malicious offsets and later reads out of bounds.
- Update-check, telemetry or bug-report handling that leaks secrets or follows attacker URLs.
- CI/release supply-chain weaknesses.

## Repository protections

| Control | Where |
|---|---|
| CodeQL C# analysis (push, PR, weekly) | `.github/workflows/codeql.yml` |
| Dependency review on pull requests | `.github/workflows/dependency-review.yml` |
| Dependabot updates + security updates | `.github/dependabot.yml` |
| Secret scanning and push protection | GitHub repository settings |
| API compatibility and package validation | `RVZSharp/RVZSharp.csproj` |
| SPDX SBOM on Release builds | `Microsoft.Sbom.Targets` |
| Least-privilege CI permissions | `.github/workflows/*.yml` |
| Path-traversal-safe extraction | `Program.IsSafePathSegment`, `ExtractDirectory` |
| Bounded, validated parsing | `RvzReader`, `ChunkDecoder`, `ExceptionListParser` |

## Telemetry

The CLI sends an anonymous launch hit to ApplicationStats and forwards warning-or-higher
logs to the bug-report API. Reports contain the environment details from `AGENTS.md` and
exception stack traces, but no disc content or file paths. The library performs **no**
network I/O. Set `RVZSHARP_NO_UPDATE_CHECK=1` to disable the GitHub update check.

## Safe harbor

Good-faith research is welcome when it stays in scope, avoids privacy violations and service
disruption, uses your own test data, and allows a reasonable fix window before disclosure.
