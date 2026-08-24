# Contributing

Thank you for helping Gatehouse.

## Before you start

Open or reuse a GitHub issue. State the result, limits, and proof needed. Keep GitHub access read-only and keep the readiness engine deterministic.

Use a short branch from current `main`. Keep each pull request focused. Do not put tokens, private repository data, customer data, or copied production responses in code, tests, screenshots, logs, or issues.

## Local checks

Install the .NET SDK version in `global.json`, then run:

```bash
dotnet restore Gatehouse.slnx --locked-mode
dotnet format Gatehouse.slnx --no-restore --verify-no-changes
dotnet build Gatehouse.slnx --configuration Release --no-restore
dotnet test Gatehouse.slnx --configuration Release --no-build
dotnet list Gatehouse.slnx package --vulnerable --include-transitive
```

Browser tests need Chromium:

```bash
pwsh tests/Gatehouse.BrowserTests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test tests/Gatehouse.BrowserTests/Gatehouse.BrowserTests.csproj \
  --configuration Release --no-build
```

See [local development](docs/LOCAL_DEVELOPMENT.md) for package and demo checks.

## Pull requests

A pull request must:

- link its issue;
- explain user-visible behavior and security or privacy effects;
- add or update tests for changed behavior;
- update public docs and the changelog when needed;
- pass CI; and
- include a screenshot when the UI changes.

Use synthetic fixtures. Keep tests independent of a live token or private repository. A maintainer reviews and merges changes.

## Design rules

- Put provider-independent readiness behavior in `Gatehouse.Domain`.
- Normalize GitHub facts before the domain engine sees them.
- Keep UI, CLI, JSON, and reports on the shared result.
- Preserve stable rule identifiers, sorting, schema versions, and exit codes.
- Treat absent evidence as absent. Do not invent a pass or failure.
- Do not add GitHub write operations or arbitrary command execution.

## Conduct and security

Follow the [Code of Conduct](CODE_OF_CONDUCT.md). Report a security issue as described in [SECURITY.md](SECURITY.md), not in a public issue.
