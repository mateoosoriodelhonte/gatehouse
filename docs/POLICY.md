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

Gatehouse V1 supports one `readiness` mapping with the documented boolean keys. It fails closed on unknown roots, unknown keys, duplicate keys, invalid values, and unindented entries. It does not implement the full YAML language.

| Key | Effect |
| --- | --- |
| `require_linked_issue` | Requires an explicit GitHub issue link. Text references do not pass. |
| `require_all_checks` | Evaluates every reported check. When false, only checks marked required are gates. |
| `require_approval` | Requires GitHub to report an approved review decision and at least one approval. |
| `require_no_unresolved_threads` | Blocks when unresolved review threads remain. |
| `require_mergeable` | Requires GitHub to report a clean merge state. |
| `require_current_branch` | Blocks a branch that is behind its base. |
| `block_on_changes_requested` | Blocks when the current review decision requests changes. |
