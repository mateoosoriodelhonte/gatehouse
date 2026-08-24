# Security policy

## Supported version

Gatehouse 1.0.x receives security fixes. Use the latest 1.0.x release.

## Report a vulnerability

Use a [private GitHub security advisory](https://github.com/mateoosoriodelhonte/gatehouse/security/advisories/new). Do not open a public issue. Include the affected version, impact, repeat steps, and any safe proof. Do not include a real token or private repository data.

The maintainers will confirm the report, assess its severity, and plan a fix. Public details will wait until users can update.

## Security model

Gatehouse is a single-user local tool. It is not an internet service and has no user account or remote administration model.

- The web server binds only to localhost on IPv4 and IPv6.
- The GitHub client accepts only `https://api.github.com/` as its base address.
- GitHub operations are read-only. The GraphQL POST contains a query, not a mutation.
- The token comes only from `GATEHOUSE_GITHUB_TOKEN`. Gatehouse does not save or return it.
- Demo mode uses fixed synthetic data and makes no GitHub request.
- The application does not run Git, a shell, hooks, or repository code.
- Razor encodes untrusted text. The UI accepts only absolute HTTPS evidence links.
- CLI text strips control and Unicode format characters before terminal output.
- Repository names, policy, filter input, JSON bodies, paths, and ports have validation or size limits. The web request body limit and CLI policy file limit are each 64 KiB.
- GitHub requests have request, page, item, response-size, retry, and time limits.
- Local API writes need the exact `X-Gatehouse-Request: 1` header. Interactive UI posts also use ASP.NET Core antiforgery protection.
- Non-development API errors return a generic problem response.
- Information-level HTTP client logs are suppressed so private repository paths do not enter normal process logs.

The local mutation header is not authentication. It reduces accidental cross-site writes to the local API. The loopback binding remains the main network boundary.

## Local data and secrets

Gatehouse stores repository names, pull request metadata, policy, reports, refresh state, and cache history in SQLite. The default Unix directory and database modes allow only the current user. A custom existing directory keeps its current mode. Windows access follows the directory access control list.

Gatehouse does not encrypt the database. A local user or process with access to your account or files can read it. Use operating system disk encryption, lock the computer, and clear local data before you give the computer or database to another person.

Set the token only for the process or trusted shell session. Give it read-only access to the fewest repositories and the shortest useful lifetime. Revoke it if it may have leaked. See [GitHub authentication](docs/GITHUB_AUTH.md).

## Private repository metadata

Private names, titles, authors, labels, branches, review state, file paths, and links can be sensitive. They remain in the local database and process output. Gatehouse has no telemetry. Do not attach the database, terminal output, reports, or screenshots to a public issue without review.

Use Settings > Clear local data, or delete the configured database while Gatehouse is stopped, to remove the local cache. See [privacy](docs/PRIVACY.md) for the data list and paths.

## Scope limits

Gatehouse does not protect a compromised computer, operating system account, browser, shell, .NET runtime, dependency, or GitHub account. It does not replace GitHub branch protection or a repository security review.
