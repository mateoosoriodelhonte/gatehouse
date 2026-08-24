# Privacy

Gatehouse is local-first. It has no telemetry, analytics, advertising, cloud account, or Gatehouse service.

## Network use

Demo mode makes no GitHub or external network request. The dashboard still uses browser-to-localhost traffic. For a real repository, Gatehouse sends bounded read requests to `https://api.github.com/`. GitHub receives the normal request metadata, such as your IP address, user agent, selected repository, and token when set. GitHub's privacy terms apply to that traffic.

The web dashboard listens only on localhost. It is not published to the local network or internet.

## Data kept on the computer

The SQLite database can contain:

- GitHub owner and repository names;
- pull request number, title, author, state, branches, labels, reviewers, and links;
- check, review, thread, issue-link, merge, file, and change-size facts;
- normalized readiness results, blockers, evidence, and reports;
- repository policy;
- refresh time, cache age, rate-limit facts, ETag, warnings, and history; and
- the selected repository and local database schema state.

The browser stores the light or dark theme choice in local storage under `gatehouse-theme`. It stores no token there.

Private repository metadata remains private only if you protect this database and any copied output. The database is not encrypted by Gatehouse.

Gatehouse does not store the GitHub token. It reads `GATEHOUSE_GITHUB_TOKEN` from the process environment and sends it only as an authorization header to GitHub.

## Logs and output

The web host writes normal ASP.NET Core application logs to its process output. Gatehouse suppresses Information-level HTTP client logs because request URLs contain repository metadata. The CLI writes command data to standard output and errors to standard error. Gatehouse does not intentionally log the authorization header or GitHub response bodies. Reports, JSON, screenshots, and terminal output can still contain repository metadata.

Review output before you share it. Never attach a private database or token to a public report.

## Retention and deletion

The default cache keeps recent evidence under the configured retention and snapshot limits. Defaults are 30 days and 50 snapshots per pull request. A successful refresh prunes older history.

Use Settings > Clear local data to clear all Gatehouse records. Removing one repository clears its cached records. You can also stop Gatehouse and delete the database file and its SQLite sidecar files.

The default database is `Gatehouse/gatehouse.db` under the operating system local application data directory. `--data PATH` or `GATEHOUSE_DATA_PATH` can select another file for the CLI.

Clearing Gatehouse does not remove data already copied to logs, shell history, screenshots, backups, or other tools. GitHub keeps data under its own terms.
