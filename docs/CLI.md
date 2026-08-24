# CLI reference

The `gatehouse` command uses the same local store, readiness engine, and report model as the dashboard.

## Commands

```text
gatehouse repo add OWNER/REPOSITORY [--config PATH] [--json]
gatehouse status OWNER/REPOSITORY [filters] [--json] [--cached]
gatehouse ready OWNER/REPOSITORY [filters] [--json] [--cached]
gatehouse pr OWNER/REPOSITORY NUMBER [--json] [--cached]
gatehouse report OWNER/REPOSITORY NUMBER [--json] [--cached]
gatehouse serve [--port 5341]
gatehouse version [--json]
```

Run `gatehouse help` for the built-in summary.

### `repo add`

Validates and stores a GitHub `OWNER/REPOSITORY`. It selects the new repository and uses `--config PATH`, the closest `.gatehouse.yml` found from the current directory upward, or safe defaults in that order. Config files are limited to 64 KiB. A duplicate is an input error.

### `status`

Refreshes the configured repository, applies filters, and lists open pull requests. `--cached` skips GitHub and uses saved evidence. A cached request with no evidence returns exit 3.

### `ready`

Works like `status` but shows only `GO` items. A valid empty result is successful and returns exit 0.

### `pr`

Shows one open pull request, its blockers, evidence, next action, and URL.

### `report`

Writes the deterministic Markdown readiness packet for one open pull request. With `--json`, it writes the same readiness document as `pr --json`.

### `serve`

Starts the dashboard on loopback. The port must be from 1024 through 65535. The default is 5341.

### `version`

Writes the three-part Gatehouse version.

## Demo mode

Demo mode uses the synthetic `acme/payments` repository and makes no network request:

```bash
gatehouse status --demo
gatehouse status --demo --json
gatehouse pr 201 --demo
gatehouse report 201 --demo
```

The fixed demo has five pull requests that cover all readiness statuses.

## Filters

Filters apply to `status` and `ready`. Text matching is case-insensitive.

| Option | Accepted value |
| --- | --- |
| `--status` | `go`, `review`, `blocked`, `draft`, or `unknown` |
| `--search` | title text or a pull request number such as `#201` |
| `--author` | valid GitHub login |
| `--label` | label text |
| `--branch` | head branch text |
| `--reviewer` | requested user or team text |
| `--ci` | `all`, `passing`, `blocked`, `pending`, or `notrun` |
| `--draft` | `all`, `ready`, or `draft` |

Text filter values have a 100-character limit and cannot contain control characters.

## Data path

The command chooses the database in this order:

1. global `--data PATH` option;
2. `GATEHOUSE_DATA_PATH`; or
3. `Gatehouse/gatehouse.db` under the operating system local application data directory.

The option is global and can appear before or after the command:

```bash
gatehouse --data ./artifacts/team-a.db status --demo
gatehouse status --demo --data ./artifacts/team-a.db
```

Gatehouse creates and migrates the database when a command needs the store. Demo, help, and version commands do not need it.

## JSON contract

JSON goes to standard output. Messages and errors go to standard error. This keeps standard output safe for a parser.

All current JSON documents use `schemaVersion: "1.0"`. A `status --json` response is an envelope:

```json
{
  "schemaVersion": "1.0",
  "repository": "acme/payments",
  "pullRequestCount": 5,
  "pullRequests": []
}
```

Each pull request document contains:

- `schemaVersion`, `repository`, and `pullRequest` identity;
- `status`, `summary`, and `nextAction`;
- `evaluatedAt` and `policyVersion`;
- ordered `blockers` with type, impact, summary, optional check, and optional URL; and
- ordered `evidence` with stable ID, label, outcome, summary, and optional URL.

The formal pull request document schema is [readiness-v1.schema.json](../schemas/readiness-v1.schema.json). A future incompatible contract will use a new schema version.

## Exit codes

| Code | Meaning |
| ---: | --- |
| 0 | Command completed and its selected result is ready or informational. |
| 2 | At least one selected result is not `GO` and no result is `UNKNOWN`. |
| 3 | At least one selected result has `UNKNOWN` evidence, or the requested cache has no evidence. |
| 64 | Invalid command, option, repository, filter, policy, or pull request selection. |
| 69 | GitHub did not provide current evidence because of access, rate limit, or provider failure. |
| 70 | Local I/O or an internal operation failed. |
| 130 | The command was cancelled. |

For `status`, exit 3 takes priority over exit 2. `ready` returns 0 after a valid filter even when it finds no `GO` item. `pr` and `report` return the selected pull request's result code.

## Automation example

```bash
set +e
gatehouse status OWNER/REPOSITORY --json > readiness.json
code=$?
set -e

case "$code" in
  0) echo "All selected changes are ready" ;;
  2) echo "A known gate blocks or waits" ;;
  3) echo "Required evidence is unknown" ;;
  *) echo "Gatehouse could not evaluate readiness" >&2; exit "$code" ;;
esac
```

Treat versioned JSON fields as the machine contract. Do not parse the human text format.
