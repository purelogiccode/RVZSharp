# Security Policy — RVZSharp

RVZSharp reads and writes untrusted disc images. Security reports are taken seriously and
are handled privately until a fix is available.

## Supported versions

| Version | Supported |
|---|---|
| 1.1.x | :white_check_mark: Active |
| 1.0.x | :white_check_mark: Security fixes only |
| &lt; 1.0 | :x: Not supported |

The latest release is published on [GitHub Releases](https://github.com/purelogiccode/RVZSharp/releases)
and [NuGet](https://www.nuget.org/packages/RVZSharp/). Upgrade to the newest supported version
before reporting an issue whenever possible.

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Use GitHub's private vulnerability reporting:

1. Open <https://github.com/purelogiccode/RVZSharp/security/advisories/new>.
2. Include the affected version, operating system, a minimal reproducer or crafted image,
   the impact, and any suggested fix.
3. If you cannot use GitHub, open a minimal public issue that says only that you have a
   security report and ask the maintainer for a private channel — do not include details.

You can expect:

- Acknowledgement within **5 business days**.
- An initial assessment and severity classification within **10 business days**.
- A coordinated fix and release for confirmed issues. The default disclosure window is
  **90 days** from the initial report, or sooner when a fix ships.

Credit is given in the release notes unless you ask to remain anonymous.

## Scope

In scope:

- Memory-safety, buffer, integer-overflow, allocation or denial-of-service issues in the
  RVZ/WIA/GCZ/CISO/WBFS/TGC/NFS/ISO readers and writers.
- Path traversal or arbitrary file write/delete during `extract` (crafted FST names, `-s`
  paths, partition names, output folders).
- Incorrect validation that accepts a malicious container and later reads/writes out of
  bounds or trusts attacker-controlled offsets.
- Update-check, telemetry or bug-report handling that leaks secrets, executes untrusted
  content, or follows an attacker-controlled URL.
- CI/release supply-chain issues (workflow permissions, artifact substitution, dependency
  confusion).

Out of scope:

- Bugs that require the user to run a malicious executable on their own machine.
- Missing hardening on a format the library already rejects safely.
- Denial of service from intentionally huge but valid images on a machine with insufficient
  memory/disk (report it as a performance issue instead).
- Vulnerabilities in unsupported versions or in third-party dependencies that are already
  fixed upstream (report those to the dependency maintainer; Dependabot tracks them here).

## Security measures in this repository

- **CodeQL** (`.github/workflows/codeql.yml`) analyses C# on every push and pull request and
  on a weekly schedule.
- **Dependency review** (`.github/workflows/dependency-review.yml`) blocks pull requests that
  introduce high/critical vulnerable dependencies.
- **Dependabot** tracks NuGet and GitHub Actions updates, with security updates enabled.
- **Secret scanning and push protection** are enabled for the repository.
- **NuGet package validation** checks API compatibility and target-framework assets; Release
  builds embed an SPDX SBOM.
- **Least-privilege workflows**: CI has read-only contents; only the release workflow gets
  `contents: write`, and only on tags.
- Untrusted disc data is treated as hostile: offsets and sizes are validated, allocations are
  bounded, and extraction rejects path segments that could escape the output folder.

## Telemetry and privacy

The CLI sends two best-effort requests:

- **ApplicationStats**: an anonymous launch hit (`applicationId`, `version`) to
  `https://www.purelogiccode.com/ApplicationStats/stats`, rate-limited server-side.
- **Bug reports**: warning-and-above log events to
  `https://www.purelogiccode.com/bugreport/api/send-bug-report`, including the environment
  details listed in `AGENTS.md` and any exception stack trace.

No disc content, file names, paths, user names or email addresses are intentionally sent.
The update check queries the public GitHub Releases API. The update check can be disabled
with `RVZSHARP_NO_UPDATE_CHECK=1`; to avoid all outbound traffic, run the CLI with network
access blocked or use the library directly (the library performs no network I/O).

## Safe harbor

We will not pursue legal action for good-faith security research that:

- stays within the scope above,
- avoids privacy violations, data destruction and service disruption,
- uses your own test images and accounts, and
- gives us a reasonable opportunity to fix the issue before disclosure.

## Related documents

- [Contributing](docs/contributing.md)
- [Agent and contributor guide](AGENTS.md)
- [Security page on the documentation site](docs/security.md)
