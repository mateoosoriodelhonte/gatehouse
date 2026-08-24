# GitHub authentication

Gatehouse can read public GitHub.com repositories without a token, subject to GitHub's lower anonymous rate limit. Private repositories and higher request limits need a token.

## Recommended token

Use a fine-grained personal access token when it supports your account and repository. GitHub recommends fine-grained tokens because they can be limited to one owner, selected repositories, and selected permissions.

Choose:

- the owner that holds the repository;
- only the repositories Gatehouse must read;
- the shortest useful expiration; and
- read access only.

Gatehouse reads pull requests, reviews, review threads, issue links, branches, commits, checks, commit statuses, and repository metadata. Grant only the read permissions GitHub requires for those facts. Permission names can vary by token type and can change. GitHub notes that fine-grained personal access tokens do not support every Checks API case. If a fine-grained token omits needed evidence for your account, use an organization-approved GitHub App when possible. A classic token with the `repo` scope has broad read and write power over repositories even though Gatehouse makes only read requests. Use that fallback only when necessary, with a short lifetime and careful process isolation.

Current GitHub references:

- [Managing personal access tokens](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens)
- [Fine-grained token permissions](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens)
- [Keeping API credentials secure](https://docs.github.com/en/rest/authentication/keeping-your-api-credentials-secure)

## Set the token

Gatehouse reads only `GATEHOUSE_GITHUB_TOKEN`.

macOS or Linux:

```bash
export GATEHOUSE_GITHUB_TOKEN="github_pat_..."
gatehouse status OWNER/REPOSITORY
unset GATEHOUSE_GITHUB_TOKEN
```

PowerShell:

```powershell
$env:GATEHOUSE_GITHUB_TOKEN = "github_pat_..."
gatehouse status OWNER/REPOSITORY
Remove-Item Env:GATEHOUSE_GITHUB_TOKEN
```

The token is read when the process starts. Restart `gatehouse serve` after you change it.

## Storage and output

Gatehouse does not save the token in SQLite, config, logs, JSON, reports, or web responses. It sends the token as an authorization header only to `https://api.github.com/`.

Do not put the token:

- in `.gatehouse.yml`;
- in `--data` or another command argument;
- in a shell history file;
- in source control;
- in a screenshot, report, issue, or test fixture; or
- in a shared process environment.

If access fails, check the selected repository, owner approval, expiration, and read permissions. Revoke and replace a token if you think it leaked.
