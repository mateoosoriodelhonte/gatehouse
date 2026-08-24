# Repository policy

Gatehouse separates GitHub facts from repository rules. The same pull request can have a different verdict under a different policy, while the underlying snapshot stays unchanged.

Create `.gatehouse.yml` in the repository that runs Gatehouse:

```yaml
readiness:
  require_linked_issue: false
  require_all_checks: true
  require_approval: true
  require_no_unresolved_threads: true
  require_mergeable: true
  require_current_branch: false
  block_on_changes_requested: true
```

These are the safe defaults. They are defaults, not claims about every GitHub repository. Each boolean can be changed for the selected repository.

`gatehouse repo add` uses `--config PATH` when given. Otherwise, it searches the current directory and each parent for `.gatehouse.yml`. If it finds no file, it uses the safe defaults. The policy is saved with the configured repository.

The CLI does not fetch this file from the selected GitHub repository. The dashboard policy editor changes the policy stored in local SQLite. It does not edit `.gatehouse.yml`.

Gatehouse V1 supports one `readiness` mapping with the documented boolean keys. The file limit is 64 KiB. It fails closed on unknown roots, unknown keys, duplicate keys, invalid values, and unindented entries. It does not implement the full YAML language.

| Key | Effect |
| --- | --- |
| `require_linked_issue` | Requires an explicit GitHub issue link. Text references do not pass. |
| `require_all_checks` | Evaluates every reported check. When false, only checks marked required are gates. |
| `require_approval` | Requires GitHub to report an approved review decision and at least one approval. |
| `require_no_unresolved_threads` | Blocks when unresolved review threads remain. |
| `require_mergeable` | Requires GitHub to report a clean merge state. |
| `require_current_branch` | Blocks a branch that is behind its base. |
| `block_on_changes_requested` | Blocks when the current review decision requests changes. |

Gatehouse does not claim that every reported check is required. When `require_all_checks` is `false`, it gates only checks that GitHub marks as required. If GitHub cannot expose the required-check set, Gatehouse keeps all checks visible and reports the missing fact.
