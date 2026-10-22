# @dokument/api

`openapi.json` is written by `dotnet build` of `src/Dokument.Host`. After changing an endpoint, rebuild the
server and run `pnpm --filter @dokument/api generate` to refresh `src/schema.d.ts`. CI fails if either is stale.
