# Dokumentsystem

A self-hosted, free and open-source alternative to Google Drive, in the spirit of Immich. Built to run on a home NAS.

Status: roadmap M0 (foundations). The design is in [docs/design.md](docs/design.md).

## Run it on a NAS

You need Docker with Compose v2.

1. Copy `docker-compose.yml` and `.env.example` into a folder on the NAS, and rename `.env.example` to `.env`.
2. In `.env`, set `DB_PASSWORD`, your `PUID`/`PGID` (`id` shows them), and the data locations. Keep `DB_DATA_LOCATION` on a local disk, not a network share.
   - For sign-in, set `PUBLIC_URL` and the `OIDC_*` values for your identity provider ([design §5](docs/design.md#5-authentication-and-sessions)).
3. Run `docker compose up -d` and open `http://<nas>:8080`.

Optional services are Compose profiles: `--profile office` (Office previews through Gotenberg), `--profile ml` (machine learning, from roadmap M7) and `--profile clamav` (virus scanning, not used in v1).

## Develop

Requirements: .NET 10 SDK, Node 22.18 or later with pnpm (`corepack enable`), and Docker.

```sh
pnpm install
dotnet tool restore
dotnet run --project src/Dokument.AppHost
```

The Aspire dashboard opens with Postgres (built from `docker/postgres`), Keycloak, the server, a worker and the Vite dev server. Local files go to `.data/`. Open http://localhost:5173 and sign in as `alice`/`alice` (admin), `bob`/`bob` or `carol`/`carol`. pgAdmin is at http://localhost:5050, and the API reference (Scalar) is at `/scalar` on the server; the dashboard links both.

Your data survives restarts: the database lives in the Docker volume `dokumentsystem-postgres` and files in `.data/`. Keycloak keeps nothing and imports the realm on every start, so the test users have fixed ids in `src/Dokument.AppHost/keycloak/dokument-realm.json`; without them each start would sign you in as a new, empty person. To start over, stop Aspire and delete both:

```sh
docker volume rm dokumentsystem-postgres
rm -rf .data        # PowerShell: Remove-Item -Recurse -Force .data
```

The worker runs from the app's Docker image (its `runtime` stage, without the web UI), so exiftool, Tesseract and libvips come with it and nothing more needs installing. Aspire rebuilds the image on each start, which takes a minute or two after a change to the worker code.

To debug the worker, run it as a project instead with `dotnet run --project src/Dokument.AppHost -- --WorkerOnHost=true`. It then needs the tools on your machine:

- Debian or Ubuntu: `sudo apt install libimage-exiftool-perl tesseract-ocr tesseract-ocr-swe tesseract-ocr-eng`
- macOS: `brew install exiftool tesseract tesseract-lang`
- Windows: `winget install OliverBetz.ExifTool UB-Mannheim.TesseractOCR`, and tick Swedish under additional languages in the Tesseract installer. That installer doesn't add Tesseract to PATH, so either add `C:\Program Files\Tesseract-OCR` to PATH or set `Worker__TesseractCommand` to the full path of `tesseract.exe`. `Worker__ExifToolCommand` does the same for exiftool.

Without the tools the worker still runs. It logs a warning and skips what the missing tool would have read, then reads those files on the first start after the tool is installed.

The integration tests run against our Postgres image, so build it once first: `docker build -t dokumentsystem-postgres:ci docker/postgres`.

| Task | Command |
| --- | --- |
| Build and test .NET | `dotnet build` · `dotnet test` |
| Format the C# code (CSharpier, checked in CI) | `dotnet csharpier format .` |
| Lint, typecheck, test, build the web side | `pnpm lint` · `pnpm typecheck` · `pnpm test` · `pnpm build` |
| Browse the UI components and the clickable mockup (Storybook) | `pnpm --filter @dokument/ui dev`, then open http://localhost:6006 |
| Refresh the API client after an endpoint change | `dotnet build` then `pnpm --filter @dokument/api generate` |
| Add a migration | `dotnet ef migrations add <Name> -p src/Dokument.Infrastructure -s src/Dokument.Host -o Persistence/Migrations` |

Design tokens live in `docs/design-system/tokens.json` and are built by `packages/tokens`. Don't edit the generated files in `packages/tokens/dist`.

## License

[AGPL-3.0](LICENSE)
