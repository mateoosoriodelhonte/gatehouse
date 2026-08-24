# Local development

## Requirements

- .NET SDK 10.0.400 or a later 10.0 patch allowed by `global.json`
- PowerShell and Chromium for browser tests
- Git for source control only; Gatehouse itself never runs Git

No paid service is needed. Tests and demo mode do not need a token.

## Restore, build, and test

Run these commands from the repository root:

```bash
dotnet restore Gatehouse.slnx --locked-mode
dotnet format Gatehouse.slnx --no-restore --verify-no-changes
dotnet build Gatehouse.slnx --configuration Release --no-restore
pwsh tests/Gatehouse.BrowserTests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test Gatehouse.slnx --configuration Release --no-build
dotnet list Gatehouse.slnx package --vulnerable --include-transitive
```

CI installs Chromium system dependencies on Linux. Use `install --with-deps chromium` there.

## Run Gatehouse

```bash
dotnet run --project src/Gatehouse.Cli -- status --demo
dotnet run --project src/Gatehouse.Cli -- serve --port 5341
```

Open <http://localhost:5341>. The dashboard starts with synthetic data available. Demo mode makes no network request.

To use a real repository, set a read-only token when needed and add it:

```bash
export GATEHOUSE_GITHUB_TOKEN="your-token"
dotnet run --project src/Gatehouse.Cli -- repo add OWNER/REPOSITORY
dotnet run --project src/Gatehouse.Cli -- status OWNER/REPOSITORY
```

See [GitHub authentication](GITHUB_AUTH.md). Never commit a token.

## Local data

The default file is `Gatehouse/gatehouse.db` under the operating system local application data directory. Common roots are:

- macOS: `~/Library/Application Support`
- Linux: the .NET local application data directory for the current account
- Windows: `%LOCALAPPDATA%`

Use an explicit test file when you need isolation:

```bash
dotnet run --project src/Gatehouse.Cli -- \
  --data ./artifacts/dev/gatehouse.db status --demo
```

`GATEHOUSE_DATA_PATH` is the fallback when `--data` is absent. Stop Gatehouse before you copy or delete its database files.

## Policy

`repo add` searches the current directory and each parent directory for `.gatehouse.yml`. `--config PATH` selects an exact file. A missing file uses the safe defaults. See [policy](POLICY.md).

## Pack and smoke test

Build first, then put the five related packages in one source directory:

```bash
mkdir -p artifacts/packages artifacts/tool
dotnet pack src/Gatehouse.Domain/Gatehouse.Domain.csproj -c Release --no-build -o artifacts/packages
dotnet pack src/Gatehouse.Application/Gatehouse.Application.csproj -c Release --no-build -o artifacts/packages
dotnet pack src/Gatehouse.Infrastructure/Gatehouse.Infrastructure.csproj -c Release --no-build -o artifacts/packages
dotnet pack src/Gatehouse.Web/Gatehouse.Web.csproj -c Release --no-build -o artifacts/packages
dotnet pack src/Gatehouse.Cli/Gatehouse.Cli.csproj -c Release --no-build -o artifacts/packages
dotnet tool install Gatehouse --tool-path artifacts/tool --version 1.0.0 \
  --add-source artifacts/packages --ignore-failed-sources
artifacts/tool/gatehouse version
artifacts/tool/gatehouse status --demo --json
```

On Windows, run `artifacts/tool/gatehouse.exe`.

## CI proof

Every pull request and main push runs locked restore, format, Release build, all tests, tool packing, installed-tool checks, browser checks, and a vulnerable dependency scan. Package smoke jobs also install and run the exact uploaded packages on macOS and Windows.
