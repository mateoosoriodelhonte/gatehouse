# Gatehouse

Know what is actually ready to merge — and what is blocking everything else.

Gatehouse is a local-first, read-only GitHub engineering change-readiness dashboard. The project is under active development toward v1.0.0.

## Development

Gatehouse uses the .NET 10 LTS SDK pinned in `global.json`.

```bash
dotnet restore Gatehouse.slnx
dotnet build Gatehouse.slnx --configuration Release
dotnet test Gatehouse.slnx --configuration Release
```

The application will bind to loopback interfaces only. Gatehouse does not require paid services and does not mutate connected repositories.
