# Dokumentsystem

A self-hosted, free and open-source alternative to Google Drive, in the spirit of Immich. Built to run on a home NAS.

Status: roadmap M0 (foundations). The design is in [docs/design.md](docs/design.md).

## Run it on a NAS

You need Docker with Compose v2.

1. Copy `docker-compose.yml` and `.env.example` into a folder on the NAS, and rename `.env.example` to `.env`.
2. In `.env`, set `DB_PASSWORD`, your `PUID`/`PGID` (`id` shows them), and the data locations. Keep `DB_DATA_LOCATION` on a local disk, not a network share.
3. Run `docker compose up -d` and open `http://<nas>:8080`.

Optional services are Compose profiles: `--profile office` (Office previews through Gotenberg), `--profile ml` (machine learning, from roadmap M7) and `--profile clamav` (virus scanning, not used in v1).

## Develop

Requirements: .NET 10 SDK, Node 22.18 or later with pnpm (`corepack enable`), and Docker.

```sh
pnpm install
dotnet tool restore
dotnet run --project src/Dokument.AppHost
```

The Aspire dashboard opens with Postgres (built from `docker/postgres`), the server, a worker and the Vite dev server. Local files go to `.data/`.

| Task | Command |
| --- | --- |
| Build and test .NET | `dotnet build` · `dotnet test` |
| Lint, typecheck, test, build the web side | `pnpm lint` · `pnpm typecheck` · `pnpm test` · `pnpm build` |
| Refresh the API client after an endpoint change | `dotnet build` then `pnpm --filter @dokument/api generate` |
| Add a migration | `dotnet ef migrations add <Name> -p src/Dokument.Infrastructure -s src/Dokument.Host -o Persistence/Migrations` |

Design tokens live in `docs/design-system/tokens.json` and are built by `packages/tokens`. Don't edit the generated files in `packages/tokens/dist`.

## License

[AGPL-3.0](LICENSE)
