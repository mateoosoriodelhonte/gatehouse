# Readiness model

Gatehouse evaluates a normalized pull request snapshot against a repository policy. The UI, CLI, reports, and JSON output use the same pure engine.

## Statuses

| Status | Meaning |
| --- | --- |
| `GO` | Every configured gate passed. |
| `REVIEW` | No hard failure exists, but a known gate is still waiting. |
| `BLOCKED` | A known failure prevents merge consideration. |
| `DRAFT` | The pull request is explicitly marked as a draft. |
| `UNKNOWN` | Required evidence is missing, calculating, or not applicable to an open pull request. |

For an open, non-draft pull request, precedence is `BLOCKED`, then `UNKNOWN`, then `REVIEW`, then `GO`. A draft is always `DRAFT`. A pull request that is not open is `UNKNOWN`.

## Evidence rules

- A merge conflict is blocking when policy requires mergeability.
- GitHub mergeability `unknown` remains `UNKNOWN`; it is not converted to a failure.
- Failed, cancelled, and action-required checks remain distinct blocking reasons.
- Pending and not-executed checks remain distinct waiting reasons.
- Unknown check state produces `UNKNOWN`.
- Review comments are not approvals.
- Changes requested are blocking when policy enables that gate.
- A possible issue reference does not satisfy an explicit-link requirement.
- A behind branch is advisory unless policy requires freshness.
- No checks is a fact, not an invented failure.

Rules and blockers use stable identifiers and stable sorting. Provider response order cannot change the verdict or report.

## Machine-readable output

The current contract uses `schemaVersion: "1.0"`. See [`schemas/readiness-v1.schema.json`](../schemas/readiness-v1.schema.json).
