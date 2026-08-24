# Changelog

All notable Gatehouse changes are recorded here.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-08-24

### Added

- Deterministic readiness statuses for checks, reviews, merge state, linked issues, branch freshness, and draft state.
- Repository policy from safe defaults or a strict `.gatehouse.yml` file.
- Bounded, read-only GitHub REST and GraphQL collection with retry and partial-data handling.
- Local SQLite cache, migrations, retention, repository management, and data clearing.
- Loopback-only dashboard with repository, overview, pull request, batch triage, report, policy, and settings views.
- Versioned local API and `schemaVersion: "1.0"` JSON documents.
- `gatehouse repo add`, `status`, `ready`, `pr`, `report`, `serve`, and `version` commands.
- Synthetic demo mode, stable exit codes, filters, plain reports, and one installable .NET tool package.
- Domain, application, integration, CLI, accessibility, browser, package, and vulnerability checks in CI.
- Public architecture, security, privacy, authentication, CLI, development, and contribution documentation.

### Security

- Restricted the web host to loopback and GitHub traffic to `https://api.github.com/`.
- Kept GitHub tokens in process memory and out of Gatehouse storage and responses.
- Added input, response-size, pagination, request-count, retry, link, terminal, and local mutation controls.
- Suppressed Information-level HTTP client logs that could contain private repository paths.

[1.0.0]: https://github.com/mateoosoriodelhonte/gatehouse/releases/tag/v1.0.0
