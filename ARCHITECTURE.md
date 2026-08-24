# Architecture

Gatehouse has one readiness engine and two user interfaces. It is local-first and read-only for connected GitHub repositories.

## System flow

```text
GitHub API -> normalized pull request snapshot -> local SQLite cache
                                                  |
repository policy -> deterministic readiness engine
                                                  |
                         web UI <- shared result -> CLI and JSON
```

The GitHub adapter collects facts. The domain engine applies policy. This split stops provider response order, UI state, or cache behavior from changing a verdict.

## Projects

| Project | Role |
| --- | --- |
| `Gatehouse.Domain` | Pure readiness rules, statuses, blockers, and reports. It has no provider or storage dependency. |
| `Gatehouse.Application` | Shared contracts, policy parsing, filters, JSON documents, validation, and synthetic demo data. |
| `Gatehouse.Infrastructure` | Bounded GitHub API reads, normalization, SQLite persistence, retention, and migrations. |
| `Gatehouse.Web` | Loopback-only ASP.NET Core and Blazor UI plus the versioned local API. |
| `Gatehouse.Cli` | Command parsing, terminal and JSON output, packaged tool host, and the web `serve` entry point. |

Tests follow the same boundaries. Domain tests prove the decision table. Application and integration tests prove parsing, storage, provider handling, and local API behavior. Browser tests prove the main UI path and screenshot.

## Important decisions

### Deterministic rules

Gatehouse does not use a language model to decide readiness. The same snapshot and policy must give the same ordered result. Missing or calculating evidence stays `UNKNOWN`.

### Read-only GitHub access

Gatehouse sends GET requests to GitHub REST endpoints. When a token is present, it also sends one GraphQL POST query for each open pull request. These POSTs contain queries, not mutations. The client base address must be exactly `https://api.github.com/`. The application has no code to merge, comment, push, or change repository settings.

### Local storage

SQLite stores configured repository names, normalized pull request evidence, policy, reports, refresh metadata, and cache history. The default path uses the operating system local application data directory. Schema changes use Entity Framework Core migrations.

### Shared host

The `Gatehouse` tool package includes the web host. `gatehouse serve` and the source web project use the same service setup and local API. Kestrel listens on loopback interfaces only.

### Local mutation guard

The interactive UI uses `GatehouseUiService` to change local Gatehouse data. The separate local HTTP API supports other local clients. Each mutating API request must include `X-Gatehouse-Request: 1`. Interactive UI requests also use ASP.NET Core antiforgery protection. This header is a local request guard, not user authentication.

### No shell execution

The pull request view can show a suggested `git worktree` command. It is text only. Gatehouse never runs it or any other shell command.

## Trust boundaries

- GitHub titles, logins, labels, branch names, URLs, and provider errors are untrusted input.
- Repository names, policy files, command arguments, JSON bodies, query filters, paths, and ports are validated or bounded.
- Razor encodes displayed text. External evidence links must be absolute HTTPS URLs before the UI renders them.
- Terminal text removes control and Unicode format characters.
- Response sizes, page counts, pull request counts, retries, request counts, request bodies, and ports have limits.

## Versioned contracts

- CLI and readiness JSON use `schemaVersion: "1.0"`.
- The local HTTP API is under `/api/v1`.
- The JSON schema is in `schemas/readiness-v1.schema.json`.
- Package and product version 1.0.0 use Git tag `v1.0.0`.

See [the readiness model](docs/READINESS_MODEL.md), [privacy](docs/PRIVACY.md), and [security](SECURITY.md).
