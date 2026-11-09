# Dokumentsystem — Design Document

> Status: **Draft v0.50** · Last updated: 2026-11-09
>
> A self-hosted, free and open-source alternative to Google Drive, in the spirit of
> [Immich](https://immich.app). Built to run on a home NAS with Docker.

---

## 1. Goals and non-goals

### Goals

- Store, organize, preview and search documents and media from **web (desktop and mobile)** and a **React Native app**.
- **Originals are sacred:** the uploaded bytes are stored exactly as received. EXIF and other metadata are never modified.
- **Searchable everything:** filename, extracted text, OCR of scans and images, and later semantic search.
- Folders, **sharing of personal folders**, access levels, **file versioning**, **trash**, **audit log**.
- **Quotas** per user, managed by admins.
- **Easy NAS deployment:** `docker compose up` with mounted volumes, sensible defaults, low resource usage.
- An **optional, remote machine-learning service** (as in Immich) that can run on a more powerful machine.

### Non-goals (explicitly out of scope)

- **Document editing** (docs, spreadsheets, presentations). We preview; we do not edit.
- **Our own authentication.** Identity comes from an external OIDC provider.
- **Team / shared drives.** Every item is owned by one person.
- **End-to-end encryption.** It would prevent thumbnails, OCR and search. Rely on disk encryption on the NAS.
- **Desktop sync client.** WebDAV (later) covers most of that need.
- **Mobile camera-roll backup.** That is Immich's job.

### Target audience

A household plus extended family: roughly 2–20 users, reachable over the internet through a reverse proxy.
Scale target: up to about 1M files and a few TB per instance.

---

## 2. Decision summary

| Area | Decision |
|---|---|
| Backend | .NET (current LTS), ASP.NET Core, EF Core + Npgsql |
| Topology | Modular monolith: **one image, two roles** (`server`, `worker`), plus a separate **ML image** |
| Database | PostgreSQL in our own image, bundling **ParadeDB `pg_search`** (BM25) and **VectorChord** (vectors) |
| Job queue | Postgres-backed (`SKIP LOCKED`). No Redis. |
| Auth | OIDC (code + PKCE) for identity, then **app-issued sessions** and API keys |
| Storage | **App-owned layout.** The DB is the source of truth for the tree, and blobs are content-addressed by SHA-256. |
| Versioning | Yes. Uploading over an existing file creates a new version. |
| Sharing | Per-user grants on files and folders (Viewer / Editor), plus public links |
| Uploads | Resumable uploads using the tus protocol |
| Search | BM25 (pg_search) over names and extracted text, with optional hybrid semantic search (VectorChord) |
| Web | Vite + React + TanStack Router/Query + Tailwind v4 + Radix/shadcn |
| Mobile | Expo / React Native + NativeWind |
| Design | Material 3–inspired, one fjord-blue accent, Albert Sans, light + dark themes; tokens in `docs/design-system/tokens.json` (§16) |
| Virus scanning | Not in v1. A pipeline hook allows optional ClamAV later. |
| WebDAV | Later (post v1) |
| License | AGPL-3.0, matching Immich and compatible with pg_search and VectorChord |
| Repo | One monorepo: .NET solution in `src/`, pnpm + Turborepo workspace in `apps/` and `packages/` (§3.2) |

---

## 3. Architecture

```
          ┌───────────────┐     ┌────────────────┐
          │ Web (React)   │     │ Mobile (Expo)  │
          └───────┬───────┘     └───────┬────────┘
                  └──────── HTTPS ──────┘
                             │
                    (reverse proxy: Caddy/Traefik/NPM)
                             │
      ┌──────────────────────▼───────────────────────┐
      │ server   (image: dokumentsystem, ROLE=server)│
      │  REST API · tus uploads · SignalR · web SPA  │
      │  trash and version purge                     │
      └───────┬───────────────────────────┬──────────┘
              │                           │
   ┌──────────▼─────────┐        ┌────────▼──────────────┐
   │ PostgreSQL         │◄───────┤ worker (ROLE=worker)  │
   │ + pg_search        │  jobs  │ metadata · thumbnails │
   │ + vchord/pgvector  │        │ text extraction · PDF │
   └────────────────────┘        │ renditions · transcode│
                                 │ backups               │
                                 └───┬──────────────┬────┘
            /data (NAS volumes) ◄────┘              │ HTTP (API key)
                                                    ▼
                                  ┌────────────────────────────────┐
                                  │ ml (optional, any host)        │
                                  │ OCR · embeddings · transcribe  │
                                  │ stateless, GPU optional        │
                                  └────────────────────────────────┘
               optional: gotenberg (LibreOffice → PDF)
```

### 3.1 Components

| Component | Responsibility | Needs storage? | Needs DB? |
|---|---|---|---|
| `server` | API, auth, uploads, downloads/streaming, live updates over SignalR (§15.4), serves the web SPA, the purge (§12) | yes (rw originals) | yes |
| `worker` | Runs background jobs; calls `ml` and `gotenberg` | yes (ro originals, rw derived) | yes |
| `ml` | Stateless inference over HTTP | **no** | **no** |
| `gotenberg` | Converts Office documents to PDF (optional) | no | no |
| `postgres` | Data, search indexes, vectors, job queue | own volume | n/a |

`server` and `worker` are the **same image** with a different `ROLE` environment variable. They share all domain code, so there is no API drift between them.
You can run several workers, though one is enough for a NAS.

### 3.2 Repository layout

```
Dokumentsystem.slnx         .NET solution (central package versions in Directory.Packages.props)
src/
  Dokument.Domain/          entities, value objects, domain rules (no I/O)
  Dokument.Application/     use cases, permissions, quota logic, job definitions
  Dokument.Infrastructure/  EF Core + migrations, storage, queue, SMTP, ML client, Gotenberg client
  Dokument.Server/          server role: API, tus, SignalR, auth, serves the web SPA
  Dokument.Worker/          worker role: job handlers (thumbnails, extraction, backup …)
  Dokument.Host/            single entrypoint (`dokument`); picks role(s) from the ROLE env var
  Dokument.AppHost/         .NET Aspire for local dev: Postgres, server, worker and Vite
tests/
  *.UnitTests, *.IntegrationTests (Testcontainers + our Postgres image)
apps/, packages/            pnpm + Turborepo workspace (see §15.1)
ml/                         ML service (Python, see §11), from M7
docker/postgres/            our Postgres image (§13.1)
Dockerfile                  the app image (both roles)
docker-compose.yml          NAS deployment (§4)
```

- `ROLE` is `server`, `worker` or `server,worker`. Unset means both, for development and very small installs.
- The server applies EF Core migrations on startup. The worker waits until they are applied.
- The OpenAPI document is written to `packages/api/openapi.json` on every build, and `packages/api` generates the TypeScript client from it. CI fails if either is stale.

---

## 4. Deployment (NAS)

### 4.1 Compose sketch

The full file is [`docker-compose.yml`](../docker-compose.yml), configured through `.env` ([`.env.example`](../.env.example)). In short:

```yaml
services:
  server:
    image: ghcr.io/elmaxe/dokumentsystem:${DOKUMENT_VERSION}
    user: "${PUID}:${PGID}"
    environment: { ROLE: server, DB_HOST: postgres, DB_PASSWORD: ..., PUBLIC_URL: ... }
    volumes:
      - ${ORIGINALS_LOCATION}:/data/originals
      - ${DERIVED_LOCATION}:/data/derived
      - ${BACKUP_LOCATION}:/data/backups
    ports: ["${DOKUMENT_PORT}:8080"]
    depends_on: { postgres: { condition: service_healthy } }
  worker:
    image: ghcr.io/elmaxe/dokumentsystem:${DOKUMENT_VERSION}
    user: "${PUID}:${PGID}"
    environment: { ROLE: worker, ... }
    read_only: true
    volumes:
      - ${ORIGINALS_LOCATION}:/data/originals:ro
      - ${DERIVED_LOCATION}:/data/derived
      - ${BACKUP_LOCATION}:/data/backups
      - ${EXPORT_LOCATION}:/data/exports   # optional: where export jobs write (§7.1)
  postgres:
    image: ghcr.io/elmaxe/dokumentsystem-postgres:${DOKUMENT_VERSION}
    volumes: [ "${DB_DATA_LOCATION}:/var/lib/postgresql/data" ]
  ml:         # profile "ml"; can instead run on another machine
    image: ghcr.io/elmaxe/dokumentsystem-ml:${DOKUMENT_VERSION}   # -cuda / -openvino variants
  gotenberg:  # profile "office"; enables Office previews
    image: gotenberg/gotenberg:8
  clamav:     # profile "clamav"; not used in v1 (§21)
    image: clamav/clamav:stable
```

Images are published to GHCR for amd64 and arm64: `edge` from the default branch, and version tags from releases.

### 4.2 NAS requirements

- **Multi-arch images** (amd64 + arm64). Many Synology and QNAP boxes are ARM.
- **`PUID`/`PGID`**, so files on the mounted volume get the owner the user expects. Compose runs the app containers as `PUID:PGID` (`user:`), so they are never root and need no entrypoint script.
- **Low-resource profile:**
  - One worker with low concurrency.
  - Gotenberg and ML optional, so the base install fits in about 1 GB RAM.
  - Office previews and OCR are disabled until their services are configured.
- **The DB volume must be on local disk**, not a network share. Postgres on SMB/NFS is a corruption risk.
- Healthchecks on every service. `/health/live` and `/health/ready` endpoints. The image's `HEALTHCHECK` runs `dokument healthcheck`, since the runtime image has no curl. Readiness covers the database and the data directories.

### 4.3 Configuration

- **Environment variables, for bootstrap only:** DB connection (`DB_HOST`, `DB_PORT`, `DB_USERNAME`, `DB_PASSWORD`, `DB_DATABASE`, or `ConnectionStrings__Dokument`), OIDC issuer/client, paths, `PUBLIC_URL`, `ROLE`, the standard `OTEL_*` variables and `METRICS_PORT` (§22.1).
- **Everything else is admin settings stored in the DB** and edited on the admin settings page (`/admin`, admins only). Each is one row of `settings` (key → JSON); a missing row means the default, so a fresh install needs no seeding. Each section of the page saves on its own, and every change writes a `settings.change` audit event naming the key (never the value). Built so far (decisions 17, 18, 24, 32 and 33): the default quota, trash and version retention, text recognition on or off, video copies (when they're made and their size), the Gotenberg URL, the ML URLs and the ClamAV address. Sign-in is shown on the page read-only, because OIDC stays in environment variables (§5). The rest of the list below follows as its features land:
  - quota defaults
  - trash and version retention
  - SMTP
  - backup schedule
  - ML URLs
  - Gotenberg URL
  - job concurrency (until the Jobs page can edit it: `Worker__ThumbnailConcurrency`, default 2; `Worker__MetadataConcurrency`, default 2; `Worker__TextConcurrency`, default 1; `Worker__TranscodeConcurrency`, default 1)
  - OCR languages (until then `Worker__OcrLanguages`, default `swe+eng`)

---

## 5. Authentication and sessions

- **Login:** OIDC authorization code flow with PKCE against the user's IdP (Authentik, Authelia, Keycloak, Pocket ID, …).
  - Web: the server performs the flow as a confidential client (ASP.NET's OpenIdConnect handler). The browser navigates to `GET /api/v1/auth/login?returnTo=…`, and the IdP redirects back to `/api/v1/auth/callback`. Only local paths are accepted as `returnTo`.
  - The IdP's response comes back as a query string, so the short-lived correlation cookies can be SameSite=Lax. That also lets login work over plain http on a LAN.
  - Clients that run the sign-in themselves (the API reference now, the mobile app in M5) use the **token endpoint**, `POST /api/v1/auth/token`:
    1. The client opens the IdP's authorize URL with our client id and PKCE: Scalar in a popup, the app in the system browser.
    2. The IdP redirects back to the client with a code. Scalar gets it at its page. The app's redirect URI is an https page on our server, `${PUBLIC_URL}/api/v1/auth/app-callback`, which answers `302` to `dokumentsystem://signed-in` with the IdP's query unchanged, so the IdP only needs ordinary redirect URIs (decision 37).
    3. The client sends an OAuth token request (`grant_type=authorization_code`, `code`, `code_verifier`, `redirect_uri`). The app adds `device_name` (the phone's model), which names the session under active devices; other clients are named from their User-Agent.
    4. The server redeems the code as the confidential client, so the secret never leaves it. It validates the ID token, reads userinfo and provisions the person as at web login.
    5. It answers with one of our own **bearer sessions** as `access_token`. The IdP's tokens are dropped, and no refresh token is needed because sessions slide.
  - Errors use OAuth's shape (`invalid_grant`, `access_denied`, …). Requests share login's rate limit.
  - Codes are redeemed only for allow-listed redirect URIs, which must also be registered at the IdP: the app's `${PUBLIC_URL}/api/v1/auth/app-callback` always, and the API reference's page (`${PUBLIC_URL}/scalar/`) in development.
  - `GET /api/v1/auth/config` tells such clients the issuer, client id, scope and the app's redirect URI. All are null while sign-in is off.
- **Configuration** is bootstrap env (§4.3). Sign-in stays off until `OIDC_ISSUER` and `OIDC_CLIENT_ID` are set, so a fresh install still starts.

  | Variable | Default | Meaning |
  |---|---|---|
  | `OIDC_ISSUER` | — | Issuer URL, e.g. `https://auth.example.com/application/o/dokument/` |
  | `OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET` | — | The confidential client registered at the IdP. Its redirect URIs are `${PUBLIC_URL}/api/v1/auth/callback` and, for the mobile app, `${PUBLIC_URL}/api/v1/auth/app-callback`. Its post-logout redirect URIs are `${PUBLIC_URL}/api/v1/auth/signed-out` and `${PUBLIC_URL}/api/v1/auth/app-signed-out`. |
  | `OIDC_SCOPE` | `openid profile email` | Add `groups` for IdPs that need it (e.g. Authelia) |
  | `OIDC_GROUPS_CLAIM` | `groups` | Read from the ID token and the userinfo endpoint |
  | `OIDC_USER_GROUP` | unset (anyone at the IdP may log in) | Allow-list group |
  | `OIDC_ADMIN_GROUP` | `dokument-admins` | Members get the admin role |
  | `OIDC_PROVIDER_NAME` | unset | Shown on the button: "Sign in with …" |
  | `OIDC_QUOTA_CLAIM` | unset (quotas are set on the admin page) | A claim that sets each person's storage at login (§17.1) |

- **`PUBLIC_URL`** sets the scheme, host and path base that the app sees itself at, behind the reverse proxy. It drives the redirect URI and the cookie's Secure flag.
- **Sessions:** after a successful OIDC login, the server issues **its own session**. The IdP's tokens are discarded, except the ID token, which the session keeps only as the hint for signing out at the IdP. It is never accepted as a credential.
  - The token is 256 random bits. Only its SHA-256 is stored in `sessions`, so a leaked database doesn't hand out live sessions.
  - **Lifetime:** a session expires after **30 days without use**. Each use slides the expiry forward, with at most one write every 5 minutes.
  - **Web:** an httpOnly, SameSite=Lax cookie (`dokument_session`). It is Secure whenever `PUBLIC_URL` is https.
    - CSRF: mutations made with the cookie must send `X-CSRF-Token`. Its value is a hash of the session token, returned by `GET /api/v1/auth/session`.
  - **Mobile:** the same opaque token, sent as `Authorization: Bearer`. It is stored in the Keychain or Android Keystore (expo-secure-store) and needs no CSRF token. Only the live updates hub also reads it from the `access_token` query, because WebSockets can't send headers (§15.4).
  - **Session proof:** `POST /api/v1/auth/proof` with `{sessionId, nonce}` answers `{proof}`, the base64url HMAC-SHA256 of the nonce's text keyed by the stored SHA-256 of the session token. The app computes the same from its token, so a local address can prove it's this server without the token crossing the network (§15.3). The nonce is 16–64 random bytes as base64url; unknown, revoked and cookie sessions get 404. It shares login's rate limit.
  - **Revoking:** users list and revoke their own sessions ("active devices": `GET/DELETE /api/v1/sessions`). Admins do the same for anyone (`/api/v1/admin/users/{id}/sessions`, `/api/v1/admin/sessions/{id}`). A revoked session stops working on its next request.
  - **Sign out** ends the current session, and the person's session at the IdP too (OIDC RP-initiated logout, decision 44), so signing in again asks for the password instead of going straight back in.
    - `POST /api/v1/auth/logout` revokes the session and answers `{endSessionUrl}`: the IdP's `end_session_endpoint` with `id_token_hint` (the session's ID token), `client_id` and `post_logout_redirect_uri`. With the hint, the IdP signs out without asking.
    - **Web:** the page sends the browser to `endSessionUrl`. The IdP returns to `/api/v1/auth/signed-out`, which goes on to `/signin?reason=signedOut` ("You're signed out."). The tab that signed out ignores its own live "signed out" notice, so it doesn't stop at the sign-in page on the way.
    - **App:** it signs out locally first, then opens `endSessionUrl` in the same auth browser it signs in with (the IdP's cookie lives there). The IdP returns to `/api/v1/auth/app-signed-out`, which hands back to `dokumentsystem://signed-out`. If the server can't be reached or the browser is closed early, the app is still signed out.
    - **Fallback:** when the IdP's discovery has no `end_session_endpoint`, or it can't be read, `endSessionUrl` is null and signing out stays local.
    - Ending a session under Active devices, or by an admin, stays local. Signing out of other apps at the IdP doesn't reach us yet (back-channel logout is later).
- **API keys:** personal, scoped (read-only / read-write), for scripts, rclone and WebDAV later.
- **User provisioning:** users are created on first login (JIT), with a stable identity from `iss` + `sub`.
  - Email, display name and role are refreshed on every login. Disabled users are refused, and their sessions stop working.
  - An optional **allow-list**: only users in the IdP group named by `OIDC_USER_GROUP` (e.g. `dokument-users`) may log in. Admins are always allowed.
  - The `dokument-admins` group → admin role. The mapping is re-evaluated on each login.
  - An optional claim sets the person's quota (§17.1).
- **Rate limiting:** 20 login starts per minute per IP.
- **Break-glass:** a CLI command in the container (`dokument admin reset-oidc` / `create-admin-token`) for when the IdP is broken.
- **Local development:** Aspire runs Keycloak with a `dokument` realm. Its test users are alice (admin), bob (user) and carol (no groups); each password is the username.
  - Keycloak and Vite listen on all addresses, so a phone on the same network can use the dev server. The AppHost finds this machine's network address (`--LanHost` overrides it) and lists `http://<address>:5173` on the web resource; that is the server address to type in the app.
  - Everything signs in at Keycloak on that address, because Keycloak names its issuer after the address it's asked at. Aspire sets no `PUBLIC_URL`: Vite's proxy keeps the Host header, so redirect URIs follow the address each device used. The browser stays on `http://localhost:5173`, since plain http elsewhere isn't a secure context.
  - The realm's `dokument` client allows the web callback, Scalar's page and the app's callback at `http://localhost:5173`, and the app's callback at the network address. Its post-logout redirect URIs are the two sign-out pages at `http://localhost:5173`, and the app's at the network address.
  - The API reference (Scalar, `http://localhost:5173/scalar/`, proxied by Vite) has an Authorize button. It signs in at Keycloak through the token endpoint, and its requests then carry a bearer session, which is listed under active devices.
- **Built in M0:** login, sessions, CSRF and active devices. **Still to come:**
  - API keys and the break-glass CLI
  - auth audit events, with the audit table in M1
  - the quota claim (§17.1), right after the admin settings page
- **Built in M5:** the app's sign-in through the token endpoint and the callback page, device names, the session proof and the bearer token on the live hub.

---

## 6. Data model (sketch)

```
users(id, oidc_issuer, oidc_subject, email, display_name, role {user|admin},
      quota_bytes NULL→default, quota_unlimited,   -- own limit, the default, or no limit (§17)
      quota_source {admin|claim},                   -- who set it (§17.1)
      used_bytes, reserved_bytes, locale NULL→browser {sv|en}, created_at, last_login_at, disabled_at,
      UNIQUE (oidc_issuer, oidc_subject))

nodes(id, owner_id, parent_id NULL, kind {folder|file}, name, name_key,
      path ltree,                 -- ids (hex, no dashes) from the top down, for subtree queries
      current_version_id NULL,    -- files only
      created_at, modified_at, trashed_at NULL, trashed_root_id NULL,
      trashed_by NULL,            -- who moved it to the trash, when it was someone it's shared with (§9)
      UNIQUE NULLS NOT DISTINCT (owner_id, parent_id, name_key) WHERE trashed_at IS NULL)

file_versions(id, node_id, version_no, blob_hash, size, mime_type, original_name,
              uploaded_by, uploaded_at, client_modified_at, restored_from NULL, keep_forever,
              comment NULL)

blobs(owner_id, hash = sha256, size, storage_key, ref_count, created_at,
      PRIMARY KEY (owner_id, hash))   -- one copy per owner (§7)

uploads(id, user_id, owner_id, parent_id NULL, target_node_id NULL, name, size, upload_offset,
        claimed_hash NULL, mime_type, client_modified_at, on_conflict {fail|keepBoth|newVersion},
        created_at, expires_at, result_node_id NULL, completed_at NULL)   -- tus uploads in flight

blob_previews(blob_hash PK, status {ready|unsupported|failed}, pipeline_version, width NULL, height NULL,
              page_count NULL, updated_at)       -- what the thumbnail stage made of a blob
renditions(blob_hash, kind {thumb_s|thumb_m|preview|playable|document|seek},
           storage_key, mime_type, width, height, created_at, PRIMARY KEY (blob_hash, kind))

blob_metadata(blob_hash PK, pipeline_version, taken_at NULL, taken_offset_minutes NULL, camera NULL,
              duration_seconds NULL, latitude NULL, longitude NULL,
              tags jsonb, readable, updated_at)  -- exiftool's searchable tags and the filter values (§10.3)
blob_contents(blob_hash PK, status {ready|empty|unsupported|failed}, pipeline_version, page_count NULL,
              ocr_skipped, ocr_engine NULL, updated_at)   -- what the text stage made of a blob
content_pages(id, blob_hash, version_id NULL, page_no, source {text|ocr|metadata|added|transcript}, text,
              words jsonb NULL,                  -- OCR word boxes, [[x, y, w, h, "word"], …] as page fractions;
                                                 -- for transcripts, word times [[start_ms, end_ms, "word"], …]
              start_ms NULL, end_ms NULL,        -- transcript pages only: the stretch of sound the page covers
              edited_by NULL, edited_at NULL,
              UNIQUE (blob_hash, page_no) WHERE version_id IS NULL,      -- what was read, per blob
              UNIQUE (version_id, page_no) WHERE version_id IS NOT NULL) -- people's corrections, per version (§13.5)
                                                 -- BM25-indexed; page 0 is the metadata text
version_details(version_id PK → file_versions, title NULL, description NULL, taken_at NULL,
                taken_precision {year|month|day|time} NULL, edited_by, edited_at)   -- edits over what was read (§13.5)
embeddings(blob_hash, page_no NULL, model, vector vchord) -- optional (M7)

blob_tags(blob_hash, tag, tag_key, source {rule|ml}, rule_id NULL, created_at,
          PRIMARY KEY (blob_hash, tag_key))      -- automatic tags belong to the bytes, like text (§13.4)
node_tags(node_id, tag, tag_key, added_by, added_at, PRIMARY KEY (node_id, tag_key))   -- tags people add
node_tag_removals(node_id, tag, tag_key, removed_by, removed_at,
                  PRIMARY KEY (node_id, tag_key))  -- an automatic tag someone took off this file
tag_rules(id, tag, tag_key UNIQUE, words, enabled, created_by, created_at, updated_at)  -- "kvitto, receipt" → receipt
effective_node_tags  -- a view: node_tags, plus the current version's blob_tags minus removals (what a file shows),
                     -- with since: when the file got each tag (filing rules, §13.6)

filing_rules(id, owner_id, tag, tag_key, folder_template, position, enabled, active_since, created_at, updated_at,
             UNIQUE (owner_id, tag_key))      -- each person's own: "receipt" → Kvitton/{year} (§13.6)
node_filings(node_id PK, rule_id NULL, tag, from_folder_id NULL, to_folder_id NULL, filed_at, undone_at NULL,
             skipped)                         -- a file is filed once; moving it back, or Undo, sticks.
                                              -- skipped: left in place because the move would hide it (§13.6)

stars(user_id, node_id, starred_at, PRIMARY KEY (user_id, node_id))   -- each person's own (§15.2)
recent_files(user_id, node_id, at, action {opened|downloaded|uploaded|restored},
             PRIMARY KEY (user_id, node_id))     -- one row per person and file, overwritten; private, not audit

shares(id, node_id, grantee_user_id, role {viewer|editor}, created_by, created_at, seen_at NULL,
       UNIQUE (node_id, grantee_user_id))   -- seen_at: opened since it was shared, so no longer New (§9)
shared_role(user, path) → 0|1|2              -- SQL function: the highest share on the path's nodes (§9)
links(id, node_id, token_hash, role {viewer}, allow_download, password_hash NULL,
      expires_at NULL, created_by, created_at, revoked_at NULL)

jobs(id, type, subject, status {queued|running|done|failed}, priority, attempts, run_after,
     locked_by, locked_until, last_error, created_at, finished_at)   -- subject: e.g. a blob hash
audit_events(id, at, actor_user_id NULL, actor_kind {user|link|system|api_key}, ip, user_agent,
             action, target_node_id NULL, target_version_id NULL, details jsonb)  -- partitioned monthly
notifications(id, user_id, type, data jsonb, created_at, read_at)
notification_prefs(user_id, type, email bool, in_app bool)
settings(key, value jsonb, updated_by, updated_at)
sessions(id, user_id, kind {cookie|bearer}, token_hash UNIQUE, device_name, created_at, last_seen_at,
         expires_at, revoked_at)
api_keys(id, user_id, name, token_hash, scopes, created_at, last_used_at, revoked_at)
```

Notes:
- **`nodes` vs `file_versions` vs `blobs`.** A node is the logical file in the tree. A version is one upload of it. A blob is the bytes on disk.
  - Uploading identical bytes **reuses the owner's existing blob** (dedup by SHA-256, per owner). The same bytes from two people are two blobs.
  - Versions, previews and renditions refer to blobs by hash, so they have no foreign key to `blobs`; a version's blob is `(node.owner_id, blob_hash)`.
  - Re-uploading identical content to the same file does **not** create a new version.
- **Name uniqueness within a folder:** NFC-normalized and case-insensitive (`name_key`). The top of each user's "My files" (parent NULL) counts as one folder, hence `NULLS NOT DISTINCT`. Conflicts are resolved like Drive: either "file (1).pdf" or "upload as new version", chosen by the user.
- **Names** (`NodeName` in the domain) are trimmed, NFC, at most 255 UTF-8 bytes, never "." or "..", and contain no slashes or control characters, so `dokument export` can write any of them to disk.
- **Renditions belong to the bytes, not versions or owners** (decisions 15 and 23): identical bytes are rendered once, even for two people, and a copy or a restored version has its thumbnail at once. **Extracted text, metadata and automatic tags do too** (decision 24), so a copy is searchable at once.
- **People's corrections belong to the version** (decision 28): what was read is shared by everyone with the same bytes, so edits of the title, description, date taken and text are rows of `version_details` and `content_pages` with a `version_id`, and go with their version when it is purged.
- **`path ltree`** makes subtree operations cheap: permission inheritance, "trash folder", "folder size", "search in folder".

---

## 7. Storage layout

```
/data/originals/            # PRECIOUS — back this up
  keys/                     # ASP.NET Data Protection keys that encrypt secrets in settings (§21)
  blobs/{owner_id}/ab/cd/abcdef…sha256   # per person, content-addressed, write-once, never modified
  tmp/{upload_id}           # in-flight uploads (tus), same filesystem for atomic rename
/data/derived/              # REGENERABLE — backup optional
  thumbs/ab/{sha256}/{kind}.webp   # thumb_s, thumb_m, preview; keyed by blob like the originals
  previews/ab/{sha256}/playable.mp4  # a copy browsers play, of video and audio they can't (§14)
  previews/ab/{sha256}/document.pdf  # a PDF of an Office or OpenDocument file, made by Gotenberg (§10.3)
  previews/ab/{sha256}/waveform.bin  # an audio file's loudness over time, for the player's timeline (§14)
  previews/ab/{sha256}/seek.webp     # a video's seek previews: frames for the timeline, as one sheet (§14)
  previews/ab/{sha256}/pages-{n}.webp  # a PDF's pages as small images for choosing pages, 100 to a sheet (§8.4)
  tmp/pdf/{operation_id}/{n}.pdf     # a merge or split's new PDFs, until the server saves them (§8.4)
/data/backups/              # DB dumps and name manifests (§7.1, §19)
/data/exports/              # optional: readable copies written by export jobs (§7.1)
```

- **Write path:**
  1. Append each tus chunk to `tmp/{upload_id}` at the recorded offset.
  2. When the last byte arrives, hash the temp file and compare it with the SHA-256 the client sent. A mismatch discards the upload. (Hashing as bytes arrive doesn't survive a resume, since a SHA-256 state can't be saved between requests, so the file is read once more instead.)
  3. `fsync`, then `rename()` to `blobs/{owner_id}/…`, where the owner is the file's owner (the folder owner for uploads into a shared folder, §9). If the owner already has the blob, drop the temp file.
  4. Commit the DB transaction: the blob row (`ON CONFLICT (owner_id, hash) DO NOTHING`), the node or version, `ref_count + 1`, the quota and the audit event.
- **Blob deletion** happens only in the purge job (§12): when `ref_count` drops to 0, the blob is deleted **after** the DB commit (outbox pattern).
- **Integrity job:** a scheduled sweep finds orphaned blobs, missing blobs and checksum mismatches (bit rot), and reports to admins.
- **Each person's files are in their own folder** (decision 23), named by their user id. Only the bytes they own are there, so the folder's size is their used storage (§17), and one person can be backed up, moved or removed on their own. Access is still decided in the database: every node has an `owner_id`, every request goes through `NodeAccess`, and downloads go by node id, never by hash.
  - The cost: the same bytes from two people are stored twice. Copying a file someone shared with you, and later transferring ownership, copy the bytes into the new owner's folder (to a temp file, then renamed) instead of being instant.
- **The pool stays unreadable on purpose.** Blobs have no names or extensions; names, types and the tree are in the DB. That is what makes renames and moves a single DB update, keeps writes atomic, and makes versions, trash and dedup plain rows. Readable files come from exports instead (§7.1).

### 7.1 Export and manifests

Decided 2026-10-24 (decision 22) over a live mirror of hardlinks, real paths on disk (as in Nextcloud) and sidecar files next to each blob. This is the "if the app dies, my files are still mine" guarantee.

- **Name manifest:** right after each scheduled DB dump (§19), the worker writes `manifest-{timestamp}.jsonl.gz` beside it in `/data/backups`, with the same retention as the dumps. It has one line per version that isn't purged: owner (id, email, display name), path of folder names from the top of My files, name, version number, whether it's current, trashed or kept forever, blob hash, size, MIME type and modified time. Written to a temp file, then renamed, so a half-written manifest never replaces a good one.
  - With the blobs and a manifest, the tree can be rebuilt with no Postgres at all. `dokument export --manifest <file>` does that, and the format is documented so a short script can too.
- **Export job:** writes a person's files (or everyone's, one folder per person) as an ordinary folder tree with original names and modified times.
  - Runs on the job queue in the worker, which only reads originals, into the optional `/data/exports` volume. Admins start it from the admin Storage page, and the CLI `dokument export --user X --to /path` runs the same code.
  - Writes current versions by default; "include old versions" adds them as `name (v3).ext` in a `.versions/` folder beside each file. Trash is left out.
  - Exports are **real copies** and take real space. Running one again into the same folder is incremental: files whose size and modified time match are skipped, and files gone from the app are removed from the export.
  - Names that Windows or SMB can't hold (`: ? * " < > |`, trailing dots and spaces) are escaped, and names that clash only in case get " (2)" in the export. The app's names are never changed.
  - Progress is on the job; an export that fails says which file and why. Each run writes an `export.start` and `export.finish` audit event, but not one per file.
  - Can be put on a schedule like the DB dump, **off by default**.

---

## 8. Uploads and versioning

### 8.1 Upload flow

1. **Client → `POST /api/v1/uploads` (tus create)** with `Upload-Length` and `Upload-Metadata`: `filename`, and optionally `folderId`, `nodeId` (upload a new version of that file), `sha256`, `filetype`, `modifiedAt` (ms since the epoch) and `onConflict` (`fail`, `keepBoth`, `newVersion`).
   - The server checks permission and **reserves quota** (`reserved_bytes += size`) in one conditional update. If it doesn't fit, it answers 413 immediately. With `onConflict=fail` a taken name is refused (409) before any bytes are sent.
2. Client → `PATCH` chunks (`application/offset+octet-stream`, `Upload-Offset`). The web client sends 32 MiB chunks, so it stays under common reverse-proxy body limits. The upload can resume after network loss or app restart: `HEAD` reports the offset, and the bytes that arrived before a dropped connection are kept.
3. **On completion**, before the last `PATCH` answers:
   - Hash, store the blob, and create the node or version (§7). The response carries the file's id in `Dokument-Node-Id`.
   - Release the reservation and add the size to `used_bytes`, unless the owner already had that blob.
   - Enqueue the processing jobs (M2).
   - Write an audit event and a live update notice (§15.4).
4. Abandoned uploads expire after 24 h without progress, which releases the reservation and deletes the temp data. The server sweeps them hourly, since it owns `tmp/`.

Implementation notes (built in M1):
- The server implements tus 1.0 core with the `creation`, `creation-with-upload`, `termination` and `expiration` extensions itself (`TusEndpoints`), rather than through tusdotnet, so the `uploads` row and the quota reservation share a transaction. Any tus client works; the web uses `tus-js-client`.
- One request writes to an upload at a time (423 otherwise). Uploads are owned by the server process, so an in-memory lock is enough.
- `GET /api/v1/uploads/{id}` returns the upload's state as JSON and, once complete, the file it produced and any duplicates of it (the fallback in §8.3).

Also:
- The client sends `client_modified_at` (the file's mtime) for display. The original bytes stay untouched.
- **Folder upload** (decision 24) creates the folder tree first, then uploads the files with limited parallelism:
  - **Starting one:** "Upload folder" in the New menu and the phone's New sheet (`<input webkitdirectory>`, left out where the browser can't pick folders), or dropping folders onto the file area. A drop reads the folders through the entries API, so empty folders come along; files dropped beside them upload as usual.
  - **Skipped without asking:** `.DS_Store`, `Thumbs.db`, `desktop.ini` and AppleDouble `._*` files. A file or folder whose name `NodeName` refuses fails in the tray with the reason, and isn't retried.
  - **`POST /api/v1/folders/tree`** makes every folder in one transaction: `{folderId, paths: ["Kvitton", "Kvitton/2025", …], onConflict, totalBytes}`. It answers each path's folder id and name and, for a folder that was already there, the names it holds, so the client can ask about name clashes before sending anything.
  - **The top folder's name is taken:** `onConflict=fail` answers 409 and the client asks: **Merge** (the default button), **Keep both** ("Kvitton (1)") or Cancel. Merge reuses existing folders below it with the same names. A *file* with a folder's name always gets a numbered folder beside it.
  - **Quota:** `totalBytes` is checked against the owner's quota (used plus reserved) before anything is made; if it doesn't fit, the request answers 413, nothing is created and the dialog says how big the folder is and how much is left. Duplicates aren't subtracted, since the hashes aren't known yet. Each file still reserves its own quota when its upload starts.
  - **Checks:** files are hashed and checked in batches (200 files or 512 MiB); new files with free names start uploading at once. Duplicates (§8.3) and name clashes inside merged folders are each asked about once, for the whole folder, after the last file is hashed. Skipped duplicates leave their folders in place.
  - **Upload tray:** one row per folder with its files and bytes added up and the time left, Pause/Resume for the whole folder, and Open when it's done. Expanding it lists the files that failed (with **Retry failed**) and those still on their way, with their paths inside the folder.
  - **Not yet:** picking a folder upload back up after the page reloads (single files still resume through tus), and folder uploads in the mobile app (M5).

### 8.2 Versioning

- Uploading to an existing file (explicit "upload new version", or a name collision where the user chooses "replace") → `version_no + 1`.
- **Restoring an old version** creates a *new* version pointing at the old blob. History is never rewritten.
- **Retention** (admin setting; defaults follow Google Drive): a non-current version is purged when it is **older than 30 days** or when the file has **more than 100 versions**, whichever comes first.
  - The current version is always kept.
  - A version's age counts from when the next version replaced it. The versions API reports each version's purge date, so the UI can say "Removed in N days".
  - Users can mark a version **"Keep forever"**, which exempts it from purging. It still counts toward quota.
  - The purge (§12) removes versions past these limits every hour.
- **Quota counts all versions**, deduplicated per blob per owner.
- Renditions and extracted text exist **per version**. Search indexes **only the current version**.

### 8.3 Duplicate detection

On disk, identical bytes are always stored once per person (§6, §7). On top of that, **users are told** when they upload a file they already have.

- **Before transfer:** the client computes SHA-256 locally, streamed so large files don't fill memory (`hash-wasm` in a Web Worker on web; a native streaming hash on mobile).
  - It then calls `POST /api/v1/uploads/check` with `[{name, size, sha256}]` for the whole batch.
- **The server answers per file:** `new`, or `duplicate` with the matching locations ("Kvitton/2025/ikea.pdf", and "in trash" if that's where it is).
- **UX:**
  - **Single file:** "You already have this file in *Kvitton/2025*." Choices: **Skip** / **Upload anyway** / **Open existing**.
  - **Batch:** one summary, "12 of 40 files are already in Dokumentsystem." Choices: **Skip duplicates** (default) / **Upload all** / **Review**.
  - Selecting "Upload anyway" creates a second node that points at the same blob (`POST /api/v1/uploads/copy`). This is instant: no bytes are transferred and no extra quota is used.
  - "Open existing" opens the folder that holds the match until the viewer arrives in M2.
- **Fallback:** if the client couldn't hash in advance (older client, huge file on a weak phone), the server detects the duplicate on completion. The upload tray then shows "Duplicate of …" with a **Remove this copy** action.
- **Privacy rule:** matches are reported **only against nodes the user can already access**, whether owned or shared with them.
  - A user must never learn that *someone else* has a file.
  - The "instant, no transfer" shortcut is allowed only in that same case. Otherwise the client has to upload the real bytes, which prevents the classic "I know the hash, give me the file" dedup attack. The server still dedups on disk, per owner, after verifying the hash.
- **Folder uploads** are checked file by file, and the summary covers the whole folder.
- An optional **"Duplicates" view** under Storage lists groups of identical files across the user's library, for cleanup.

### 8.4 Merging and splitting PDFs

Decided 2026-11-04 (decision 43). People merge several PDFs into one, or split one into several, on the web and in the app. It replaces the roadmap's "split and merge through Gotenberg".

- **Inputs are PDFs only**, ones the worker could read (`NodeDto.pdf.readable`, with the page count). Office files and photos may come later, through their PDF rendition.
- **The results are new files, never new versions.** They go next to the originals, which stay as they are, unless the person turns on **Move the originals to the trash** (off by default), which trashes them once the new files are saved. A merge goes into the first file's folder; a split goes next to the PDF, or into a **new folder** named by the person.
  - Each source's current version is pinned by its blob when the operation starts, so a new version uploaded meanwhile doesn't change what's made.
  - Names that are taken get a number, as uploads do (Keep both). Defaults: "{name} (merged).pdf" for a merge and "{name}, pages 1–3.pdf" for each part of a split.
- **The worker makes them with PDFium** (`FPDF_ImportPagesByIndex` into a new document, `FPDF_SaveAsCopy`), which it already bundles for thumbnails and text. No Gotenberg, and no PDF library in the clients. Pages are copied as they are, so text stays text and nothing is re-compressed. Bookmarks, links between pages and form fields aren't kept, and the dialogs say so.
- **Two steps, because the worker can't write originals (§10.4):**
  1. `POST /pdf/merge` or `POST /pdf/split` checks the request, reserves quota for the sources' total size (§17), stores a `pdf_operations` row and queues a `pdf` job; it answers `202` at once.
  2. The worker's `pdf` job (concurrency 1) writes the new PDFs to `derived/tmp/pdf/{operation_id}/`, reporting pages done as it goes, then queues `pdf-save`.
  3. The server claims `pdf-save` jobs itself, hashes each new file again (the worker reads untrusted files, so its hash isn't trusted), imports the bytes into the owner's blobs, settles the quota with the real size and adds the files. A retry skips files already saved.
- **What the new files keep:**
  - **Text, recognized words and the person's text corrections** are copied page by page from the sources' content pages, so nothing is read or recognized again; the text stage isn't queued for the new blob.
  - **Tags people added** to any source are added to the result. Automatic tags come from tag rules running on the new text, as for an upload.
  - **Details** (title, description, date taken) come from the first source.
  - **Filing rules don't move the results**: the person chose where they go.
  - **"Made from"** (`file_origins`, per version) lists each run of pages with the source's name and pages, and the Info tab shows it, linking to sources that still exist (`GET /nodes/{id}/origin`).
- **Duplicates:** if a new file's bytes are already in the person's files, it's still made (dedup on disk keeps it free) and the tray says so, with **Remove this copy** (§8.3).
- **Limits:** 2 to 100 files and at most 2,000 pages per merge; 1 to 500 parts per split. A split's parts are page ranges ("1-3, 5"), may leave pages out, and may use a page more than once. Password-protected or damaged PDFs fail with a sentence that names the file, without retries.
- **Following it:** `GET /pdf/operations` (the person's last week, newest first) and `GET /pdf/operations/{id}` give the state (waiting, working, saving, done, failed, cancelled), pages done and the new files. The web tray polls every second while one is going and shows a row per operation beside the uploads, with Cancel until saving starts, then Open (a merge) or Show in folder (a split). `DELETE /pdf/operations/{id}` cancels. Operations are deleted after 7 days.
- **Choosing pages:** the split screen shows every page as a small image. `POST /nodes/{id}/page-sheets` says where each page is and asks the worker to make them if they aren't there (`page-sheets` stage, on demand only, never in the backfill): 200 × 283 px cells, 10 to a row, 100 to a WebP sheet, served unaudited by `GET /nodes/{id}/page-sheets/{n}`.
- **Web:** **Merge into one PDF** in the action bar when two or more readable PDFs are selected, opening a dialog to put them in order (drag, or each row's menu), name the file and choose the trash option. **Split PDF** in a PDF's menu, the More menu and the viewer opens a full-screen page: pages in a grid, a cut between any two pages (the gap on wider screens, a page's menu on phones), presets Every page and Every 2 pages, pages left out, and a **Ranges** mode for typing each part's pages. Each part is named on the side (below on phones).
- **App:** a long-press selection mode in file lists, then the same merge list and split screen (second pull request).
- **Audit:** `file.pdf_merge` and `file.pdf_split` on each new file, naming the sources (§18).

---

## 9. Permissions and sharing

Sharing with people is built (decision 39). Public links and notifications follow in their own PRs.

- **Roles:** `owner` (implicit, one per node), `editor`, `viewer`.
  - **Viewer:** see, preview, search, download, and star for themselves.
  - **Editor:** viewer, plus upload, new version, restore a version, new folder, rename, move within the owner's files, tags, details and text edits, redo a transcript, and move to the trash.
  - **Owner:** editor, plus share, Keep forever, the trash itself (restore, delete forever), and later transfer ownership.
- **Only the owner shares.** Editors can't add people or change roles; they see who has access, and the dialog says "Only Alice can change who has access."
- **Inheritance:** a share on a folder applies to its whole subtree.
  - A share lower down can only **add** rights; it can never remove inherited ones. The dialog won't offer a role below what a folder above gives, and the server refuses one ("Bob is already an editor through a folder above this one").
- **Effective permission:** ownership, or the highest share on the node and its ancestors. One SQL function, `shared_role(user, path)`, joins `shares` to the nodes whose `path` contains the node's; `NodeAccess.With(actor, View|Edit|Own)` uses it for browsing, the viewer, downloads, search, tags, details and every change. Refusals are 403 `forbidden`.
- **Quota attribution:** files and folders added to someone else's folder are **owned by, and count against, the folder owner**.
  - The uploader is recorded on the version. A full quota names whose storage it is.
  - This prevents "your files are stranded in my account" problems.
- **Trash:** an editor's "Move to trash" moves it to the **owner's** trash, which records who did it (`trashed_by`, shown as "Moved to the trash by Bob"). The editor's Undo still works; otherwise only the owner restores or deletes it.
- **Moves** stay within one owner's files. A move that would hide something from people who see it now (it leaves a shared folder for one they aren't in) asks first: "Bob can see this now, through the folder it's in. After the move they can't." (`POST /nodes/move-check`).
- **Merging and splitting PDFs** (§8.4) reads the sources with view access but adds the new files where the person can edit, so the menus offer it only there; trashing the sources afterwards takes edit access too. The new files belong to the folder's owner.
- **"Shared with me":** a page listing the top-most things shared with the person, newest first, with Shared by, Shared and Your access. Folders inside a listed folder are reached from it, not listed again. Unopened items are marked **New**, with a count on the nav item; opening one marks it seen. **No shortcuts in My files** (decision 39): Shared with me plus Star cover quick access, and My files stays the person's own. In someone else's folder, the breadcrumbs start at "Shared with me" and the top folder the person can see (nothing above it is shown), with "Alice's folder · you can view" under them, and the nav highlights Shared with me.
- **Leave:** someone with a share on the item itself can leave it ("Leave" in Shared with me and in the Sharing tab). Leaving or losing access also drops their stars and Recent rows for anything they can no longer see.
- **The people picker** lists everyone who has signed in and isn't disabled, matched by name or email (`GET /people?q=`, at most 50). Nobody is invited by email address yet.
- **The share dialog** (owner) adds people as Viewer or Editor, lists the owner and everyone with access, labels roles from a folder above ("Editor of “Projects”, so here too", naming the folder only when the person can see it), and changes or removes this item's own shares. Everyone else sees the same list read-only. The Sharing tab of the details panel shows the same list with "Manage access" for the owner and "Leave" where it applies.
- **Live updates:** a new topic `shares` goes to the owner and the person on grant, change, remove and leave; the person's open app refetches and says "Alice shared “Projects” with you" (§15.4).
- **Public links (next PR):** viewer-only (upload-capable "file request" links come later), with optional password, expiry and allow-download flag; links never expire unless a date is set. Access goes through `/s/{token}` and is rate-limited. Every access is audited.
- **Notifications (the PR after):** in-app and email when something is shared with you.
- **Groups:** not in v1. User-to-user shares are enough for a small group. The model leaves room for `grantee_group_id` later.

---

## 10. Processing pipeline (worker)

### 10.1 Job model

- **Our own `jobs` table** (built in M2): workers claim the most urgent due job with `FOR UPDATE SKIP LOCKED`, so several workers can share it. A claim is a lease of twice the job timeout; a job whose worker died is claimed again once its lease runs out.
- A job is enqueued **per blob per stage** (`subject` is the hash), so identical content is processed once. It isn't queued again while one for the same subject is waiting or running.
- Jobs are **idempotent**: re-running one overwrites its outputs.
- **Retries** with exponential backoff: 30 s, doubling, at most 1 h apart. After 5 attempts → `failed`, visible in the admin UI. A job running longer than 5 minutes is cancelled and counts as a failed attempt. Finished jobs are deleted after 7 days; failed ones stay until retried.
- A file the stage can't read (corrupt, password-protected) isn't a failed job: the stage records that and moves on. Only unexpected errors (disk, database) retry.
- **`pipeline_version` per stage:** bumping it (e.g. a better thumbnailer) lets an admin choose "re-run stage X for all".
- **Concurrency per job type**, configurable in the UI. Defaults are NAS-friendly, e.g. thumbnails 2, video 1, OCR 1.
- **Priorities:** interactive work (a user just uploaded) before bulk imports and rescans.
- **Backfill:** when the worker starts, it queues the thumbnail, metadata and text stages at background priority for every blob without results from the current `pipeline_version`. That covers files from before a stage existed and pipeline upgrades. When video copies are made in the background, it also queues every copy still missing (never ones that failed), and it queues seek previews (§14) for every video of 10 s or more without them (again never failed ones).
- **Timeouts per job type:** the default 5 minutes, except the text stage, which gets 30 minutes because a long scanned PDF is read page by page, the transcode stage, which gets 3 hours (`Worker__TranscodeJobTimeout`) because a long film is re-encoded on the CPU, and seek previews, which get 15 minutes (`Worker__SeekPreviewsJobTimeout`) for up to 100 seeks into a long 4K film.
- **Admin UI** (built, decision 27): the Jobs page at `/admin/jobs` shows one card per queue (running, waiting, failed, done in the last 7 days) with **Pause/Resume**, then the running, waiting or failed jobs, filterable by type. Each job names the **files that use its content**, whoever owns them: name, owner, folder path, and whether it's the current file, an older version or in the trash. Playable copies and seek previews (§14) have their own cards, after the file stages. Tag-rule runs (§13.4) have their own card and name the rule's tag instead of files. Filing (§13.6) has two: `file` jobs name the file being filed, and a filing rule's "file existing" run names its tag (without a link, since filing rules are each person's own). Failed jobs show their last error and can be retried or cleared, one at a time or all at once. Jobs record `started_at`, so running jobs show how long they've been at it. A paused queue is stored in the `jobs` setting (`{"paused": ["text"]}`) and checked in the claim query, so every worker honours it. Still to come: "regenerate missing" and editing concurrency.
- **Per file:** the details panel's Info tab has a **Processing** section with each stage's state (done, working on it, waiting with the number of jobs ahead, paused, nothing found, unsupported, unreadable, failed), and file listings carry the stages still waiting or running so grid and list can show a badge (§16.4). Admins also see a failed step's error and can retry it there. Video and audio files browsers can't play also list their **playable copy** (§14) after the stages: its progress while it's made, "made when someone opens the file" or "turned off" by the admin's setting, and ffmpeg's error for admins when it couldn't be converted. It is left out of the file-list badge, since the viewer shows its own progress. Videos of 10 s or more also list their **seek previews** last.

### 10.2 Stages

```
upload complete (or the backfill)
   ├─► thumbnail (+ preview renditions, Office → PDF)          built (M2)
   ├─► metadata   (exiftool → blob_metadata, content page 0)  built (M3)
   ├─► text       (text layer: PDF, Office, ODF, plain text)  built (M3)
   │      └─► ocr (pages and images with no text layer)       Tesseract now, ML in M7
   │             └─► tag rules (keyword rules → blob_tags)    built (§13.4)
   │                    └─► filing rules (tag → move)         built (§13.6)
   ├─► transcode (video/audio browsers can't play)            built (M2)
   ├─► seek previews (video ≥ 10 s: frames for the timeline)  built (§14)
   ├─► embed     (optional, ML)
   └─► transcribe (optional, ML; audio/video → timed pages)  built (§11.1)

on request
   ├─► page sheets (a PDF's pages as small images)           built (§8.4)
   └─► pdf (merge or split) ─► pdf-save, run by the server    built (§8.4)
```

The stages are independent jobs per blob, queued together when an upload finishes; each detects the format from the bytes itself. Transcode and seek previews are the exception: the thumbnail stage probes video and audio anyway, so it queues the copy when one is needed and seek previews for videos long enough to have them.

### 10.3 Tooling per format

| Format | Metadata | Thumbnail / preview | Text |
|---|---|---|---|
| JPEG/PNG/WebP/GIF/HEIC/AVIF/TIFF/SVG (RAW later) | exiftool | **NetVips** (libvips + libheif) → WebP thumbs, plus a WebP preview when the browser can't show the original | OCR (Tesseract, ML in M7) |
| PDF | exiftool | **PDFium** renders page 1 for the thumbs; the viewer renders pages with pdf.js | PDFium text layer per page → OCR of pages without one |
| DOCX/XLSX/PPTX/ODF/DOC/XLS/PPT/RTF | exiftool | **Gotenberg** (LibreOffice) → PDF rendition → PDFium | The document XML read straight from the zip (one page per slide); .doc, .xls and .ppt from the PDF rendition |
| Video | ffprobe (+ exiftool) | ffmpeg poster frame; **a playable MP4 copy only when browsers can't play the original** (below). Hardware acceleration later: VAAPI/QSV/NVENC. | Transcript (ML, optional, §11.1) |
| Audio | ffprobe / ID3 | Embedded cover art, or else a tile drawn from its waveform; the waveform itself for the player (§14); a playable copy of WMA, ALAC and the like | Transcript (ML, optional, §11.1) |
| Text/Markdown/code/CSV/JSON | — | Rendered first lines as thumb (optional) | Raw text (size-capped) |
| Archives (zip) | entry list | icon | entry names only |

**Thumbnail stage, as built (M2):**
- The format comes from the bytes: `%PDF-` in the first KiB, otherwise libvips' loader, limited to an allowlist (JPEG, PNG, WebP, GIF, HEIF/AVIF, TIFF, SVG) so text files never reach the CSV or matrix loaders. Anything else is `unsupported` and keeps its type icon.
- Two WebP thumbnails at quality 80, never upscaled: `thumb_s` at most 256 px on the long edge (lists) and `thumb_m` at most 640 px (grid tiles). EXIF rotation is applied, colour is converted to 8-bit sRGB, and metadata is stripped. Originals are only read.
- A `preview` WebP of at most 2048 px is added for images browsers can't show (HEIC, TIFF, SVG) and for anything over 4096 px. Other images are viewed from the original.
- A PDF's page 1 is rendered on white at 1280 px on the long edge and scaled down for the thumbs. Its page count and page size are stored.
- The Docker image uses Ubuntu's libvips with the libde265 plugin, because the libvips bundled in NetVips.Native can't decode HEVC, the codec in iPhone HEIC photos. Development builds use the bundled one, so HEIC thumbnails only work in the image (or with `-p:UseSystemLibvips=true` and a system libvips).

**Metadata stage, as built (M3):**
- `exiftool -json -n -fast` on the original, with a 1-minute timeout. The searchable tags (title, subject, description, keywords, author/creator/artist, album, genre, company, comments, camera make and model, lens) become content page 0, so "fujifilm" or a PDF's author finds the file; their values are capped at 4000 characters each.
- The camera ("FUJIFILM X-T30", without repeating the brand), the date taken (with the camera's UTC offset when it recorded one, otherwise the camera's clock as if it were UTC) and the duration are columns of `blob_metadata`, for the `camera:` and `taken:` filters. The offset is kept too (`taken_offset_minutes`), so the details panel shows the camera's own clock. The kept tags, including the lens and the picture size, are stored as JSON for the details panel (§13.5).
- The GPS position (`GPSPosition`, signed decimal degrees; 0, 0 is a receiver without a fix) is kept as `latitude` and `longitude` and shown in the details panel. It isn't searchable yet: place search needs offline geodata.

**Text stage, as built (M3):**
- The format comes from the bytes: `%PDF-`, a zip (`PK`), text by MIME type with no NUL bytes, or an image libvips can load from the same allowlist as thumbnails.
- **PDF:** PDFium's text per page, up to 2000 pages and 200,000 characters a page. A page with fewer than 16 letters is a scan: it is rendered in grey at 300 dpi (at most 3500 px) and recognized, up to 200 pages per file.
- **Office and OpenDocument:** the XML is read from the zip with a 64 MiB uncompressed budget, so a zip bomb can't fill memory: Word paragraphs, one page per PowerPoint slide, Excel sheet names, shared and inline strings, and ODF `content.xml`. DTDs are refused.
- **Plain text:** the first 2 MB, as UTF-8 or Latin-1.
- **Images** of at least 200 px are turned upright from EXIF and recognized.
- **Tesseract** (`swe+eng`, `--psm 1`, one thread; page segmentation with orientation detection, so sideways or upside-down photos and scans are read the right way up, with word boxes still in the image's own coordinates) reads PNGs from stdin and returns TSV; words under 55 % confidence are dropped, and fewer than 3 words count as no text unless one word of 4+ letters is at least 85 % sure (a title or a sign). When a photo gives fewer than 3 words, it gets a second look inverted with a white margin, which finds light text on dark or busy backgrounds and text up against the edge; the pass with more words wins. Words keep their boxes as page fractions, for marking hits in the viewer (§14). The engine and its version are stored, so the ML `/ocr` endpoint (M7) can replace it behind the same `ITextRecognizer` interface and re-read only what it would improve.
- **In development the worker runs in the image.** The Aspire AppHost builds the image's `runtime` stage (the app without the web UI) and runs the worker from it, so exiftool, Tesseract and libvips match production on any machine. `--WorkerOnHost=true` runs it as a project instead, for debugging.
- **A missing tool doesn't fail jobs.** When the worker runs outside the image (`--WorkerOnHost`) and exiftool or Tesseract isn't installed, it logs one warning. Metadata jobs then store nothing, and text jobs keep a PDF's text layer and mark scans and photos as skipped. The next worker start queues those files again once the tool runs. The commands are `Worker:ExifToolCommand` and `Worker:TesseractCommand`, a name on PATH or a full path.
- With text recognition turned off (an admin switch, default on), scans and photos are recorded as skipped; turning it on queues them at background priority. "Read all files again" re-queues every blob.
- The stage replaces a blob's pages in one transaction, so a search never sees half a document.

**Office documents, as built (M2, decision 33):**
- **Gotenberg is optional** and set up by an admin: its address under Admin › Settings › Office documents (empty means off), with a Test button that checks `/health` reports LibreOffice. `docker-compose.yml` runs it under the `office` profile with a 3-minute API timeout. `GOTENBERG_URL` in a process's environment wins over the setting, and the admin page then shows the address read-only. Each process reads its own, because the server on the host and a worker in a container reach Gotenberg at different addresses; the worker's start queues documents recorded as unsupported before it was set. The Aspire AppHost starts Gotenberg and sets it for every role, so Office previews work in development with no setup.
- **The thumbnail stage converts** Word, Excel, PowerPoint and OpenDocument files (by MIME type, with the bytes checked: a zip, an OLE file for `.doc`/`.xls`/`.ppt`, or `{\rtf`) through `POST /forms/libreoffice/convert`, with one page per spreadsheet sheet so wide sheets aren't cut into strips. Macros never run (LibreOffice's default). The PDF is kept at `previews/ab/{sha}/document.pdf`, and its page 1 is rendered by PDFium into the usual thumbnails.
- **Failures:** a file Gotenberg refuses (damaged, password-protected) is recorded as failed and not retried; Gotenberg being down or busy fails the job, which retries with the usual backoff. While Office previews are off, documents are recorded as unsupported, and saving an address queues those at background priority.
- **Merging and splitting PDFs** uses PDFium in the worker, not Gotenberg, so it works without Office previews set up (§8.4).
- **Text:** `.docx`, `.xlsx`, `.pptx` and ODF are still read from their XML. Formats the text stage can't read (`.doc`, `.xls`, `.ppt`) are read from the PDF instead: making the PDF queues the text stage for them again.

**Video and audio, as built (M2, decision 32):**
- **The thumbnail stage probes** anything libvips doesn't take with `ffprobe` (JSON, 1-minute timeout), limited to an allowlist of media containers so images, subtitles and text are never treated as video. It stores `blob_media` per blob: container, duration, whether there is video and audio, the codecs, the picture size as shown (after the rotation a phone records), and the **playback plan**. Other stages read it too; transcription (M7) runs only for blobs with audio.
- **The poster** is the most typical of 60 frames (`thumbnail=60`) from 10 % into the video (at most 60 s in), or from the start for short clips, turned upright, made into the usual WebP thumbnails. Audio files use their embedded cover art; without one they keep the type icon. Cover art is not a video stream for planning (`0:V:0`).
- **The plan** counts only what every current browser plays, so Firefox on Linux gets the same answer as Safari: H.264 (8-bit 4:2:0), VP8, VP9 or AV1 video and AAC, MP3, Opus, Vorbis or FLAC audio. **Direct** files (H.264/AAC in MP4, VP9/Opus in WebM, MP3, FLAC, Ogg, WAV, AAC in M4A) are played from the original. **Remux** files have streams that play in the wrong container (H.264 in a `.mov`, `.mkv` or `.ts`): the streams are copied into MP4 in seconds, with no loss. **Transcode** files need converting (HEVC, 10-bit H.264, MPEG-4 Part 2, MPEG-2, WMV, AC-3, PCM in a video, WMA, ALAC).
- **The copy** (the transcode stage) is one MP4 per blob at `previews/ab/{sha}/playable.mp4`: the first video and audio streams only, each copied when it plays and otherwise re-encoded with libx264 (`veryfast`, CRF 23, High profile, 4:2:0) and AAC (160 kb/s stereo, 192 kb/s for audio only), `+faststart` so it plays before it's fully loaded, and metadata stripped. Converted video fits within the admin's size (1080p by default, as a 1920 × 1920 box so portrait phone videos keep 1080 × 1920; 720p or the original's size). HDR (PQ, HLG) is tone-mapped to SDR with zscale; if that fails the copy is made without it. Progress is written every 3 s from `-progress` and read by the viewer; it sends no live notice per step, only when the copy is done. A file ffmpeg can't convert is recorded as failed, with ffmpeg's last error for admins, and not retried. One copy at a time by default (`Worker__TranscodeConcurrency`).
- **When copies are made** is an admin setting: **in the background** (default: queued after upload at the upload's priority, and by the backfill), **when someone opens one** (the viewer asks, at interactive priority, and the first person waits), or **never** (the viewer offers Download). Opening a file whose copy is still waiting moves it ahead of background work. Admins see how many copies exist, their disk use, how many are still to make and how many failed, and can delete them all; they are made again as the setting says.
- **HLS is not used.** One MP4 with range requests seeks fine in every browser, and is one file to clean up. Hardware encoding (VAAPI, QSV, NVENC) can be added behind the same stage.
- **A missing ffmpeg doesn't fail jobs.** Outside the image, the worker logs one warning, and files libvips doesn't take are left without a thumbnail record, so the next worker start queues them again once ffmpeg runs. The commands are `Worker:FfmpegCommand` and `Worker:FfprobeCommand`.
- **Seek previews** (decision 40) are the frames the player shows above its timeline while seeking (§14), made by their own `seek-previews` stage so posters aren't held up. The thumbnail stage queues it, at the upload's priority, for every video of at least 10 s. **One frame for every 1 % of the video, but never closer than 2 s apart**, so a 30 s clip gets 15 frames, a 2-minute video 60 and anything over 200 s the full 100; each frame stands for its stretch of the timeline and is taken from the middle of it. Each frame is its own quick ffmpeg run that seeks before opening the input (`-ss` before `-i`), so only the few seconds after a keyframe are decoded, and a 2-hour film doesn't have to be read end to end. Frames come from the original, upright, 256 px on the long edge (never scaled up) and all the same size; a frame ffmpeg can't read borrows its neighbour's, and a video with none at all fails the job, so an admin sees it. NetVips joins them into one WebP at quality 70, **at most 10 frames to a row**, left to right and top to bottom, kept at `previews/ab/{sha}/seek.webp` as the `seek` rendition (the sheet's size is its width and height; the layout follows from the length, so nothing else is stored). A 4-minute video's sheet is 2560 × 1440 and usually a few hundred kB. One video at a time (`Worker__SeekPreviewsConcurrency`), 15 minutes per job. Making thumbnails again keeps them, and "Delete all" for playable copies leaves them alone. There is no admin setting: they are small and always made. HDR frames aren't tone-mapped, like the poster.
- **For other stages**, the worker's `FFmpeg` service (probe, frame, cover, and a runner with progress and a timeout) is the one way to call ffmpeg, so transcription can make its 16 kHz audio the same way.

Licensing note: NetVips/libvips (LGPL), PDFium (BSD/Apache), PdfPig (Apache) and ffmpeg (LGPL build) are all fine. We avoid ImageSharp (split license) and MuPDF, which is AGPL; MuPDF would be acceptable since we are AGPL, but it isn't needed.

### 10.4 Worker sandboxing

The worker parses untrusted files with complex native libraries, so:
- run it as non-root with a read-only root filesystem
- mount originals **read-only**
- give it no outbound network except to postgres, ml and gotenberg (a compose network)
- set memory/CPU limits and a per-job timeout, with subprocess kill for ffmpeg and Tesseract

---

## 11. ML service (optional, remote-capable)

Modeled on Immich's `immich-machine-learning`.

- **A separate image**, written in **Python (FastAPI + ONNX Runtime)**. The ML ecosystem lives there, and the HTTP contract keeps the .NET side clean.
  - Variants: `-cpu`, `-cuda`, `-openvino`, `-rocm`.
- **Stateless:** no DB, no storage mounts.
  - The worker sends **derived inputs**, not originals: page images rendered at OCR resolution, downscaled previews, and 16 kHz mono audio for transcription. This keeps LAN traffic small even for huge files.
  - Models are downloaded on first use into a cache volume and unloaded after an idle TTL.
- **Configured in the admin UI:**
  - **A list of ML URLs, in priority order, each with its own optional API key** (sent as `Authorization: Bearer`, stored encrypted, never returned to the browser). Example: `http://gaming-pc:3003` (GPU) first, `http://ml:3003` (on the NAS) as fallback. The admin page checks each URL's `GET /health` when it opens, and has a test button for a new one. Up to 10 URLs.
  - The worker health-checks each URL and uses the first healthy one. If none is reachable, ML-dependent jobs wait in the queue instead of failing.
  - Per-feature toggles and model selection: OCR model and languages, embedding model, transcription model size.
- **Bulk import scenario:**
  1. Start the ML container on the powerful PC and add its URL in the UI.
  2. Raise the OCR/embedding concurrency.
  3. Import.
  4. Shut the PC down. The worker falls back to the NAS ML instance, or pauses ML jobs.
- **Endpoints (v1):**
  - `POST /ocr`: image → lines with bounding boxes, confidence and detected language. Uses PaddleOCR/RapidOCR ONNX models, with Tesseract as a lightweight engine.
  - `POST /embed/text` and `POST /embed/image`: vectors, from CLIP-style multilingual models for text↔image search.
  - `POST /transcribe`: Whisper (faster-whisper) → timed segments, streamed (§11.1). The first endpoint built, ahead of the rest of M7 (decision 34).
  - `GET /health` and `GET /models`.
- **Results are stored with `engine` + `model` + `version`**, so changing the model triggers re-processing.
- **No-ML fallback:** the worker image includes **Tesseract** (`swe` + `eng`) for basic OCR, built in M3 (§10.3). A server without an ML service still gets searchable scans.

### 11.1 Transcription (speech to text)

The first piece of the ML image, built on the M2 video/audio work, which brings ffmpeg and ffprobe to the worker (decision 34). Mockups: the "Transcription" canvas.

- **The ML side:** the image starts as FastAPI + **faster-whisper** (CTranslate2 rather than ONNX Runtime; Whisper runs best there) with Silero VAD, so music and silence are skipped instead of hallucinated over. `GET /health` and `GET /models` report the loaded model and whether a GPU is used.
  - `POST /transcribe` takes the audio, the model, an optional `start_ms` and, when resuming, the language. It streams NDJSON: first `detected` (language, length, how much is speech, the model and engine), then a `segment` per line (`{start_ms, end_ms, text, words: [[start_ms, end_ms, "word"], …]}`), then `done`, or `error` if it fails part way. An optional API key is sent as a Bearer token.
- **Languages:** Whisper detects the language per file, limited to Swedish and English (the likelier of the two is used), and the result is stored with the transcript. The index is already Swedish- and English-stemmed, so search needs nothing new.
- **Models:** by default the best the service can run: `large-v3-turbo` on a GPU, `small` on a CPU. Admins can pick a size, or **KB-Whisper** (the Swedish National Library's fine-tune) for better Swedish.
- **The worker stage:** a blob whose ffprobe shows an audio stream gets a `transcribe` job at background priority (videos too, unless an admin turns that off). ffmpeg makes 16 kHz mono Opus (about 10 MB an hour), and that is sent, never the original.
  - **Long files:** the job type gets a long timeout (12 h), and the job's lease is renewed as pages are stored. Pages are written as they arrive, so an interrupted job resumes from the last stored time instead of starting over, unless the admin picked another model since.
  - **Service down:** with no service answering, or one that drops mid-file, the job is postponed 5 minutes without counting an attempt, and the first healthy service in the admin's order is used next time.
  - **No speech** (under a few seconds after VAD) is stored as a result ("no speech found") and isn't retried.
- **Storage:** segments are grouped into transcript pages of about a minute, cut at pauses: `content_pages` with `source = transcript`, `start_ms`/`end_ms`, and the page's lines with their word times in `words` (`[{s, e, t, w: [[s, e, "word"], …]}, …]`), so hits map to times and lines come back for the transcript panel. A `blob_transcripts` row per blob holds the state (partial, ready, no speech, failed), language, model, length and how far it has got. Like all text, a transcript is keyed by blob, stored with `engine` + `model` + `version`, and the per-version corrections of decision 28 apply to it (fix a misheard name).
- **Transcribing again:** a finished transcript isn't redone by itself; a model change applies to recordings transcribed from then on. "Transcribe again" in the viewer's transcript panel (`POST /nodes/{id}/transcript/redo`, ahead of background work) and "Transcribe all again" under Admin › Speech to text (`POST /admin/transcription/redo`, in the background) start over from the beginning. The old transcript stays readable and searchable until the new run starts. The panel names the model that made it.
- **Without an ML service:** no transcribe jobs are queued, so nothing piles up; adding an ML URL queues every recording at background priority. With URLs set but none healthy, jobs wait (as above). Whisper isn't added to the worker image: it would add hundreds of MB and hours of NAS CPU, and the NAS can run the ML image itself (`--profile ml`) with the small model.
- **Set by the environment:** `Ml__Url` (and `Ml__ApiKey`) adds a service after the admin's list, shown read-only on the admin page. Local development (Aspire) runs the ML image and uses it, since the server on the host and the worker in a container reach the service at different addresses.
- **Admin › Settings › Machine learning:** a "Speech to text" section with an on/off switch (on), the model, "Transcribe the sound in videos too" (on), and the queue's size in recordings and hours. With no service added it says that speech to text needs one.
- **Search:** a transcript hit shows "At 12:34 · In spoken words" instead of a page number, with the usual snippet (§13.3). `GET /nodes/{id}/matches` returns times instead of boxes for transcript pages. `GET /nodes/{id}/transcript` returns the state and the lines.
- **Viewer:** opened from a hit, the player starts 10 s before it, so the sentence is heard from its start. A transcript panel (a sheet on phones) lists the lines with times, marks the hits, follows along while playing, seeks when a line is clicked and has its own find box; the player's timeline shows a tick for each hit, and the header stepper moves between them. Videos get **captions** drawn over the picture from the transcript, with a CC toggle that is remembered; the same captions as WebVTT (`GET /nodes/{id}/captions.vtt`) go to the phone's own full-screen player.
- **Details** (not built yet; comes with the Processing section of the processing-jobs proposal): a Transcript section (word count, the start, Open transcript, Correct text, download as .txt or .vtt), the detected language under Details, and a "Speech to text" row under Processing (running with the time reached, waiting for the service, no speech found, off on this server).

---

## 12. Trash and retention

Built in M1 (decision 18).

- **Trash a node:** set `trashed_at` on it and on everything live below it, and `trashed_root_id = node` on all of them, in one subtree update via `path`. Items trashed earlier inside it keep their own group. Trashed nodes are hidden from folders, the viewer, downloads and search, and their names no longer count for uniqueness. The duplicate check still finds them ("in the trash").
- **Trash page:** lists only the groups' roots, newest first, with the original location, when it was trashed, "Deleted forever in N days" (amber with an icon in the last 7 days) and the size, a folder's contents included. Trashed items can't be opened; opening one offers to restore it. Browsing inside a trashed folder may come later.
- **Restore** brings back exactly the items with that `trashed_root_id`. If the original folder is gone or itself in the trash, the item goes to the top of "My files" and the toast says why. If the name is taken, it gets a number, as with Keep both. Moving to the trash shows an Undo toast, which is a restore.
- **Trash counts toward quota**, as in Google Drive. The storage meter says how much emptying the trash would free.
- **Retention:** an admin setting (the `trash` key, default 30 days), next to the version limits in the "Trash and versions" section. Changes apply to what is already in the trash.
- **"Delete forever" and "Empty trash"** are available to the owner. Both confirm with a danger button and are audited. The rows go at once; the purge deletes the files within seconds.
- **The purge** runs in the server role, because it deletes originals and the worker mounts them read-only (§10.4). It runs on start, **every hour** (so the countdown holds to the hour), and right after a delete forever. Each run, in batches of 200:
  1. Deletes trash groups older than the retention, a whole subtree per transaction, with a `node.purge` audit event.
  2. Deletes versions past the version policy (§8.2), with a `version.purge` audit event each. It is the same rule as the API's "Removed in N days", written as one SQL query with window functions.
  3. For each deleted version, lowers the owner's `blobs.ref_count`, and lowers the owner's `used_bytes` by the blob's size once they have no version left with those bytes.
  4. Deletes blobs whose `ref_count` is zero: the row and that owner's original, and, once no one has those bytes any more, their preview state, renditions and thumbnails.
- **Blob lock:** uploads and the purge take the same transaction-scoped advisory lock per blob hash (for all owners, so the last copy and its renditions are deleted safely). The upload holds it while it moves its temp file into the owner's folder and records the version; the purge holds it while it deletes the row and the files. So a file that someone uploads again at the moment the purge deletes it is never lost: the upload either finds the old file still referenced or stores its own copy.
- An email warning "N items will be permanently deleted in 3 days" waits for notifications (§20).

---

## 13. Search

### 13.1 Engine: ParadeDB `pg_search` (BM25) and VectorChord, both inside Postgres

- **Why not plain Postgres FTS?** `ts_rank` is not BM25, and relevance on long OCR text is noticeably worse. pg_search gives us:
  - Tantivy-backed BM25
  - fuzzy matching and phrase queries
  - highlighting/snippets
  - Snowball stemmers, **including Swedish**

  It all stays inside Postgres, so **permission filtering is a normal SQL join**. No second system has to be kept in sync with ACLs.
- **Why VectorChord?** It is the vector extension Immich standardized on. It's faster and lighter than plain pgvector at scale (IVF + RaBitQ). It's only needed when semantic search is enabled.
- **Cost:** we ship and maintain **our own Postgres image**, as Immich does ([`docker/postgres`](../docker/postgres/Dockerfile)).
  - Base: official `postgres:17` + `pg_search` + `pgvector` + `vchord`, preloaded via `shared_preload_libraries`.
  - The extension files are copied from the upstream `paradedb/paradedb` and `tensorchord/vchord-postgres` images at pinned versions. Both are published for amd64 and arm64, so our image is multi-arch without per-arch downloads.
  - Extension versions are pinned per release, and the server **checks extension versions on startup** and refuses to start on a mismatch.
  - Upgrades: we document upgrade steps and `REINDEX` when needed.
- **Fallback:** search sits behind an `ISearchEngine` interface. A plain-FTS implementation can exist for people running an external stock Postgres.

### 13.2 What is indexed

Built in M3 (decision 26). Two BM25 indexes, created in the migration with pg_search 0.20's tokenizer casts:

- **`nodes.name`:** split into words by the `simple` tokenizer (so "IKEA-kvitto.pdf" is three words) and into 3-grams, for partial matches such as "4471" in "IMG_4471.jpg".
- **`content_pages.text`:** the same text indexed three times, Swedish-stemmed, English-stemmed and unstemmed, and every query searches all three. This beats guessing a language per page, which is unreliable on short receipts and mixed-language documents.
- **Text is keyed by blob** (§6). A query joins pages to files through each file's current version, so old versions and trashed files never match, and a copy is searchable at once.
- **Filters** are SQL on the candidates, not index fields: type (MIME group), modified date, folder subtree (`path`), starred, camera and date taken (`blob_metadata`). Owner and shared-with-me join with sharing (M4).
- Trashed nodes aren't removed from the index; the join leaves them out.

### 13.3 Query

- **The search bar** suggests names as someone types (`GET /search/suggest`, no text, ~6 rows). Enter opens the search page, `/search?q=…`, with its filters in the URL so a search can be bookmarked.
- **The query language:** words, `"exact phrases"`, `-excluded` words and the operators `type:` (pdf, doc, sheet, slides, image, video, audio, text, archive, folder), `in:` (folders with that name, and everything below), `after:` and `before:` (modified), `camera:`, `taken:` (a year, month or day), `tag:` (a tag, `tag:"tax 2026"` for one with spaces), `is:starred`, `owner:me` (only the person's own files) and `is:shared` (only files someone else shared with them). Unknown operators are searched as words. The chips on the search page (Type, Modified, In, Starred, Tag, and a Relevance / Last modified sort) set the same filters. Queries are capped at 20 words.
- **Matching:** every word must match, in the name, the text, the metadata or the file's tags (§13.4). In text a word matches its Swedish or English stem or, from 5 letters, one typo; in names a word matches whole (boosted), as a prefix, with one typo from 5 letters, or by its 3-grams. Phrases match the unstemmed text in order.
- **Ranking:** the name's BM25 score, weighted ×2, plus the best page's. At most 1000 candidates are ranked; that is plenty for a person's own files.
- **Results are files, not pages** (`GET /search`). Each has its best page: the page number ("Page 2 of 5", "Slide 3 of 12"), how many pages matched, where the hit was (name, text, a scan or photo's recognized text, or the file's details), and a snippet. The snippet is about 220 characters of plain text around the most hits, with the hits as `[start, end)` offsets, never HTML, so file content can't inject markup.
- **Marking hits** uses one rule on the server and in the clients (`Highlighter` in C#, `matchesTerm` in `@dokument/core`): the word itself, the word plus an ending of up to 4 letters (from 4 letters: "kvitto" marks "kvittot"), or one typo from 5 letters. It approximates the stemmers closely enough that what is marked is what matched.
- **Opening a result** opens the viewer on the best page with the hits marked. `GET /nodes/{id}/matches?q=` lists the file's pages that match, with OCR word boxes for recognized pages; text pages are marked by the viewer from the PDF's own text layer. A stepper in the viewer's header moves between pages with hits, and the arrows step through the results.
- **While files are still being read,** the page says "Still reading text in N files" and polls (every 3 s, then 15 s) until they're done.
- **No results with filters** says how many there would be without them and offers to show them.
- **Privacy:** queries are never logged, audited or sent to telemetry (§22.1), and recent searches aren't stored on the server.
- **Hybrid search (optional, M7):** BM25 results and vector results are merged with reciprocal rank fusion. Examples: "kvitto från IKEA" finds the receipt even when the OCR is poor, and "beach sunset" finds photos.
- **Scope:** search *always* applies the same effective-permission predicate as browsing (§9): the candidates are joined with `NodeAccess`, so only files the person could open are found or counted, and `/matches` answers 404 for anyone else's file.
- **Sound and speech:** audio tags (title, artist, album, comment) are searchable now through the metadata stage. Spoken words come with transcription (§11.1, decision 34): Whisper segments become `transcript` pages with times, and opening a hit plays from there.

### 13.4 Tags

Built after M3 (decision 29).

- **A tag is a word or a few words on a file**, such as "receipt" or "tax 2026": at most 50 characters, trimmed, NFC, no commas or line breaks. Two tags are the same when they match ignoring case (`tag_key`); the first spelling used on files the person can see, or a rule's, is kept, so "Receipt" joins "receipt". Files only: a tag on a folder would raise whether it applies to everything inside.
- **Tags are shared:** everyone who can see a file sees its tags (not per person, unlike stars). Anyone who can change the file adds and removes them: the owner and editors (§9). Suggestions and counts only ever come from files the person can see, so a tag on someone's private file never shows.
- **Sources:** `user` (someone added it), `rule` (an admin's keyword rule matched the text) and `ml` (the ML service, M7). Automatic tags look different, tinted with a small icon, and say on hover which rule added them.
- **Automatic tags belong to the bytes** (`blob_tags`), like text: identical files get them once, and a new version gets those of its own bytes once its text is read. Tags people add belong to the file (`node_tags`) and stay through new versions. A tag someone adds that a rule also gives is theirs, so it stays if the rule stops matching.
- **Removing an automatic tag** is recorded for that file (`node_tag_removals`) and sticks when rules run again and through new versions. The details panel says "Removed from this file: receipt · Put back"; adding the tag again lifts the removal. What a file shows is the `effective_node_tags` view.
- **Keyword rules** are a tag plus words: `kvitto, receipt, "tack för ditt köp"`. A word matches as itself, as a Swedish or English stem, or, from 5 letters, as the start of a longer word ("kvitto" in "kvittot", which the Swedish stemmer keeps apart); a quoted phrase matches word for word. **There's no typo tolerance**, unlike search: one typo would let "kvitto" match "kvitta" and tag files it shouldn't. Rules match the stored text and metadata (content pages, page 0 included) through the BM25 index, so they never read a file again. One rule per tag; at most 500 rules.
  - The worker runs every enabled rule on a blob right after its text stage and its metadata stage finish.
  - Adding or changing a rule queues a `tag-rule` job (background priority) that replaces the rule's tags in one query and one insert. Turning a rule off or deleting it takes its tags off at once. Rule tags have the rule's id, so a renamed tag follows.
  - **Admin → Tags** (`/admin/tags`, between Jobs and Settings) lists the rules with how many live files show each tag, "Running" while one is being run, an on/off switch and Edit. The edit dialog previews, without changing anything, how many files would get the tag and lists them, newest first, 50 at a time, each with its owner and a snippet. Admins see every owner's file names there, as on the Jobs page.
- **In the clients:** a Tags section in the details panel's Info tab, under the file facts, with a chip per tag (remove button, tapping the name searches for it) and Add tag, a field with suggestions from tags on files the person can see, most used first, then "Create tag". Selecting files adds a Tags action: a dialog with the tags all of them have and those only some have (dashed, "1 of 3", with + and ×); nothing changes until Save, and the snackbar offers Undo. List rows on wide screens show up to two small tags after the name; the grid and phones show none. Search results show a file's tags.
- **Search:** `tag:receipt` and the Tag chip are exact filters. **Plain words also match tags**: a word that is a word of a tag on files in scope also counts as found on those files (scored like a good name match), so "receipt" finds a Swedish receipt tagged receipt and "receipt hornstull" finds it when "hornstull" is in its text. At most three words are tried this way. The search bar suggests up to two matching tags above the names; picking one opens the search page filtered to it.
- **API:** `GET /nodes/{id}/tags` (tags and removed ones), `POST /nodes/tags` (`ids`, `add`, `remove`, up to 1000 files and 20 tags), `GET /tags?q=` (suggestions with counts), tags on every `NodeDto`, and `/admin/tag-rules` (list, create, update, delete, `preview`). Changes send a live `files` notice and write `file.tags_change` per file, naming the tags (§18).
- **Not yet:** renaming or merging a tag across all files, tag colours, a Tags item in the main nav, and copying tags with a file copy (when copying lands, the copy gets the file's tags and removals).

### 13.5 Details and text in the file info

Built after M3 (decision 28). The details panel's Info tab shows what the worker read from a file, and people can correct it.

- **Details** shows what the current version says about itself, only the rows it has: title, description, date taken (in the camera's clock, with its UTC offset when known), camera, lens, picture size, length, pages, place (coordinates and an "Open map" link to OpenStreetMap; place names wait for offline geodata), then subject, author, artist, composer, album, genre, company, keywords and comment. `GET /nodes/{id}/details`.
- **Text in this file** shows where the text came from (the file's own text, recognized in a scan or photo, or added by people), the word count, and the start of the first page. "Show all text" opens a dialog with each page labelled with its source, and Copy for all of it. A file of up to 10 pages shows them all; a longer one shows one page at a time, with previous, next and a field to go to a page (pages without text show as empty, so text can be added there). A file whose format has no text (a zip) has no Text section; a photo with no text says so and offers "Add text". While the text is still being read, the section says search won't find words inside the file yet. `GET /nodes/{id}/text?from=&limit=`.
- **Editing the details:** the title, the description and the date taken. A date can be known to the year, month, day or minute ("1974", "1974-07", "1974-07-14 15:30"), for scans of old photos. Camera, lens, size, length and pages are facts about the file and can't be changed; keywords stay read-only because adding words to a file is what tags (§13.4) are for. Edited rows are marked and show what the file says, with "Use this" to drop the edit. A value the same as the file's isn't an edit. `PUT /nodes/{id}/details` sends all three; null keeps what the file says, and an empty title or description hides the file's.
- **Editing the text** works one page at a time, so a long scan never becomes one huge text box. Text the same as what was read drops the correction, and "Use the text read from the file" does too (`PUT` and `DELETE /nodes/{id}/text/{page}`). Text can be added to a page where nothing was read, up to the file's page count (one page for files without pages). A page holds at most 200,000 characters, the text stage's own cap.
- **Edits belong to the file version, never to the bytes** (§6): what was read is keyed by blob and shared by everyone with the same bytes, so fixing your copy must not change someone else's. A new upload starts with the previous version's details edits, but not its text corrections, because new bytes have new text. Restoring a version brings back that version's edits, text included. "Read all files again" or a better recognizer replaces what was read underneath and leaves the edits alone. The original is never changed (§1), and Download still gives the exact bytes.
- **Search uses the edits.** An edited page replaces the read page of the same number for that version only; the version's page 0 holds the edited title and description and adds to what was read, so the file's own title still matches. An edited date wins over the camera's in `taken:`, and a partial date counts from its first day ("1974-07" matches `taken:1974` and `taken:1974-07`). The viewer still marks hits on a corrected scan from the read word boxes, so words people changed are found but not marked.
- **Who:** everyone who can open the file sees its details and text; everyone who can change it (the owner and editors, §9) can edit them. Link viewers (M4) won't see the place. Each save writes a `file.details_edit` audit event naming the fields, or `file.text_edit` naming the page, never the text, and sends a live notice (`files`), so other tabs refresh.
- **Later:** opening the text dialog from the viewer on the page being shown, and writing edits into an exported copy's EXIF.

---

### 13.6 Filing rules

Built after tags (decision 30). The case: upload a receipt, and it ends up in `Kvitton/2026` with the receipt tag, without anyone moving it.

- **Filing rules are each person's own,** in a Filing rules section of their Settings (`/settings`), not an admin's: moving files is the owner's business, and an admin's rule shouldn't rearrange someone's folders. A rule is a tag plus a folder: "When a file of mine gets **receipt**, move it to **Kvitton/{year}**". One rule per tag, at most 100 per person.
- **Folders** are a path from the top of My files, folders separated by `/`, which may hold `{year}` and `{month}` (`Kvitton/{year}`, `Räkningar/{year}/{month}`). The month is two digits, so the folders sort in order. Other `{…}` are refused, and every folder must make a valid name. The date is the date taken people gave the version (§13.5, as written, so a scan dated "1974" goes to 1974), else when the photo was taken (`blob_metadata`), else the file's modified time on the device it came from, else when the version was uploaded, on the server's clock (`TZ`). Missing folders are made; a folder name held by a file stops the filing; a file name already taken in the folder is numbered as an upload would ("kvitto (1).pdf").
- **When it runs:** a `file` job (subject: the node id, concurrency 1) runs the owner's rules on one file. It's queued when a file gets a tag (`POST /nodes/tags`), when a keyword rule tags its bytes (after the text or metadata stage, or when an admin changes a rule), and when a file gets new bytes (upload, new version, restore, copy), since bytes that already have tags may match at once. Only files whose owner has a rule on, not in the trash, and not filed before, are queued.
- **Only tags got while the rule was on:** the `effective_node_tags` view says since when a file has each tag (when someone added it, or when its current bytes got it or were uploaded, whichever is later), and a rule matches a tag only from its `active_since` (made, turned back on, or given another tag). So a new rule doesn't move the files that already have its tag, unless the person ticks "Also file the N files that have this tag now", which queues a `filing-rule` job for those. Keyword rules keep the tags blobs already had when they run again, so their time stays.
- **Shared folders:** rules only ever move the owner's files, and never out of sight of someone a file is shared with. If the move would take a file out of a folder shared with someone into one they aren't in, the file stays where it is, a `node_filings` row with `skipped` records it so it isn't tried again, and the rule's line in Settings says "Left 1 file where it was: moving it would hide it from people it's shared with". A file an editor adds to the owner's folder is filed by the owner's rules.
- **Once, and reversibly:** a file is filed at most once, by whichever rule (`node_filings`, one row per file). If the person moves it back or elsewhere, or presses Undo, it stays there. The move is a `node.move` in the audit log with the actor `system`, naming the rule and the tag (and the new name if it was numbered); folders it makes are `folder.create` by the system. Deleting a rule leaves its files where they are.
- **Telling the person:** the worker sends a `filings` live notice. The web app waits for notices to settle (0.8 s), fetches `GET /filings/recent?after=` (the person's filings after the last ones shown, still where the rule put them) and shows one snackbar wherever the person is: "Filed IMG_4471.jpg in Kvitton / 2026", or "Filed 3 files in Kvitton / 2026", with Undo (`POST /filings/{id}/undo`, which moves each back where it was, or to the top of My files if that folder is gone). The upload tray doesn't say it, since a tag can arrive minutes after an upload (OCR). The Activity tab shows the move ("The filing rule for “receipt” moved …").
- **Order:** rules are ordered (Move up and Move down in a rule's menu); the first enabled rule that matches wins, so a file with two tags moves once.
- **Settings:** each rule shows its tag, its folder ("Kvitton / Year"), how many files it filed with an example ("for example Kvitton / 2026"), an on/off switch and a menu (Edit, Move up, Move down, Delete). The dialog has the tag (with suggestions), the folder with Year and Month buttons that insert the placeholders, a preview of where two of the files that have the tag now would go, and the "Also file" choice, on by default for a new rule.
- **API:** `/me/filing-rules` (list with `filed` counts and `running`, create, update, delete, `PUT /order`, `POST /preview`), `GET /filings/recent`, `POST /filings/{id}/undo`. Rule changes write `filing_rule.change` and send an `account` notice.
- **Not yet:** shared folders (M4: a file in a folder shared with others should only be filed if it stays visible to the same people, or the rule asks first), the mobile app, and conditions beyond a single tag.

## 14. Viewers

| Type | Web | Mobile |
|---|---|---|
| Images | The original, or the WebP preview for HEIC/TIFF/SVG and huge images; zoom/pan; EXIF details in the details panel (§13.5) | Same; `expo-image` |
| PDF | **pdf.js** with page thumbnails, search-in-document, OCR text-layer overlay for scans | `react-native-pdf` (or pdf.js in a WebView) |
| Office | The server's **PDF rendition** in the PDF viewer, with a note that it's shown as a PDF | Same |
| Video | Our own player over `<video>` (HTTP Range, the poster): play, ±10 s, a timeline with hit ticks and the time under the pointer, and the frame from that moment (seek previews), volume, speed, captions, full screen; controls hide while playing; keys Space/K, J/L, arrows, M, C, F while it has focus. The playable copy if the original isn't playable | `expo-video` with the system's controls for now; its own timeline with seek previews later |
| Audio | The same player's controls under the cover art (a large play button, ±10 s, speed, volume), with the file's **waveform as the timeline**; playlist for a folder later; transcript panel with timed hits (§11.1) | `expo-audio` (background playback); the same waveform drawn with `react-native-svg` |
| Text / code / Markdown | CodeMirror 6 (read-only) / formatted Markdown with a Source toggle | The same views as Expo DOM components (M5) |
| Other | Metadata + download | Download / "open in…" |

- **Built in M2 (web):** a full-screen viewer at `/files/{id}` for images (zoom steps from 50 % to 400 %, double-click, drag to pan), PDFs (pdf.js with range requests, one page at a time fitted to the width, Page Up/Down), text (the first 1 MiB; see below), and audio and video the browser can play. Everything else shows "No preview" with Download. Arrow keys or a swipe move between the files in the folder; Esc closes. PDF page thumbnails and free search in the document come later.
- **Text and Markdown, built in M2 (web, decision 31):** text files open in a read-only **CodeMirror 6** editor, loaded only when one is opened: line numbers, folding, syntax highlighting with the language picked from the file name by `@codemirror/language-data` (each language is its own small chunk), and find with Ctrl+F or the top bar's search button. A top-bar toggle wraps long lines and is remembered per browser. Markdown (`text/markdown`) opens **formatted**, with a Formatted / Source toggle in the top bar; Source is the same editor. Markdown is GitHub-flavoured (tables, task lists, strikethrough, autolinks) through `react-markdown`, and raw HTML in a file stays text, so nothing in a file runs in our page. **Images from the web are not loaded** until "Load images" is pressed, so a remote server can't tell the file was opened, and then load with `no-referrer`. Web links open in a new tab with `noopener noreferrer`. Relative links and images (other files in the drive) show as text for now. Code blocks are highlighted by the same language data. Syntax colours are the `code-*` tokens (§16.2). Search hits are marked in both views.
- **Video and audio, built in M2 (web, decision 32):** grid tiles of video and audio show the duration and the poster or cover art. The viewer plays the original when browsers can, and otherwise `GET /nodes/{id}/preview` serves the playable copy. While the copy is on its way the viewer says so, with its progress (polled every 3 s); opening the file asks for it (`POST /nodes/{id}/playable`), which in the "when opened" mode starts it and otherwise moves it ahead of background work. If no copy can be made, or copies are off, it says the file can't be played here and offers Download. Browsers that say they play HEVC (Safari, some Chromium builds) get the original first, for full quality, with `?original=true`, and fall back to the copy if it fails. Audio shows its cover art (`media.hasCover`), or the note when it has none.
- **Audio waveform, built (web, decision 41):** an audio file's timeline is its waveform, drawn the way Audacity draws a whole recording: one lane with the channels mixed, the peaks as a pale outline and the loudness (RMS) as a solid core around the middle line, the played part in `primary` and the rest in `outline`, search-hit ticks as full-height `warning` lines, a playhead with its knob, and the time under the pointer. Click, drag and the slider's keys seek as before. Each file is scaled to its own loudest moment, so a quiet meeting recording still shows its shape, and silence shows as a flat line, which is where the transcript says "No speech". It is 112 px tall (80 px on phones) and grows out of the middle line when it arrives (instantly with reduced motion); until then, or if it can't be read, the plain timeline is shown. No zoom and no lane per channel, for now. Videos don't get one; their timeline is for seek previews.
  - **Made by the thumbnail stage**, which probes audio anyway: ffmpeg decodes the first audio stream once as 8 kHz mono `s16le` on a pipe, and the worker folds it into slices as it streams (the lowest and highest sample and the RMS of each), so hours of audio never sit in memory. Slices start at 20 ms; when there are more than 32,768, neighbours merge in pairs, so a 3-hour recording is still under 100 KB. It is stored at `previews/ab/{sha}/waveform.bin` as a `waveform` rendition, and `blob_media.has_waveform` says it exists.
  - **The format** (little-endian): `DKWF`, version 1, three zero bytes, the seconds per slice as float32 and the slice count as uint32, then three bytes per slice: the lowest sample's size, the highest sample and the RMS, each 0–255 with 255 the loudest sample in the file. `@dokument/core` reads it and folds it into columns (`parseWaveform`, `waveColumns`, `wavePaths`), so web and the app draw it the same way.
  - **`GET /nodes/{id}/waveform`** serves it, like any rendition: cached by blob, not audited. `NodeDto.media.hasWaveform` says when to ask for it.
  - **Tiles:** audio without cover art gets its waveform as its thumbnail: bars in the `file-audio` hue on a clear ground, so the one image reads in both themes. `media.hasCover` tells the viewer whether the thumbnail is cover art to show above the controls.
- **Seek previews, built (web, decision 40):** pointing at a video's timeline shows the frame from that moment above it, with the time under it in the same chip as before; on touch screens the frame shows while the finger drags, and the video seeks when it lets go. The frame fits a 192 × 171 box on desktop (192 × 108 for landscape video, 96 × 171 for portrait) and 144 × 128 on touch screens, so it never covers much of the picture, and it stops at the ends of the timeline so the first and last frames are whole. `NodeDto.media.seekPreviews` gives the layout (`intervalMs`, `count`, `columns`, `frameWidth`, `frameHeight`) once the sheet exists, and `GET /nodes/{id}/seek-previews` serves it, not audited, cached like other renditions. The player loads the sheet the first time the pointer comes to the timeline, not before, since most videos are watched without seeking; until it has loaded, only the time shows. Frame `i` covers `i × intervalMs` up to the next one, and the last also covers what's left at the end. The worker's live notice when the sheet is made updates an open viewer. **The phone app** plays video with the system's own controls (`expo-video`), which can't show our frames; it gets its own timeline with seek previews after the app's first pull request has merged.
- **Office, built in M2 (web, decision 33):** documents with a PDF rendition (`NodeDto.shownAsPdf`) open in the PDF viewer, with a note that the layout may differ a little and that the original is the one to download for editing. While the worker is still on one, the viewer says the preview is being prepared; with Office previews off, it shows "No preview" and Download.
- **Built in M3 (web):** opened from search, the viewer marks the hits like a highlighter: on PDF text pages from pdf.js' text layer (positions measured in the page's font), on scanned pages and photos at the recognized word boxes, and in text files inline. It opens on the best page, and a header stepper ("1 of 3") moves between pages with hits; closing the marks keeps the file open.
- **Text toggle (web, decision 38):** photos with recognized words and every PDF get a **Text** button (`document_scanner`) at the end of the viewer's toolbar; on phones it is a round button above the toolbar at the right, since the toolbar is full on PDFs. When on, each word sits in a soft `primary` box, under the search marks, so a file opened from search shows both. The words are invisible text stretched over their boxes, so dragging (or a long press on a phone) selects them and copying gives the words in reading order, with spaces within a line and line breaks between lines. On wider screens a **Copy** button next to it copies all of the image's or page's text. Scanned pages and photos use the recognized boxes from `GET /nodes/{id}/words?page=` (which also lists the pages that have any); other PDF pages use the PDF's own text through pdf.js, measured into words. A page with no words says "No text on this page". The toggle stays on while stepping between files and is off the next time the viewer opens. Boxes are what was read, so on a corrected page (§13.5) the words people changed are not shown; older versions get no toggle on images.
- **`GET /nodes/{id}/preview` serves what the viewer shows** and is **not audited** (§18): the WebP preview if there is one, then the playable copy of a video or audio file (unless `original=true`), the PDF of an Office document, otherwise the original bytes inline, all with range support. Markup (HTML, XML, SVG) is served as `text/plain`, with `nosniff` and a `sandbox` CSP, so a preview can never run in our origin. `GET /nodes/{id}/content` stays the audited download. Anyone who can view a file can read its bytes through the preview, so the audit log records downloads, not every way the bytes could leave.
- **"Download original" is always available** to anyone with viewer rights (except via no-download links), and returns exact bytes.
- Folders can be downloaded as a **streamed zip** (built on the fly, never staged on disk).

---

## 15. Clients

### 15.1 Monorepo (pnpm + Turborepo)

```
apps/web        Vite + React + TanStack Router/Query + Tailwind v4 (desktop-class file manager, responsive)
apps/mobile     Expo (dev builds, not Expo Go) + Expo Router + NativeWind
packages/api    TS client generated from the server OpenAPI spec (openapi-typescript / orval)
packages/core   shared hooks, formatting, upload queue logic, permission helpers
packages/tokens design tokens → Tailwind v4 theme CSS (web) + JS preset and theme variables (NativeWind)
packages/ui     web components (§16.4), Storybook and the clickable mockup (§16.5); web only
packages/i18n   sv + en strings (i18next), shared by web and mobile
```

The web and mobile apps share **logic and tokens, not UI components**. `packages/ui` is for the web app only. The web needs:
- drag-and-drop to and from the OS
- rubber-band and shift-click selection
- keyboard shortcuts
- context menus
- virtualized grids

These are better in React DOM than through React Native Web.

### 15.2 Web (desktop + mobile browser)

- **Layout:** a left nav rail, a top search bar and a content area. On mobile it collapses to a bottom nav.
  - The nav: My files, Shared with me, Recent, Starred, Trash, and a storage meter. The phone bottom nav has no room for Trash, so it is in the account menu there.
  - **Admin pages swap the nav, as in Immich.** Under `/admin` the rail (and the bottom nav on phones) lists the admin pages instead: **People** (`/admin/people`: everyone who has signed in, their usage and their quota), **Storage** (`/admin/storage`: disk usage and the default quota) and **Settings** (`/admin/settings`: search and text recognition, video and audio, Office documents, machine learning, virus scanning, trash and versions, sign-in). **Jobs** (`/admin/jobs`, §10.1) and **Tags** (`/admin/tags`: keyword rules, §13.4) sit between Storage and Settings, in that order. Server stats join the list when they're built. The rail's top button is "Back to files" where the New button usually sits; on phones it's a back arrow in the top bar. `/admin` opens People. A person's own settings (language for now) aren't admin pages, so they keep the normal nav.
- **Content area:** grid/list toggle, breadcrumbs, sorting, multi-select with a contextual action bar, right-click menu.
- **Search** (M3, §13.3): the search bar sits in the top bar on every page except admin and settings, with name suggestions as you type and "Narrow it down" chips (PDFs, Images, In this folder). `/search` shows the results; on phones the bottom nav's Search opens it and focuses the bar, which then has a back arrow. The admin page's "Search and text recognition" section has the text recognition switch, progress (files read, waiting, in progress, failed) and "Read all files again".
- **Starred and Recent** (decision 21):
  - **Stars are each person's own**, on files and folders. Star or unstar from the item menu, the selection's More menu, the details panel header and the viewer's menu; a snackbar offers Undo. Starred items show a small filled star after their name everywhere.
  - **Starred** lists everything the person starred, from any folder, sorted like a folder, with a **Location** column that opens the parent folder and a "Show in folder" action.
  - **Recent** lists files (not folders) by the person's own last activity, newest first, at most 100, with no sort controls: **opened** in the viewer, **downloaded**, **uploaded** (a new file or version) or **restored a version**. Renames and moves don't count. Columns: Location and "Your last activity" ("You opened today 11:47").
  - Recent is stored in `recent_files`, one row per person and file. It is **private to that person** and is **not the audit log**: the viewer reports an open with `POST /nodes/{id}/opened`, which is never audited (§18); downloads and uploads are noted by the server as they happen.
  - Trashed items (and everything inside a trashed folder) drop out of both pages. A star comes back on restore; deleting forever removes stars and recent rows with the node.
  - A file opened from Starred or Recent steps through that page's files in the viewer, and closing returns there.
- **Details panel:** info (with the file's tags, §13.4), sharing, versions, activity. Info goes on with the file's **Details** and **Text in this file**, which people can correct (§13.5), and ends with its **Processing** steps (§10.1, §16.4), folded to one line once every step is done. The panel's open state and tab are shared by the file list and the viewer: left open in the list, it is open when a file is opened and while stepping through files, and closing it in the viewer closes it in the list too. Back in the list, the file the viewer showed last is selected, so an open panel shows it. It lasts for the browser tab's session.
- **Upload tray** (bottom-right) with per-file progress, pause/resume and retry.
- **Live updates** (§15.4): changes from other tabs and devices, and thumbnails as they are made, show up without a refresh.
- Optionally a PWA, for "Add to home screen" on phones.

### 15.3 Mobile app (React Native)

Expo (SDK 58) with Expo Router and NativeWind on the shared tokens, in `apps/mobile`. It reuses `@dokument/api`, `@dokument/core` and `@dokument/i18n`, the web's query keys, and the same live notices. Development builds only, no Expo Go, since the scanner and share target need native code. The app id is `io.github.elmaxe.dokumentsystem` and the scheme `dokumentsystem`; CI builds a debug APK, and there is no store listing yet (decision 37).

- **Onboarding:**
  1. Enter the server's address. The app checks `GET /api/v1/server/info` names Dokumentsystem before going on. The address must be https, except in development builds.
  2. Sign in at the IdP in the system browser (an auth session, so the browser's sign-in is reused) with PKCE, back through the callback page (§5), then redeem the code at the token endpoint.
  3. Optionally add a **local address** in Settings → Addresses.
- **Main and local address (decision 37):** the app uses the local address whenever the server there proves it holds the app's session (§5, session proof), and the main address otherwise. It checks on start, when the app comes back to the front and when the network changes, with a 1.5 s timeout, starting on the main address so nothing waits. No Wi-Fi names, so no location permission. The local address may be plain http (iOS allows local networking, Android allows cleartext); the token is only sent there after a proof.
- **Places:** a navigation bar with My files, Shared with me, Search and Recent; Starred, Trash and Settings from the account sheet. Shared with me says "Sharing is coming" until M4. Lists show folders, then files as a grid or a list, sorted by name, modified or size, kept per phone. Thumbnails load with the session header and are cached on disk by version.
- **Viewers:** a tap opens a file full screen, a long press its actions. They show the same preview as the web (§14): the original, the WebP, the playable copy or the PDF of an Office document, with the same "preparing" and "making a copy" states.
  - Images natively with expo-image: pinch and double tap zoom.
  - PDFs with the web's pdf.js as an Expo DOM component, which can keep search marks later. The file is downloaded to the cache and handed over whole, so files over 50 MB open in another app instead. pdf.js parses in the web view's own thread, since its bundle has no worker file. Its wasm decoders, standard fonts and CMaps aren't bundled yet, so JBIG2, JPEG 2000 and CCITT scans and CJK text without embedded fonts may draw blank. Pinch and double tap zoom are done in the page itself, since Android's web view doesn't zoom a page unless switched on natively; pages are drawn again at the new size, up to 16 megapixels each.
  - Text and Markdown as a DOM component too, from the first megabyte (a range request). Markdown is GitHub's flavour without raw HTML, and images from the web are left out; links open in the browser.
  - Video and audio with expo-video and the system's controls.
  - "Open in another app" downloads the original to the cache and opens the share sheet.
- **Splash and fonts:** fonts are built into the app by expo-font's config plugin. On Android the splash screen is the platform's (expo-splash-screen) with an animated vector drawable as its icon, set by the app's own config plugin. For now that's Android's starter-kit animation, until the app has its own mark. The background is `surface` in light and dark.
- **Offline (decision 37):** no pinned files yet. The query cache (listings, Starred, Recent, the account) is kept on the phone for 7 days and shown read-only when offline, with a notice; thumbnails come from the image cache. Opening unseen files, uploads and changes wait for a connection.
- **File actions:** a file's or folder's sheet (its more button, or a long press on a folder) has Rename, Move and Move to trash for people with edit access (§9), under Open in another app, Split PDF, Select and the star. Rename and New folder ask in a sheet, with the name selected up to its extension. Move opens a full-screen folder picker that starts where the item is, lists folders only, can make a new folder, and asks first when the move would hide something from people who see it now (the move check). For now moves stay within the person's own files; moving inside someone else's shared folder comes with Shared with me in the app. Selecting takes folders too: a tap on a folder selects it while anything is selected, and the selection bar has Move and Move to trash next to Merge and Split. Every change shows a snackbar, with Undo for moves and the trash. The app skips live notices about its own changes, so each refetches what it changed itself.
- **New button:** the floating button in My files and every folder (hidden where the person can only view) opens a sheet with New folder and Scan a document; uploading files and photos joins it next.
- **Trash:** the person's trash as a list, with when each item goes ("Deleted forever in 2 days") or who moved it there; its sheet shows where it came from, in the warning colour when 3 days or fewer are left. A tap opens Restore and Delete forever, which asks first and says how many items inside go too; Empty trash asks with how much it frees.
- **Document scanner:** camera → edge detection and perspective correction → multi-page PDF → upload → server OCR. Uses VisionKit on iOS and ML Kit Document Scanner on Android, via `react-native-document-scanner-plugin`. **Built:** Scan a document in the New sheet opens the system's scanner, with its own cropping, filters and page list. The pages become one PDF on the phone with pdf-lib, a JPEG per page as the scanner made it, A4 wide and as tall as the page's proportions. A sheet asks for the name ("Scan 2026-11-02 21.13" by default) and uploads it to the folder with `keepBoth`, so a name already taken gets a number instead of a question. The PDF waits in the app's cache until it's uploaded, and the upload shows above the button with its progress, or the reason it failed and Try again. The button hides offline.
- **Share target:** "Share to Dokumentsystem" from other apps, with a folder picker.
- **Uploads (decision 37):** tus, like the web, with a small client of the app's own that reads the file from the phone's storage a chunk at a time (8 MB) and asks the server for its offset after a dropped connection. They run while the OS lets the app run; when it stops them they pause, and resume from the server's offset when the app is back, with a local notification. Native background transfer comes later.
- **Settings:** language (on the account, Same as device by default), appearance (Same as device / Light / Dark, on the phone), seconds in times (on the account), addresses, active devices with sign-out per device, versions. Admin pages stay in the browser: the account sheet has "Open admin pages in the browser" (decision 37).
- **Push notifications (later):** via **ntfy / UnifiedPush** to avoid depending on Google and Apple. The in-app notification center works without it.
- **Built so far (M5):** onboarding and sign-in, address switching with the proof, My files and folders, Starred, Recent, the account sheet with storage, Settings with addresses and active devices, Open in another app and stars, the offline cache, live updates, the viewers, the document scanner with its uploads, then file actions (new folder, rename, move, trash, selecting folders) and the Trash. The rest of M5 follows in this order: uploading files and photos with the share target, search, sharing (Shared with me, sharing with people, Activity), and viewer extras (the audio waveform, video seek previews, a file info sheet).

### 15.4 Live updates

Changes made in one tab or on one device show up in the person's other open clients without a refresh (decision 25).

- **Transport:** one SignalR hub at `/api/v1/live`, server to client only. The web client tries WebSockets, then Server-Sent Events, then long polling. It signs in with the session cookie and sends `X-CSRF-Token` on SignalR's own POSTs. The app sends its bearer token, which SignalR puts in the `access_token` query for WebSockets; the hub is the only place the server reads it there (§5). The app is connected only while it is in front, and refetches what's on screen when it comes back. Reverse proxies must pass WebSocket upgrades on `/api/v1/live`; without them the client falls back to SSE or long polling and still works.
- **Notices travel through Postgres.** Whatever changes data (API, purge, worker) runs `pg_notify('dokument_live', …)` on its own connection, inside its transaction, so a notice is sent on commit and dropped on rollback. The server `LISTEN`s on a dedicated connection and forwards each notice to the SignalR groups `user:{id}`. This also lets the worker's thumbnail jobs reach clients without a second channel, and works with several server instances.
- **Notices are thin:** a list of topics and an optional origin, never ids, names or sizes. Clients refetch through the normal API, so permission checks live in one place and nothing private travels on the socket.

  | Topic | Sent when | The web refetches |
  |---|---|---|
  | `files` | create, rename, move, upload, version restore or Keep forever, trash and restore, star, version purge, a thumbnail is ready, details or text edited | folder listings, file details, versions, the Details and Text sections, Starred, Recent |
  | `recent` | an open or download | Recent |
  | `trash` | trash, restore, delete forever, empty, trash purge | Trash |
  | `storage` | anything that changes usage, a quota or the default quota | the storage meter |
  | `account` | language or preferences, filing rules | the session, filing rules |
  | `filings` | a filing rule moved files (§13.6) | filing rules, and the "Filed" snackbar |
  | `shares` | a share granted, changed, removed or left (§9) | Shared with me, who has access, listings, search; and the "shared with you" snackbar |

- **Who hears what:** notices go to the owner of the changed items (and the person who made the change, when that's someone else). A `files` notice to someone also goes to everyone they share anything with: notices carry no ids, so that is simpler than working out who can see each changed path, and each client refetches only what it shows and may see. A change to the default quota goes to everyone.
- **Echo skip:** each tab makes a random client id and sends it as `X-Dokument-Client` on its mutations and tus requests. The server passes it on as the notice's origin (only if it is at most 64 letters, digits and dashes), and the tab that made the change skips that notice, since it has already updated itself.
- **Reconnects:** the client retries for as long as it is open (at once, 2 s, 5 s, 10 s, then every 30 s), and refetches everything after any gap, since notices aren't queued. If the server loses its `LISTEN` connection, it reconnects with backoff and then tells every client to refetch everything.
- **In the web app:**
  - New items in the open folder are highlighted for 2 s (a tonal fill with an outline that fades). Removed items simply go.
  - Thumbnails swap in when the worker finishes. The listing poll (§16.4) only runs while the live connection is down.
  - The viewer never swaps the file being read. If a newer version arrives, a banner offers "Show newest"; if the file is trashed or deleted, a banner says so and offers "Open trash".
  - A folder that is trashed or deleted while open shows an empty state with "Open trash" and "Go to My files".
  - After 30 s without a connection, a banner says changes from other devices will show up when it's back.
  - When a session is ended from another device, or signed out in another tab, its clients get `signedOut` and go to the sign-in page ("You were signed out because this session was ended"). The server closes those connections 2 s later.
- **Out of scope for now:** live progress of other devices' uploads, presence ("who else is looking") and the notification center (§20).

---

## 16. Design system

**Look and feel:** Material 3–inspired, close to Immich and Google Photos, with one fjord-blue accent. The chrome is calm and neutral, so people's thumbnails and the coloured file types carry the colour. **Dark mode is first-class**: it is designed, not inverted.

The full system, with live component previews in both themes, is the **Dokumentsystem Design System** artifact (https://claude.ai/artifact/QoaFs7759JbKMfxdQJNzDM). Its tokens are checked in at [`docs/design-system/tokens.json`](design-system/tokens.json), which seeds `packages/tokens`.

### 16.1 Principles

- **Files first.** Chrome uses neutral surfaces. Colour belongs to thumbnails, file-type icons and the single accent.
- **State is said in words.** Upload status, quota and retention warnings pair a colour with an icon and a sentence ("Removed in 3 days", "Almost full").
- **Nothing is lost silently.** Destructive actions confirm, and failed uploads stay in the tray until dismissed.
- **Same system everywhere.** Web (desktop and phone) and the React Native app share tokens, wording and behaviour, but not component code (§15.1).

### 16.2 Tokens (`packages/tokens`)

One JSON source, `docs/design-system/tokens.json`, is read at build time by `packages/tokens`. It generates the Tailwind v4 theme for the web (CSS variables per theme, switched by `data-theme` and `prefers-color-scheme`) and the NativeWind theme for mobile (the same names, switched by `useColorScheme`). Components use token names only, never raw values.

**Colour roles** (Material 3 names, light / dark):

| Role | Light | Dark | Use |
|---|---|---|---|
| `surface` | `#f6f8f9` | `#0e1417` | Page background |
| `surface-container-lowest` | `#ffffff` | `#090f12` | File tiles, list rows, inputs |
| `surface-container-low` | `#eef2f4` | `#161d20` | Nav, top bar, details panel |
| `surface-container` | `#e8edf0` | `#1a2124` | Menus, upload tray, bottom nav |
| `surface-container-high` / `-highest` | `#e1e7ea` / `#d9e0e4` | `#242b2f` / `#2f363a` | Dialogs and sheets / pressed fills, meter track |
| `on-surface` / `on-surface-variant` | `#161c1f` / `#3f484d` | `#dde3e7` / `#bec8cd` | Text / metadata and inactive icons |
| `outline` / `outline-variant` | `#6c767c` / `#bfc8cd` | `#899297` / `#3f484d` | Input borders / dividers and tile edges |
| `primary` / `on-primary` | `#006781` / `#ffffff` | `#64d2f7` / `#003545` | The one accent: main button, focus ring, links, progress |
| `primary-container` | `#bfe9ff` | `#004e62` | Selected files, drop target |
| `secondary-container` | `#d3e5ee` | `#33474f` | Active nav item, selected chips, tonal buttons |
| `error`, `success`, `warning` (+ `-container`) | `#b3261e`, `#1d6b3c`, `#795900` | `#ffb4ab`, `#86d9a0`, `#f2c14c` | Status, always with an icon and words |
| `file-pdf` `-doc` `-sheet` `-slides` `-image` `-video` `-audio` `-archive` `-text` `-folder` | red, blue, green, orange, magenta, violet, teal, brown, grey, grey | lighter tints | File-type icons only |
| `code-keyword` `-string` `-number` `-comment` `-function` `-type` | `#7b3fb8`, `#1f6e3a`, `#9a4d00`, `#5f6b72`, `#00629b`, `#8b3d6a` | `#d0a8ff`, `#8fd6a0`, `#ffb877`, `#93a1a9`, `#7cc7f2`, `#f4a6cf` | Syntax colours in the text viewer and Markdown code blocks (§14). All 4.5:1 on `surface-container-lowest` and `surface-container` |

Every text pair meets WCAG AA (4.5:1) in both themes. Icons, borders and the focus ring meet 3:1.

**Typography:** **Albert Sans** (SIL OFL) for all UI text, with the latin-ext subset for å, ä and ö. **IBM Plex Mono** only for checksums, keys, job ids and the text viewer. The same font files are built into the app by `expo-font`'s config plugin, so nothing loads at startup (the web build still loads them).

| Style | Size / line | Weight | Use |
|---|---|---|---|
| `headline-md` / `headline-sm` | 28/36, 24/32 | 500 | Page title on desktop / phone |
| `title-lg` / `title-md` / `title-sm` | 20/28, 16/24, 14/20 | 600 | Dialog titles / panel sections / group headings |
| `body-lg` / `body-md` / `body-sm` | 16/24, 14/20, 12/16 | 400 | Phone default / desktop default / metadata |
| `label-lg` / `label-md` / `label-sm` | 14/20, 12/16, 11/16 | 600 | Buttons / chips and nav / uppercase tags |
| `mono-sm` | 12/16 | 400 | Hashes and keys |

Sizes, dates and counts use tabular numbers.

**Spacing:** 4 px grid (`space-1` = 4 … `space-12` = 48). The page gutter is 24 px on desktop and 16 px on phones.
**Radii:** `xs` 4 (checkbox, badges), `sm` 8 (chips, inputs, menus, thumbnails), `md` 12 (tiles, upload tray), `lg` 16 (dialogs, sheets), `xl` 28 (search bar, nav pill), `full` (buttons, avatars, progress).
**Elevation:** mostly tonal (the `surface-container` steps). Three shadows are reserved for things that float: hovered tiles, menus and the upload tray, and dialogs.
**Sizes:** 24/20 px icons, 48 px minimum touch target, 40 px controls and dense rows, 176 px minimum tile width, 360 px details panel.
**Motion:** 100 / 200 / 300 ms durations, with the Material emphasized easing. With `prefers-reduced-motion`, movement is replaced by instant changes.

### 16.3 Layout and platforms

| Width | Navigation | Content | Overlays |
|---|---|---|---|
| < 600 px (phone, and the RN app) | Bottom nav: My files, Shared with me, Search, Recent | Two-column grid or two-line list; floating action bar | Bottom sheets; upload FAB |
| 600–839 px | Bottom nav | Wider grid | Dialogs |
| 840–1199 px | Collapsed nav rail (88 px) | Grid / list | Dialogs |
| ≥ 1200 px | Expanded nav (256 px) with storage meter | Grid / list + details panel (360 px) | Dialogs; upload tray bottom-right |

On admin pages the same rail and bottom nav hold the admin pages instead (§15.2), with no storage meter.

Touch interactions replace the desktop ones: long-press instead of right-click and rubber-band selection, and tap to open instead of double-click.

### 16.4 Core components

Web components are Radix primitives styled in the shadcn/ui pattern (we own the code). The artifact's previews are the visual reference.

- **Button** (filled / tonal / outlined / text / danger) and **IconButton**. At most one filled button per view.
- **Chip:** search filters that mirror the operators ("Type: PDF" for `type:pdf`).
- **TagChip** (outlined for tags people add; tinted in `secondary-container` with a `bolt` icon for rule tags and `auto_awesome` for ML tags, a tooltip naming the rule, an optional remove button; a small variant for list rows and results), **TagEditor** (the Info tab's Tags section with **TagInput**, a combobox of suggestions with counts and "Create tag") and **TagsDialog** (tags for a selection: on all, on some with + and ×, applied on Save).
- **SearchBar** with **SearchSuggestions** (a combobox: "Search for …" first, then up to two matching tags, then names with their location, arrow keys and Enter), **SearchResult** (thumbnail, name with marked hits, the snippet, then location · date · page · "Found in a scan" badge) and **Highlighted** (text with `<mark>` from offsets or terms, never parsed as markup).
- **FileTypeIcon:** a Material Symbol in the `file-*` colour per MIME group. Also the thumbnail fallback. Folders stay neutral.
- **FileGrid:** folders first, then file tiles with 4:3 thumbnails. Documents (PDF, Office, text) show the top of page 1; photos are cropped to fill. A shimmer shows while a thumbnail is pending, and the thumbnail is swapped in when it is ready, through a live update (§15.4). While the live connection is down, the listing is polled instead (every 2 s, then every 10 s) as long as anything is pending. Video and audio tiles show a duration badge; audio without cover art shows its waveform (§14).
- **FileList:** a sortable name / owner / modified / size table. Under 600 px of container width, the columns fold into a second line. Pages can swap the columns after the name: the Trash page shows original location / deleted / size.
  - A file with a ready thumbnail shows it in place of the type icon: 32 px, 40 px on phones, with pages showing their top as in the grid. Folders and files without a preview keep the icon, in a box of the same size so names line up; a pending thumbnail shimmers.
  - **Folders show their size:** the current version of every live file below them, at any depth. Old versions and trashed files don't count, so it isn't the same number as quota usage (§17). The listing API returns it as the folder's `size` plus `contents` (file and folder counts) from one ltree query per listing. An empty folder shows "—", and hovering a folder's size shows what it holds ("1,204 files, 38 folders").
  - Hovering a date shows the full timestamp ("23 October 2026, 17:40").
- **Processing indicators** (decision 27): while the worker has steps waiting or running for a file, its grid tile shows a "Processing" pill on the thumbnail and its list row a small spinner after the name, both with a tooltip naming the steps and their state ("Processing: Thumbnail: running, Searchable text: waiting"). Failed steps don't keep the badge; they show in the details panel and on the admin Jobs page. The details panel's **Processing** section folds to "Processing · all done" once every step is settled, and otherwise lists Thumbnail, Metadata and Searchable text with an icon and words for each state, notes that search can't find words inside the file until its text is read, and gives admins the error and **Try again** on a failed step. Only a finished thumbnail sends a live notice (§15.4), so while metadata or text steps are pending the listing and the section poll every 2 s (every 10 s after a minute); a pending thumbnail is polled for only without a live connection.
- **ActionBar:** the selection count plus Share, Download, Move, Trash and More.
- **UploadTray:** one row per tus upload, in one of the states hashing, uploading, paused, done, duplicate ("Remove this copy") or failed (Retry), each with an icon and words.
- **DuplicateDialog:** the §8.3 prompt. A batch offers Skip duplicates / Upload all / Review; a single file offers Skip / Upload anyway / Open existing.
- **ShareDialog:** add people, then people with access (inherited grants are labelled and cannot be lowered), then the public link with expiry, password and download chips. A bottom sheet on phones.
- **VersionHistory:** the Versions tab of the details panel. It marks the current and "Keep forever" versions and warns "Removed in N days" per the retention policy.
- **StorageMeter:** a warning at 80 % and a critical state at 95 %, in words as well as colour.
- **FileViewer:** the §14 viewer: a header with the file's name, Share, Download, details and More; the preview; a floating toolbar for pages and zoom; previous/next arrows on wide screens and swipes on phones. States: loading, preparing (the worker is still making the preview), no preview, and failed, each with Download. Opened from search it takes the hits to mark (§14) and shows "Result 3 of 23".
- **NavRail**, **BottomNav**, **EmptyState** (also the drag-and-drop target that names the destination folder).

### 16.5 Content and accessibility

- Copy is in sv and en from day one. Swedish runs about 30 % longer, so never size a control to fit English text.
- **Copy doesn't assume a setup.** Dokumentsystem is open source and runs on whatever people have, so the UI never says "the NAS", "your household" or names a particular machine or provider. Say "this server", "your OIDC provider" or "a more powerful computer" instead. Product names appear only as examples ("such as Authentik or Keycloak") or when they come from config (`OIDC_PROVIDER_NAME`).
- Copy is in sentence case, with no exclamation marks and no emoji. Buttons are verbs ("Copy link"), and toasts confirm in the past tense ("Link copied"). Errors say what happened and what to do next.
- **Icons:** Material Symbols Rounded (Apache-2.0), outlined by default and filled when active or selected.
- Keyboard navigation everywhere: arrow keys in the grid, Space to select, Enter to open, Delete to trash. Focus is shown by a visible 2 px focus ring. Every icon button has a label.
- **Clickable mockup:** `packages/ui` holds the components in Storybook (`pnpm --filter @dokument/ui dev`), with toolbar switches for light/dark, sv/en and phone to desktop widths. The **Screens** stories are one app on fake data: the file browser, viewer, search results and admin settings, with selection, sharing, uploads and the duplicate prompt working locally.
- Dates in lists come from `formatFileDate` in `packages/core`: "Today 09:14", "Yesterday", "12 Mar", then "21 Jun 2025". English uses British day-month order and a 24-hour clock. Tooltips and the details panel use `formatFullDate` ("23 October 2026, 17:40").
- **Settings page** (`/settings`, "Settings" in the account menu): the person's own settings, with the normal nav, saved as soon as they change. Sections so far: Language (the same choice as the account menu's quick switch), Appearance, Filing rules (§13.6) and Date and time.
- **Appearance** (decision 36) picks the theme: Same as device (the default, following `prefers-color-scheme`, with a "Now light/dark" hint), Light or Dark. Unlike the other settings it is saved per device, in `localStorage` (`dokument.theme`), because the right theme depends on the screen and the room, and a phone and a computer can differ. A small inline script in `index.html` sets `data-theme` on `<html>` before first paint, so the page never flashes the wrong theme; the token CSS (§16.2) already lets `data-theme` override the media query. Other open tabs follow a change through the `storage` event. The mobile app will keep its own choice on the phone.
- **Show seconds in times** is a per-person switch on the Settings page, off by default and saved on the account (`users.show_seconds`, `PUT /api/v1/me/preferences`) so it follows them across devices. When it is on, every time shown gets seconds ("Today 09:14:07", "23 October 2026, 17:40:12"). Components read it through `useFormat()` in `packages/ui`.
- **Sizes are binary everywhere** (decision 19): 1 KiB = 1024 B, labelled B, KiB, MiB, GiB and TiB, through `formatBytes` in `packages/core`. Inputs use the same units: quotas are entered in GiB. There is no setting, so every screen, the API docs and the admin pages agree.

---

## 17. Quotas

- **Admin settings:** a default quota (no limit until an admin sets one), and per person either the default, a limit of their own, or no limit. Changing someone's quota writes a `user.quota_change` audit event. The default applies at reservation time, so lowering it never deletes anything; it only stops new uploads.
- **Counted:** originals of all versions owned by the user, including trash, with blobs deduplicated per user. **Not counted:** renditions and thumbnails.
- **Enforcement:** reserve at upload start and settle at completion (§8). `used_bytes` is updated in the same transaction. A nightly reconciliation job recomputes it and logs any drift.
- **UX:** a storage meter in the nav, which also says how much of the storage emptying the trash would free ("1.4 GiB of it in the trash"), a breakdown page (by type, largest files, trash, old versions), and email plus in-app warnings at 80% and 95%.
- **Admin view:** per-user usage, total disk usage, and the free space on the volume. Built on the admin Storage and People pages: the size of all originals (each blob once), and total and free space for the volume holding originals (plus the derived volume when it is separate).

### 17.1 Quota from an OIDC claim

People who are managed in an IdP can get their storage from it instead of from the admin page.

- **Configuration:** `OIDC_QUOTA_CLAIM` names the claim, read from the ID token or userinfo like the groups claim (§5). It is a bootstrap setting like the other `OIDC_*` variables (§4.3). Unset means no claim is read and nothing changes. The admin page's Sign-in section shows which claim is read.
- **Values** (a string or a JSON number):
  - a byte count: `53687091200`
  - a size: `50GB`, `50 GiB`, `1.5TB`, `500m`, case-insensitive, with the units B, K, M, G and T, each optionally followed by B or iB
  - `unlimited`
- **Sizes are binary**, like everywhere in the UI: `GB` and `GiB` both mean 1024³ bytes, so `50GB` shows as exactly 50 GiB on the People page. IdP admins type `GB` far more often than `GiB`, and reading it as 10⁹ would show an odd 46.6 GiB.
- **A value that can't be read** (zero, negative, fractional bytes, an unknown unit, or anything else) is ignored and logged as a warning. The login goes ahead and the person keeps the quota they had.
- **Applied on every login,** like the admin-group mapping (§5). `users.quota_source` records whether the quota came from an admin or the claim. A change writes a `user.quota_change` audit event with the actor kind `system` and `"source": "claim"` in its details; changes made on the admin page carry `"source": "admin"`. A login that changes nothing writes nothing.
- **The claim wins** (decided 2026-10-24). While a person's quota comes from the claim, the People page says "Set by {provider}" (or "Set by your sign-in provider" without `OIDC_PROVIDER_NAME`) and the edit dialog is read-only with a note to change it at the IdP. The API refuses the change too.
- **When the claim goes away:** if the IdP stops sending it for someone, they fall back to the default quota at their next login and admins can set a limit again. If `OIDC_QUOTA_CLAIM` itself is removed, nothing changes at login, but the People page treats those quotas as the admin's again, so they can be edited.
- **Dev setup:** the Aspire AppHost sets `OIDC_QUOTA_CLAIM=dokument_quota`, and the dev Keycloak gives bob `50GB` through a user attribute mapped into userinfo.

---

## 18. Audit log

- **Append-only `audit_events`**, written **in the same transaction** as the change itself.
- **Events:**
  - auth: login, logout, session revoked, API key created
  - files: upload, new version, restore version, rename, move, download, trash (`node.trash`), restore (`node.restore`), delete forever (`node.delete_forever`), empty trash (`trash.empty`), tags added or removed (`file.tags_change`, naming the tags), filing rules added, changed, reordered or deleted (`filing_rule.change`), PDFs merged or split (`file.pdf_merge`, `file.pdf_split`, on each new file, naming the sources)
  - system: the purge removing expired trash (`node.purge`) and old versions (`version.purge`), and filing rules moving files (`node.move`, `folder.create`, naming the rule, §13.6), with the actor kind `system`
  - **Not logged:** opening a preview or viewing a file. It's too noisy for its value. Downloads of the original *are* logged. (Each person's Recent page remembers their own opens in `recent_files`, which nobody else sees and which isn't part of the audit log, §15.2.)
  - sharing: share granted, changed, revoked or left (`share.grant`, `share.change`, `share.revoke`, `share.leave`, naming the person and role); later link created/accessed/revoked
  - admin: settings changed, quota changed, keyword rule added, changed or deleted (`admin.tag_rule`), user disabled
- **Partitioned by month** (from M6; M1 ships a plain table with indexes on `at` and `(target_node_id, at)`). An admin setting controls retention (default 1 year).
- **Views:**
  - the details panel's **Activity** tab (built, decision 39): what happened to a file, or to a folder and everything in it, newest first, 30 at a time with "Show older" (`GET /nodes/{id}/activity`). Everyone who can see the item sees its changes: uploads, versions, renames, moves (naming folders only when the person can see them), tags, details and text edits, trash and restore, shares. Downloads show only to the owner, and to each person for their own. Admin, account and purge events stay out of it.
  - admins see everything, with filters and CSV export
- The security audit log and the user-facing activity feed draw on the same table but use different views and filters.

---

## 19. Backups

- **A scheduled DB dump** run by the worker with `pg_dump -Fc` (the client version matches our Postgres image), into `/data/backups`.
  - Admin settings: cron schedule (default nightly) and number of dumps to keep (default 14).
  - Failures notify admins.
- **Documented 3-2-1 guidance:** a DB dump on its own is **not** a backup.
  - Back up `/data/originals` + `/data/backups` together. Ideally use ZFS/Btrfs snapshots plus an off-site copy (e.g. restic, rclone, Hyper Backup).
  - `/data/derived` can be skipped.
  - Order: dump the DB first, then snapshot the files. Orphan blobs after a restore are harmless (the integrity job cleans them up); missing blobs are not.
- **Restore procedure** documented and tested in CI: restore the dump, start, run an integrity check.
- **If the DB is lost with no usable dump,** the newest name manifest plus `/data/originals/blobs` still give back every file with its name and folder (§7.1).

---

## 20. Notifications

- **SMTP** via MailKit. Admins configure host, port, TLS, user, password and from-address, and can send a test email.
- **In-app notification center** (live, over the §15.4 connection).
- **Events:**
  - Shared with you
  - Quota warning (80% / 95%)
  - Trash purge upcoming
  - Admins only: job failures above a threshold, backup failed, disk space low, integrity problems
- **Per-user preferences:** a matrix of event × channel (email, in-app).
- Templates are localized (sv / en), with a plain-text alternative.
- **Digest mode:** instead of one email per share event, send a daily digest. This avoids spam during bulk sharing.

---

## 21. Security

- **TLS** terminates at the reverse proxy. `PUBLIC_URL` must be set for correct redirects and links. Trusted-proxy configuration for forwarded headers.
- **User content is never rendered in our origin.**
  - Downloads get `Content-Disposition: attachment`, `X-Content-Type-Options: nosniff`, and a strict CSP sandbox.
  - SVG and HTML are previewed only as rasterized or plain text.
- **Rate limiting** on auth endpoints and public links. Public-link passwords are hashed with Argon2id.
- **Authorization** is checked in the application layer for every operation, and search reuses the same predicate. Integration tests cover the permission matrix.
- **Secrets** (SMTP password, ML API keys) are encrypted at rest in the DB using ASP.NET Data Protection keys stored in `/data/originals/keys`, so they are backed up with the originals. The worker mounts originals read-only and can decrypt with the same keys. Losing the keys only loses these secrets; an admin enters them again.
- **Dependency hygiene:** Renovate, image scanning (Trivy) in CI, pinned base images.
- **Virus scanning: not in v1.** On a household NAS the risk doesn't justify ~3–4 GB RAM for ClamAV.
  - The pipeline has a `scan` stage hook. Later it can call an optional **ClamAV** container (`clamav/clamav`, the official image) over clamd's `INSTREAM` protocol.
  - The admin page already stores the clamd address (`tcp://host:port`, port 3310 by default) and tests it with `zVERSION`, so the scan stage only has to read it.
  - Infected files would be quarantined and blocked from download.

---

## 22. Observability and operations

- Structured logging (Serilog → console/JSON). Log level can be changed from the admin UI.
- OpenTelemetry traces, metrics and logs, exported over OTLP when configured (§22.1).
- **Admin "System" page:** versions, extension versions, disk usage, queue stats, ML endpoint status, last backup, integrity report.
- **CLI inside the container (`dokument …`):** `admin`, `export`, `integrity-check`, `reindex`, `regenerate`, `migrate`.

### 22.1 OpenTelemetry

Telemetry is **off by default** and costs nothing until it is turned on. A household install doesn't need a collector, and nothing leaves the box unless the admin points it somewhere.

- **Turning it on uses the standard SDK variables, not our own:** setting `OTEL_EXPORTER_OTLP_ENDPOINT` enables export of all three signals. `OTEL_EXPORTER_OTLP_PROTOCOL` (default `grpc`, or `http/protobuf`), `OTEL_EXPORTER_OTLP_HEADERS`, the per-signal `OTEL_EXPORTER_OTLP_{TRACES,METRICS,LOGS}_ENDPOINT`, `OTEL_RESOURCE_ATTRIBUTES` and `OTEL_TRACES_SAMPLER`/`_ARG` behave as the spec says. These are bootstrap settings (§4.3), not admin settings, because they must be known before the database is.
- **Any OTLP backend works:** an OpenTelemetry Collector, Grafana Alloy/LGTM, SigNoz, Jaeger, Uptrace, or the standalone Aspire dashboard. We ship no collector in Compose; the docs show one optional example.
- **On in development, off in production:** the Aspire AppHost injects `OTEL_EXPORTER_OTLP_ENDPOINT` (plus its protocol, API-key header, `OTEL_SERVICE_NAME` and instance id) into the server and worker, so traces, metrics and logs show up in the Aspire dashboard with no extra setup. Compose and the image set none of these, so a NAS install exports nothing until the admin sets the endpoint.
- **Each signal is gated separately:** traces and metrics turn on when the base endpoint or their own `OTEL_EXPORTER_OTLP_{TRACES,METRICS}_ENDPOINT` is set, and metrics also when `METRICS_PORT` is; with none set, no exporter is registered at all.
- **One image, one wiring:** `Dokument.Host` registers OpenTelemetry once for both roles. Resource attributes:
  - `service.name` = `dokumentsystem-server` or `dokumentsystem-worker` (`dokumentsystem` when one process runs both), unless `OTEL_SERVICE_NAME` overrides it (Aspire sets it to the resource name, `server` or `worker`)
  - `service.version` = the image version, `service.instance.id` = the host name; both yield to `OTEL_RESOURCE_ATTRIBUTES`, which Aspire uses to match telemetry to its resources
  - `deployment.environment.name` only if the admin sets it in `OTEL_RESOURCE_ATTRIBUTES`
- **Traces:**
  - Instrumentation: ASP.NET Core (with route templates, so ids and share tokens stay out of span names; `url.path` and `url.query` are dropped from server spans), HttpClient (OIDC, Gotenberg, ML), and Npgsql for SQL.
  - SQL is traced only inside an existing trace, and connection opens are not traced, so the worker's queue polling and other background loops don't each start a trace of their own.
  - Our own `ActivitySource("Dokument")` spans:
    - uploads: `upload create`, `upload append`, `upload finish` and `upload hash`, with `dokument.upload.id`, the size and, once done, `dokument.node.id`
    - the job runner: one `job <type>` span per run with `job.type`, `job.id`, `job.attempt` and `job.outcome` (`succeeded`, `failed`, `timed_out`, `released`)
    - rendering: `thumbnail render` with the blob size and preview status
    - text extraction and OCR run inside the `job text` and `job metadata` spans; no file names or text are recorded
  - Server spans carry `dokument.user.id` for signed-in requests.
  - **Jobs carry the trace across the queue.** Enqueueing stores the current `traceparent` on the `jobs` row (`trace_parent`, nullable), and the job's span links to it. An upload's `upload finish` span then leads to its thumbnail job, even though the worker runs it later. The job is its own trace, not a child, so a slow queue doesn't stretch the upload's trace.
  - Calls to the ML service and Gotenberg propagate W3C `traceparent`. The ML service uses the Python OpenTelemetry SDK with the same `OTEL_*` variables.
  - `/health/*` and `/metrics` are not traced. Sampling is parent-based, default always-on, and can be lowered with `OTEL_TRACES_SAMPLER=parentbased_traceidratio`.
- **Metrics:**
  - ASP.NET Core and Kestrel, HttpClient, Npgsql, .NET runtime and process metrics.
  - Our own `Meter("Dokument")`, named `dokument.*`:
    - `dokument.jobs.queued` / `dokument.jobs.running` (gauges by `job.type`), `dokument.jobs.duration` (histogram by type and outcome), `dokument.jobs.failed` (counter), `dokument.jobs.lag` (age of the oldest due job)
    - `dokument.uploads.bytes` (counter), `dokument.uploads.active` (gauge), `dokument.downloads` (counter)
    - `dokument.live.connections` (open live connections on this server) and `dokument.live.notices` (counter of notices forwarded)
    - `dokument.renditions.generated` (counter by `dokument.rendition.kind`), `dokument.storage.bytes` (by `dokument.storage.kind`: originals and derived)
    - The gauges (job counts and lag, active uploads, storage) describe the whole install, so the worker samples them from Postgres every 30 s and walks the derived volume every 10 minutes, only while metrics are exported. With several workers each reports the same values under its own `service.instance.id`, so dashboards should take the max.
    - `dokument.search.duration` (histogram, seconds, no labels) and `dokument.ocr.pages` (counter)
    - later: ML endpoint up/down
  - **Prometheus scraping** is optional for people who run Prometheus without a collector: `METRICS_PORT` turns on a `/metrics` endpoint on its own port, on every interface, so it is never reachable through the public port or reverse proxy. It is the OpenTelemetry Prometheus exporter, which is still a beta package. Don't publish that port to the internet.
- **Logs:** Serilog stays the logging API. When OTLP is on, the Serilog OpenTelemetry sink exports the same structured events with their trace and span ids, next to the console output. We resolve its endpoint, protocol and headers from the same `OTEL_*` variables as the SDK (including `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT`) and give it the same resource, so logs line up with traces and metrics. The admin UI's log level applies to both.
- **Privacy rules for every signal:**
  - No file or folder names, paths, file contents, search queries, email addresses, display names, IP addresses, tokens, cookies or `Authorization` headers.
  - URLs are recorded as route templates without query strings.
  - Users and nodes appear only as their opaque ids (`dokument.user.id`, `dokument.node.id`), and only on spans, never as metric labels, which stay low-cardinality.
  - The OTLP endpoint is the admin's choice; we never send telemetry anywhere by default.
- **Built (v0.10):** everything in this section that has something to instrument. Propagation to the ML service and Gotenberg comes for free with the HttpClient instrumentation when those clients exist; the ML service's own Python SDK setup and the ML up/down gauge come with those features. Search and OCR metrics came with M3.
- **Web app:** no browser telemetry in v1. The server's spans start at the API request. Browser tracing (OTLP over HTTP, relayed by the server) can come later if it proves useful.

---

## 23. Internationalization

- **sv and en from day one:** UI, emails and date/number formats.
- **Each person picks their language** in the account menu or on the Settings page (§16.5): Same as browser (the default), Svenska or English. The choice is saved on the account (`users.locale`), so it follows them across devices and emails can use it. The sign-in page, before anyone is known, follows the browser. Whether times show seconds is saved the same way (§16.5).
- OCR languages are configurable (default `swe+eng`). The search stemmer is chosen per detected page language.

---

## 24. Roadmap

| Milestone | Scope |
|---|---|
| **M0: Foundations** | Repo/monorepo setup, CI, Postgres image, .NET solution, Aspire, OIDC login + sessions, design tokens, clickable mockup |
| **M1: Drive core** | Folders, tus uploads, downloads, rename/move, versioning, trash, quotas, audit, web file browser. **Built so far:** folders, rename/move, tus uploads with the duplicate check, audited downloads, versions (restore, Keep forever), quota reservation, file audit events, the web file browser, the trash with the hourly purge that also enforces version retention, Starred and Recent, folder uploads and live updates. |
| **M2: Previews** | Worker + job queue, metadata, thumbnails (images/PDF/video), viewers (image/PDF/video/audio/text), Gotenberg Office previews. **Built so far:** the job queue, image and PDF thumbnails with the backfill, the unaudited preview endpoint, the web viewer for images, PDFs, text and browser-playable media, the admin Jobs page with per-file processing status (decision 27), the CodeMirror 6 text viewer and formatted Markdown (decision 31), video posters, audio cover art and playable copies with the admin's Video and audio section (decision 32), and Office previews through Gotenberg (decision 33). M2 is complete; PDF page thumbnails and free search inside a document in the viewer come later. |
| **M3: Search** | Text extraction, Tesseract OCR, pg_search BM25 with Swedish/English, search UI with snippets. **Built:** the metadata stage (exiftool), text from PDFs, Office/ODF and plain text, Tesseract for scans and photos with an admin switch, BM25 over names and text, the search bar with suggestions, the search page with filters, and hits marked in the viewer (decision 26). Then details and text in the file info, with corrections search uses (§13.6, decision 28). Tags with keyword rules came next (§13.4, decision 29). Then filing rules that move tagged files (§13.6, decision 30). |
| **M4: Sharing** | User shares, public links, Shared with me, SMTP + in-app notifications. **Built so far:** sharing with people, Shared with me, role-aware menus, editors adding to the owner's storage and trash, the move warning, search operators and live updates (decision 39). Public links and notifications are next. |
| **M5: Mobile** | Expo app: browse, view, upload, search, share target, document scanner, LAN/WAN switching. **Built so far:** the app's foundation (§15.3): sign-in through the token endpoint and callback page, main and local addresses with the session proof, My files, Starred, Recent, Settings, active devices, the offline cache and live updates (decision 37); the viewers, the document scanner, PDF merge and split, then file actions and the Trash. Next: uploads with the share target, search, sharing, viewer extras. |
| **M6: Ops** | DB dump schedule, integrity job, admin system page, export job and CLI with name manifests (§7.1), docs, multi-arch release. OpenTelemetry (§22.1): the base wiring (OTLP export, standard instrumentation, Serilog sink) can land any time before this; each milestone adds its own spans and `dokument.*` metrics, and M6 adds the `/metrics` port and the docs' collector example. |
| **M7: ML** | ML service (OCR+, embeddings, hybrid search, ML tags), remote ML URLs with fallback. Transcription of audio and video into searchable, timed text is pulled ahead as the ML image's first piece, right after the M2 video work (§11.1, decision 34) |
| **Later** | WebDAV, offline files, push via ntfy/UnifiedPush, groups, optional ClamAV, file-request links (public upload folders), merging Office files and photos into PDFs (§8.4) |

---

## 25. Decisions log

| # | Question | Decision |
|---|---|---|
| 1 | Product name | **Dokumentsystem** |
| 2 | Version retention defaults | Same as Google Drive: non-current versions are kept **30 days or up to 100 versions**, whichever comes first. Admins can change this, and users can mark a version "Keep forever" (§8.2). |
| 3 | Audit preview opens? | **No.** Downloads are audited, previews are not (§18). |
| 4 | Duplicate uploads | **Tell the user**: a pre-upload check with Skip / Upload anyway / Open existing, limited to files the user can access (§8.3) |
| 5 | Public links with upload ("file requests") | **Later** (roadmap) |
| 6 | Storage layout | App-owned, content-addressed, one folder per person (§7, decision 23); readable copies come from exports (decision 22) |
| 7 | Sharing model | Personal folders shared to users; no team drives (§9) |
| 8 | Virus scanning | Not in v1; hook for optional ClamAV (§21) |
| 9 | WebDAV | Later |
| 10 | Design system | **Approved** 2026-10-21: Material 3 roles, fjord-blue accent, Albert Sans, Material Symbols Rounded, 4 px grid (§16) |
| 11 | Monorepo scaffold | 2026-10-21: .NET 10 LTS, Aspire 13, Postgres 17, Node 22 + pnpm + Turborepo, Tailwind v4 on web, AGPL-3.0 `LICENSE` (§3.2, §4.1, §15.1) |
| 12 | Sessions | 2026-10-22: opaque tokens, stored hashed, expire after 30 days idle (sliding). Cookie + `X-CSRF-Token` on web, bearer on mobile. Sign-out is local, not at the IdP (§5). 2026-11-07: replaced by decision 44. |
| 13 | UI components | 2026-10-22: web components live in `packages/ui` (Radix + Tailwind v4, Storybook 10). The mobile app gets its own components on the same tokens (§15.1, §16.5) |
| 14 | File storage (M1) | 2026-10-22: our own tus 1.0 endpoint; the server hashes the finished temp file once; names are `NodeName` (NFC, case-insensitive, 255 bytes); owner-only access until sharing (M4) behind one `NodeAccess` predicate; no quota until an admin sets one; audit table unpartitioned until M6 (§6–8, §18) |
| 15 | Thumbnails and previews (M2) | 2026-10-23: our own Postgres job table; renditions keyed by blob hash; WebP thumbs at 256 and 640 px plus a 2048 px preview only when the browser can't show the original; HEIC via the image's system libvips; the grid polls until SignalR; the viewer reads an unaudited `/preview` endpoint (§6, §7, §10, §14) |
| 16 | Observability | 2026-10-23: OpenTelemetry for traces, metrics and logs, off until `OTEL_EXPORTER_OTLP_ENDPOINT` is set; standard `OTEL_*` variables only; trace context stored on jobs; strict no-PII attribute rules; optional Prometheus endpoint on a separate port. On by default under the Aspire AppHost in development. Prometheus on `METRICS_PORT` uses the beta OpenTelemetry exporter (§22.1) |
| 17 | Admin settings page | 2026-10-23: admin pages with sections that save separately (2026-10-24: split into People, Storage and Settings, with an admin rail that replaces the normal nav on those pages, as in Immich, §15.2); default quota off until set, per-person default / own limit / no limit; ML URLs in priority order with encrypted API keys and live health checks; ClamAV address stored and tested but not yet used; sign-in shown read-only; language is a per-person choice in the account menu (§4.3, §11, §17, §21, §23) |
| 18 | Trash and retention | 2026-10-24: trash groups by `trashed_root_id`, listed by root on the Trash page; restore to My files when the folder is gone or trashed, numbering on name clashes; Undo after trashing; no browsing inside trashed folders yet; trash retention is an admin setting (default 30 days); the purge runs hourly in the server role and also enforces version retention; per-blob advisory lock shared with uploads; no manual per-version delete and no warning email yet (§12) |
| 19 | Sizes and times in file lists | 2026-10-24: binary units (KiB, MiB, GiB, TiB, 1 KiB = 1024 B) everywhere, including quota inputs, with no setting; folders show the size of their live contents (current versions only) and their file and folder counts; list rows show thumbnails instead of type icons when one is ready; a per-person "Show seconds in times" switch on a new Settings page (`/settings`, linked from the account menu, which also holds the language choice), and full timestamps on hover (§16.4, §16.5, §17) |
| 20 | Quota from an OIDC claim | 2026-10-24: `OIDC_QUOTA_CLAIM` sets each person's quota at every login from a byte count, a binary size (`GB` = `GiB` = 1024³) or `unlimited`; unreadable values are ignored with a warning; the claim wins and locks the admin dialog; a missing claim falls back to the default; changes are audited as `system` with the source `claim` (§17.1) |
| 21 | Starred and Recent | 2026-10-25: stars are per person, on files and folders; Starred is sortable with a Location column; Recent is files only, newest first by the person's own last open, download, upload or version restore (not renames or moves), at most 100; Recent is a private per-person table, not the audit log, so preview opens stay unaudited (§6, §15.2, §18) |
| 22 | Readable files on disk | 2026-10-24: keep the hash-named pool as the only store; no real paths on disk, sidecars or live mirror. A name manifest is written beside each DB dump, so the tree can be rebuilt without Postgres, and an export job (worker, into `/data/exports`, also the `export` CLI) writes readable copies, incrementally, optionally with old versions, on a schedule that starts off. Lands in M6 (§7.1, §19) |
| 23 | Blobs per person | 2026-10-25: `blobs/{owner_id}/ab/cd/{sha256}`, keyed `(owner_id, hash)`, deduplicated per person only; renditions stay keyed by hash and go with the last copy; one blob lock per hash; copies across owners copy the bytes. No data migration, since nothing is in use yet (§6, §7, §12) |
| 24 | Folder uploads | 2026-10-25: one `POST /api/v1/folders/tree` makes all the folders first; a taken top-folder name asks Merge (default) / Keep both / Cancel, and merged folders reuse subfolders with the same names; the whole folder's size is checked against the quota before anything is made; hashing and the duplicate check run in batches so uploading starts early, with one duplicate and one name-clash question per folder; one tray row per folder; OS junk files skipped silently (§8.1) |
| 25 | Live updates | 2026-10-25: one SignalR hub; notices sent with `pg_notify` inside the changing transaction and forwarded by a `LISTEN`ing server; thin notices (topics and an origin, no ids or names) that make clients refetch; owner's clients only until sharing; per-tab client id to skip echoes; refetch everything after a reconnect; viewer banners instead of swapping the open file; ended sessions sign their clients out; the thumbnail poll stays only as a fallback (§15.4) |
| 26 | Search (M3) | 2026-10-25: pg_search BM25 over names (words + 3-grams) and page text indexed Swedish-stemmed, English-stemmed and unstemmed together, no language detection; text, metadata and automatic tags keyed by blob; Tesseract (`swe+eng`) in the worker for scans and photos, at background priority with an admin switch (default on), replaced by the ML service in M7; exiftool metadata searchable now, with `camera:` and `taken:`; snippets as text + offsets, one marking rule shared by server and clients; queries never logged. Tags planned as shared per file, sources user / rule / ml, automatic ones per blob and removable per file. Transcription waits for M7 (§6, §10, §13, §14) |
| 27 | Seeing what the worker is doing | 2026-10-25: admins see every job's files by name, owner and folder, whoever owns them (the admin already runs the server, and a hash alone is no use); jobs record `started_at`; queues pause per type through the `jobs` setting, checked when claiming; failed jobs can be retried or cleared, singly or all. Everyone sees a Processing section per file in the details panel, and a badge in grid and list only while steps wait or run; failed steps show only in details (errors for admins) and on the Jobs page. Metadata and text steps send no live notices yet, so pages poll every 2 s while they're pending; the Jobs page polls every 2 s; concurrency isn't editable yet (§10.1, §15.2, §16.4) |
| 28 | Details and text in the file info | 2026-10-25: the Info tab shows what was read (details from exiftool, now with GPS, lens, size and the camera's UTC offset; and the text with its source) and lets people edit the title, description and date taken (partial dates allowed) and the text, page by page; edits belong to the file version, not the shared bytes, so nobody else's copy changes; a new version keeps the details edits but not the text corrections, a restore brings back that version's edits; search uses the edits (corrected pages replace read ones, edited dates win in `taken:`); the original is never changed; edits are audited without their text (§6, §10.3, §13.5) |
| 29 | Tags | 2026-10-25: tags are shared per file (files only), case-insensitive, at most 50 characters; sources user / rule / ml; automatic tags per blob with per-file removals that stick, and a person's own tag wins over a rule's; admin keyword rules (one per tag) match stored text and metadata by stem, phrase or, from 5 letters, word start, but never typos, after the text and metadata stages and over everything when changed; admin Tags page with a preview; a Tags section in the Info tab, a bulk dialog, two tags in wide list rows; `tag:` and a Tag chip, and plain words match tags. Filing rules are decision 30 (§6, §10.2, §13.4, §13.6, §15.2, §16.4, §18) |
| 30 | Filing rules | 2026-10-25: each person's own rules in Settings, a tag plus a folder from the top of My files with `{year}` and `{month}` (two digits), dated by the edited date taken, then the camera's, then device modified time, then upload; one rule per tag, ordered, first match wins; only tags a file got while the rule was on, plus "Also file the N files that have this tag now"; a file is filed once, and moving it back or Undo sticks; `node.move` by the system naming the rule; a snackbar with Undo from a `filings` live notice, not the upload tray (§6, §10.2, §13.6, §15.4, §16.5, §18) |
| 31 | Text and Markdown viewers | 2026-10-25: text opens in a lazy, read-only CodeMirror 6 with the language from the file name, folding, find and a remembered wrap toggle; Markdown opens formatted (GFM via react-markdown, raw HTML as text) with a Formatted / Source toggle; web images load only after "Load images", with no referrer; relative links and images show as text for now; syntax colours are new `code-*` tokens. |
| 32 | Video and audio | 2026-10-25: ffprobe in the thumbnail stage stores `blob_media` per blob and plans playback by what every current browser plays (direct / remux / transcode); posters from the most typical frame 10 % in, cover art for audio; one MP4 copy per blob only when needed, streams copied when they play, otherwise H.264/AAC at CRF 23 fitted to 1080p (admin: 720p, 1080p or original), HDR tone-mapped, `+faststart`, no HLS; copies made in the background by default, or when opened, or never; failures not retried; HEVC-capable browsers get the original first; progress polled, not pushed (§10.3, §14) |
| 33 | Office previews | 2026-10-25: Gotenberg is optional, its URL an admin setting with a Test button (empty = off); the thumbnail stage converts Office and OpenDocument files by MIME type with the bytes checked, one page per sheet, and keeps one PDF per blob that the viewer shows with pdf.js and a "shown as PDF" note; page 1 makes the thumbnails; refused files fail without retries, an unreachable Gotenberg retries; turning it on queues documents uploaded while it was off; .doc/.xls/.ppt text comes from the PDF (§10.3, §14) |
| 34 | Transcription ahead of M7 | 2026-10-25: speech to text is built as the ML image's first piece, right after the M2 video/audio work (it needs ffmpeg in the worker), superseding decision 26's "waits for M7". faster-whisper with VAD behind `POST /transcribe`, streamed segments; Swedish/English detected per file; 16 kHz Opus sent, never originals; timed transcript pages (`start_ms`/`end_ms`, word times); no jobs without an ML URL, waiting when it's down; no Whisper in the worker image; hits play from 10 s before, transcript panel, captions from the transcript (§6, §10.2, §11.1, §13.3) |
| 36 | Theme setting | 2026-10-27: Same as device / Light / Dark on the Settings page, Same as device by default; saved per device in `localStorage`, not on the account, and applied by an inline script before first paint (§16.5) |
| 37 | Mobile app (M5) | 2026-10-28: the IdP redirects to an https callback page that hands the code to `dokumentsystem://signed-in`, so IdPs only need ordinary redirect URIs; the local address is used when it proves the session (HMAC of a nonce keyed by the stored token hash, checked on start, foreground and network change, 1.5 s timeout), with no Wi-Fi names or location; PDFs through the web's pdf.js as an Expo DOM component; uploads pause when the OS stops the app and resume from the tus offset; no pinned offline files, a 7-day query cache and the thumbnail cache instead; no admin pages in the app; app id `io.github.elmaxe.dokumentsystem`, scheme `dokumentsystem`, a debug APK from CI and no store listing (§5, §15.3, §15.4) |
| 38 | Text toggle in the viewer | 2026-10-29: a Text toggle shows every recognized word on photos and scans, and a PDF's own words on its text pages, as soft `primary` boxes under the search marks; the words are a selectable, invisible text layer, so they can be selected and copied (Axel chose select and copy over highlighting only), with Copy all on wider screens; a round button on phones; on while stepping files, off on the next open; words from the new `GET /nodes/{id}/words`, which serves what was read, not corrections (§14) |
| 39 | Sharing with people (M4) | 2026-10-29: only the owner shares; no shortcuts in My files (Shared with me plus Star instead); the people picker lists everyone who has signed in; links never expire unless a date is set; built in three PRs: people, links, notifications. Access is one SQL function over `path`; editors' additions and trash belong to the owner; filing rules don't move files out of sight of people they're shared with (§9). 2026-11-07: the Activity tab shows everyone with access the changes, and downloads only to the owner (§18) |
| 40 | Seek previews in the video player | 2026-11-02: one frame for every 1 % of a video, at least 2 s apart, none under 10 s; frames 256 px on the long edge, at most 10 to a row in one WebP per blob (`previews/ab/{sha}/seek.webp`) made by its own `seek-previews` stage with one ffmpeg seek per frame from the original and joined by NetVips; layout derived from the length, served unaudited by `GET /nodes/{id}/seek-previews` and described by `NodeDto.media.seekPreviews`; the web player shows the frame above the timeline on hover and while dragging, loading the sheet on first hover; no admin setting. The app's timeline comes after its first pull request merges (Axel chose web first) (§10.2, §10.3, §14) |
| 41 | Audio waveform | 2026-11-02: an audio file's timeline is its Audacity-style waveform: one mixed lane, peaks outline plus RMS core, scaled to the file's loudest moment, no zoom for now. The thumbnail stage decodes once with ffmpeg (8 kHz mono) into ≤ 32,768 slices of min/max/RMS, stored as a `waveform` rendition in a small binary format; `GET /nodes/{id}/waveform`, unaudited. Audio without cover art gets its waveform as its tile. Videos get seek previews instead (§14) |
| 43 | Merging and splitting PDFs | 2026-11-04: PDFs only for now (Axel chose this over Office files and photos too); PDFium in the worker, not Gotenberg or the clients; results are new files next to the originals, never versions, with Move the originals to the trash off by default; sources pinned by blob; the worker writes to the derived volume and the server saves, hashing again; text, words and text corrections copied per page, people's tags carried over, details from the first source, filing rules skipped, "Made from" per version; duplicates made anyway with Remove this copy; quota reserved from the sources and settled; 100 files or 2,000 pages per merge, 500 parts per split; page sheets on demand; the tray follows operations by polling; the app's selection mode and screens come in a second pull request (§8.4) |
| 44 | Signing out at the IdP | 2026-11-07: signing out on the web and in the app also ends the person's session at the IdP (RP-initiated logout) when its discovery lists an `end_session_endpoint`, so signing in again isn't silent (Axel chose web and app together over web only or staying local). The session keeps the IdP's ID token as `id_token_hint`; the IdP returns to `/api/v1/auth/signed-out` (web) or `/api/v1/auth/app-signed-out` (app), which must be registered as post-logout redirect URIs; without the endpoint, sign-out stays local. Ending sessions under Active devices stays local; back-channel logout is later (§5) |

## 26. Open questions

1. ~~**Video:** transcode to MP4 once, or generate HLS for large files?~~ MP4 once, only when needed (decision 32).
