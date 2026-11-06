# Contributing

Contributions are welcome: bug reports, format-compatibility fixes, documentation and tests.

## Development setup

1. Install the **.NET SDK 10.0** (the repository's `global.json` rolls forward to the latest
   major).
2. Clone the repository and restore:

   ```bash
   git clone https://github.com/purelogiccode/RVZSharp.git
   cd RVZSharp
   dotnet restore CSharp_RVZSharp.sln
   ```

3. Build and run the fast tests:

   ```bash
   dotnet build CSharp_RVZSharp.sln -c Release
   dotnet test CSharp_RVZSharp.sln -c Release
   ```

The slow/differential suite is opt-in because it needs real images and `DolphinTool.exe`:

```bash
dotnet test RVZSharp.Slow.Tests -c Release
```

## Repository conventions

- The **library** (`RVZSharp/`) has no logging, telemetry or UI dependencies. Serilog,
  ApplicationStats and bug reporting belong to `RVZSharp.Cli/` only.
- Warnings are errors and analyzers are enabled; the build must stay clean on
  `net8.0`, `net9.0` and `net10.0`.
- Asynchronous methods end in `Async`; library async methods take a `CancellationToken`.
- Public API is XML-documented and must remain compatible with the published package
  baseline. Additive changes are preferred.
- Match the reference behaviour in `References/dolphin-master/` and `References/rvz-1.0.3/`
  when changing container parsing or compression.

## Adding tests

| Change | Test project |
|---|---|
| Library parsing, codecs, containers | `RVZSharp.Tests` |
| CLI options, update check, logging | `RVZSharp.Cli.Tests` |
| Real files or Dolphin differential | `RVZSharp.Slow.Tests` |

Use the synthetic builders in `RVZSharp.Tests/Helpers/` rather than committing large binary
fixtures. Internals are already visible to the test projects.

## Logging rules (CLI)

- Expected user/environment failures are logged at **Information**.
- Warnings, errors and fatal events are forwarded to the bug-report API.
- stdout is reserved for command results; diagnostics go to stderr or Serilog.
- Never block a command for telemetry or update checks.

See [`AGENTS.md`](https://github.com/purelogiccode/RVZSharp/blob/master/AGENTS.md) for the
complete contract and [`security.md`](security.md) for vulnerability reporting.

## Documentation

`docs/` is the single source for the GitHub Pages site and the GitHub Wiki. Add new pages to
`docs/_data/nav.yml` and `docs/_Sidebar.md`. The DocFX API reference is generated from XML
comments and published under `/api/`.

## Pull requests

1. Keep the change focused.
2. Add or update tests and documentation.
3. Run the fast suites locally.
4. Make sure CI, CodeQL and dependency review are green.
5. Describe user-visible changes and any compatibility impact.
