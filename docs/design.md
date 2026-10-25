# Dokumentsystem — Design Document

> Status: **Draft v0.13** · Last updated: 2026-10-25
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
| `server` | API, auth, uploads, downloads/streaming, SignalR, serves the web SPA, the purge (§12) | yes (rw originals) | yes |
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
- **Everything else is admin settings stored in the DB** and edited on the admin settings page (`/admin`, admins only). Each is one row of `settings` (key → JSON); a missing row means the default, so a fresh install needs no seeding. Each section of the page saves on its own, and every change writes a `settings.change` audit event naming the key (never the value). Built so far (decisions 17 and 18): the default quota, trash and version retention, the ML URLs and the ClamAV address. Sign-in is shown on the page read-only, because OIDC stays in environment variables (§5). The rest of the list below follows as its features land:
  - quota defaults
  - trash and version retention
  - SMTP
  - backup schedule
  - ML URLs
  - Gotenberg URL
  - job concurrency (until the admin queue page exists: `Worker__ThumbnailConcurrency`, default 2)
  - OCR languages

---

## 5. Authentication and sessions

- **Login:** OIDC authorization code flow with PKCE against the user's IdP (Authentik, Authelia, Keycloak, Pocket ID, …).
  - Web: the server performs the flow as a confidential client (ASP.NET's OpenIdConnect handler). The browser navigates to `GET /api/v1/auth/login?returnTo=…`, and the IdP redirects back to `/api/v1/auth/callback`. Only local paths are accepted as `returnTo`.
  - The IdP's response comes back as a query string, so the short-lived correlation cookies can be SameSite=Lax. That also lets login work over plain http on a LAN.
  - Mobile: the app opens the system browser and redirects back to a custom scheme. The server then exchanges the code. The hand-off to the app is built in M5. Bearer sessions are already accepted.
- **Configuration** is bootstrap env (§4.3). Sign-in stays off until `OIDC_ISSUER` and `OIDC_CLIENT_ID` are set, so a fresh install still starts.

  | Variable | Default | Meaning |
  |---|---|---|
  | `OIDC_ISSUER` | — | Issuer URL, e.g. `https://auth.example.com/application/o/dokument/` |
  | `OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET` | — | The confidential client registered at the IdP. Its redirect URI is `${PUBLIC_URL}/api/v1/auth/callback`. |
  | `OIDC_SCOPE` | `openid profile email` | Add `groups` for IdPs that need it (e.g. Authelia) |
  | `OIDC_GROUPS_CLAIM` | `groups` | Read from the ID token and the userinfo endpoint |
  | `OIDC_USER_GROUP` | unset (anyone at the IdP may log in) | Allow-list group |
  | `OIDC_ADMIN_GROUP` | `dokument-admins` | Members get the admin role |
  | `OIDC_PROVIDER_NAME` | unset | Shown on the button: "Sign in with …" |
  | `OIDC_QUOTA_CLAIM` | unset (quotas are set on the admin page) | A claim that sets each person's storage at login (§17.1) |

- **`PUBLIC_URL`** sets the scheme, host and path base that the app sees itself at, behind the reverse proxy. It drives the redirect URI and the cookie's Secure flag.
- **Sessions:** after a successful OIDC login, the server issues **its own session**. The IdP's tokens are discarded.
  - The token is 256 random bits. Only its SHA-256 is stored in `sessions`, so a leaked database doesn't hand out live sessions.
  - **Lifetime:** a session expires after **30 days without use**. Each use slides the expiry forward, with at most one write every 5 minutes.
  - **Web:** an httpOnly, SameSite=Lax cookie (`dokument_session`). It is Secure whenever `PUBLIC_URL` is https.
    - CSRF: mutations made with the cookie must send `X-CSRF-Token`. Its value is a hash of the session token, returned by `GET /api/v1/auth/session`.
  - **Mobile:** the same opaque token, sent as `Authorization: Bearer`. It is stored in the secure enclave/keystore and needs no CSRF token.
  - **Revoking:** users list and revoke their own sessions ("active devices": `GET/DELETE /api/v1/sessions`). Admins do the same for anyone (`/api/v1/admin/users/{id}/sessions`, `/api/v1/admin/sessions/{id}`). A revoked session stops working on its next request.
  - **Sign out** revokes the current session only. It does not end the user's session at the IdP.
- **API keys:** personal, scoped (read-only / read-write), for scripts, rclone and WebDAV later.
- **User provisioning:** users are created on first login (JIT), with a stable identity from `iss` + `sub`.
  - Email, display name and role are refreshed on every login. Disabled users are refused, and their sessions stop working.
  - An optional **allow-list**: only users in the IdP group named by `OIDC_USER_GROUP` (e.g. `dokument-users`) may log in. Admins are always allowed.
  - The `dokument-admins` group → admin role. The mapping is re-evaluated on each login.
  - An optional claim sets the person's quota (§17.1).
- **Rate limiting:** 20 login starts per minute per IP.
- **Break-glass:** a CLI command in the container (`dokument admin reset-oidc` / `create-admin-token`) for when the IdP is broken.
- **Local development:** Aspire runs Keycloak with a `dokument` realm. Its test users are alice (admin), bob (user) and carol (no groups); each password is the username.
- **Built in M0:** login, sessions, CSRF and active devices. **Still to come:**
  - API keys and the break-glass CLI
  - auth audit events, with the audit table in M1
  - the quota claim (§17.1), right after the admin settings page
  - the mobile hand-off, in M5

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
      starred_by …,               -- (separate table)
      created_at, modified_at, trashed_at NULL, trashed_root_id NULL,
      UNIQUE NULLS NOT DISTINCT (owner_id, parent_id, name_key) WHERE trashed_at IS NULL)

file_versions(id, node_id, version_no, blob_hash, size, mime_type, original_name,
              uploaded_by, uploaded_at, client_modified_at, restored_from NULL, keep_forever,
              comment NULL)

blobs(hash = sha256 PRIMARY KEY, size, storage_key, ref_count, created_at)

uploads(id, user_id, owner_id, parent_id NULL, target_node_id NULL, name, size, upload_offset,
        claimed_hash NULL, mime_type, client_modified_at, on_conflict {fail|keepBoth|newVersion},
        created_at, expires_at, result_node_id NULL, completed_at NULL)   -- tus uploads in flight

blob_previews(blob_hash PK, status {ready|unsupported|failed}, pipeline_version, width NULL, height NULL,
              page_count NULL, updated_at)       -- what the thumbnail stage made of a blob
renditions(blob_hash, kind {thumb_s|thumb_m|preview|preview_pdf|video_web|poster|…},
           storage_key, mime_type, width, height, created_at, PRIMARY KEY (blob_hash, kind))

metadata(version_id, kind {exif|xmp|id3|pdf_info|ffprobe…}, data jsonb)

content_pages(version_id, page_no, source {text_layer|ocr|transcript}, lang, text,
              ocr_engine, ocr_version)           -- BM25-indexed
embeddings(version_id, page_no NULL, model, vector vchord) -- optional

shares(id, node_id, grantee_user_id, role {viewer|editor}, created_by, created_at, expires_at NULL)
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
  - Uploading identical bytes **reuses the existing blob** (dedup by SHA-256).
  - Re-uploading identical content to the same file does **not** create a new version.
- **Name uniqueness within a folder:** NFC-normalized and case-insensitive (`name_key`). The top of each user's "My files" (parent NULL) counts as one folder, hence `NULLS NOT DISTINCT`. Conflicts are resolved like Drive: either "file (1).pdf" or "upload as new version", chosen by the user.
- **Names** (`NodeName` in the domain) are trimmed, NFC, at most 255 UTF-8 bytes, never "." or "..", and contain no slashes or control characters, so `dokument export` can write any of them to disk.
- **Renditions belong to blobs, not versions** (decision 15): identical bytes are rendered once, and a copy or a restored version has its thumbnail at once.
- **`path ltree`** makes subtree operations cheap: permission inheritance, "trash folder", "folder size", "search in folder".

---

## 7. Storage layout

```
/data/originals/            # PRECIOUS — back this up
  keys/                     # ASP.NET Data Protection keys that encrypt secrets in settings (§21)
  blobs/ab/cd/abcdef…sha256 # content-addressed, write-once, never modified
  tmp/{upload_id}           # in-flight uploads (tus), same filesystem for atomic rename
/data/derived/              # REGENERABLE — backup optional
  thumbs/ab/{sha256}/{kind}.webp   # thumb_s, thumb_m, preview; keyed by blob like the originals
  previews/ab/{sha256}/…           # later: PDF renditions of Office files, web-playable video, posters
/data/backups/              # DB dumps (see §13)
```

- **Write path:**
  1. Append each tus chunk to `tmp/{upload_id}` at the recorded offset.
  2. When the last byte arrives, hash the temp file and compare it with the SHA-256 the client sent. A mismatch discards the upload. (Hashing as bytes arrive doesn't survive a resume, since a SHA-256 state can't be saved between requests, so the file is read once more instead.)
  3. `fsync`, then `rename()` to `blobs/…`. If the blob already exists, drop the temp file.
  4. Commit the DB transaction: the blob row (`ON CONFLICT DO NOTHING`), the node or version, `ref_count + 1`, the quota and the audit event.
- **Blob deletion** happens only in the purge job (§12): when `ref_count` drops to 0, the blob is deleted **after** the DB commit (outbox pattern).
- **Integrity job:** a scheduled sweep finds orphaned blobs, missing blobs and checksum mismatches (bit rot), and reports to admins.
- **Export:** `dokument export --user X --to /path` writes the real folder tree with original names and mtimes. This is the "if the app dies, my files are still mine" guarantee.

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
   - Emit a SignalR event (later) and an audit event.
4. Abandoned uploads expire after 24 h without progress, which releases the reservation and deletes the temp data. The server sweeps them hourly, since it owns `tmp/`.

Implementation notes (built in M1):
- The server implements tus 1.0 core with the `creation`, `creation-with-upload`, `termination` and `expiration` extensions itself (`TusEndpoints`), rather than through tusdotnet, so the `uploads` row and the quota reservation share a transaction. Any tus client works; the web uses `tus-js-client`.
- One request writes to an upload at a time (423 otherwise). Uploads are owned by the server process, so an in-memory lock is enough.
- `GET /api/v1/uploads/{id}` returns the upload's state as JSON and, once complete, the file it produced and any duplicates of it (the fallback in §8.3).

Also:
- The client sends `client_modified_at` (the file's mtime) for display. The original bytes stay untouched.
- **Folder upload** (drag-and-drop a directory) creates the folder tree first, then uploads the files with limited parallelism.

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

On disk, identical bytes are always stored once (§6). On top of that, **users are told** when they upload a file they already have.

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
  - The "instant, no transfer" shortcut is allowed only in that same case. Otherwise the client has to upload the real bytes, which prevents the classic "I know the hash, give me the file" dedup attack. The server still dedups on disk after verifying the hash.
- **Folder uploads** are checked file by file, and the summary covers the whole folder.
- An optional **"Duplicates" view** under Storage lists groups of identical files across the user's library, for cleanup.

---

## 9. Permissions and sharing

- **Roles:** `owner` (implicit, one per node), `editor`, `viewer`.
  - **Viewer:** see, preview, download (unless a link disallows it).
  - **Editor:** viewer, plus upload, new version, rename, move within the shared tree, trash.
  - **Owner:** editor, plus share, permanently delete, transfer ownership.
- **Inheritance:** a grant on a folder applies to its whole subtree.
  - A grant lower down can only **add** rights; it can never remove inherited ones. Same rule as Drive, and easy to reason about.
- **Effective permission:** the highest role among the grants on the node and its ancestors (via `path`), or ownership. It's a single SQL query, and the same predicate is reused in search.
- **Quota attribution:** files uploaded into someone else's shared folder are **owned by, and count against, the folder owner**.
  - The uploader is recorded on the version.
  - This prevents "your files are stranded in my account" problems.
- **"Shared with me":** a virtual view listing the top-level nodes shared directly with the user. Users can "add a shortcut to My files" (a shortcut node) without copying.
- **Public links:** viewer-only (upload-capable "file request" links come later), with optional password, expiry and allow-download flag. Access goes through `/s/{token}` and is rate-limited. Every access is audited.
- **Groups:** not in v1. User-to-user grants are enough for a family. The model leaves room for `grantee_group_id` later.

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
- **Backfill:** when the worker starts, it queues the thumbnail stage at background priority for every blob without results from the current `pipeline_version`. That covers files from before a stage existed and pipeline upgrades.
- **Admin UI:** queue sizes, running and failed jobs with errors, "retry failed", "regenerate missing", pause/resume per queue.

### 10.2 Stages

```
upload complete
   └─► detect   (MIME by magic bytes, not extension)
        └─► metadata   (exiftool / ffprobe / PdfPig info → metadata table)
             ├─► thumbnail (+ preview renditions)
             ├─► text-extract  (text layer: PDF, Office, plain text)
             │      └─► ocr   (only if no/insufficient text layer)  → ML or local Tesseract
             ├─► transcode (video/audio only if not browser-playable)
             ├─► embed     (optional, ML)
             └─► transcribe (optional, ML; audio/video → searchable text)
```

### 10.3 Tooling per format

| Format | Metadata | Thumbnail / preview | Text |
|---|---|---|---|
| JPEG/PNG/WebP/GIF/HEIC/AVIF/TIFF/SVG (RAW later) | exiftool | **NetVips** (libvips + libheif) → WebP thumbs, plus a WebP preview when the browser can't show the original | OCR (ML) |
| PDF | PdfPig / exiftool | **PDFium** renders page 1 for the thumbs; the viewer renders pages with pdf.js | PdfPig text layer → OCR when there is none |
| DOCX/XLSX/PPTX/ODF/RTF | OpenXML SDK / exiftool | **Gotenberg** (LibreOffice) → PDF rendition → PDFium | OpenXML/ODF text extraction; fall back to text from the PDF rendition |
| Video | ffprobe | ffmpeg poster frame; **web-playable rendition only if needed** (H.264/AAC MP4, or HLS for large files). Hardware acceleration: VAAPI/QSV/NVENC. | Transcript (ML, optional) |
| Audio | ffprobe / ID3 | Embedded cover art, or a generic icon | Transcript (ML, optional) |
| Text/Markdown/code/CSV/JSON | — | Rendered first lines as thumb (optional) | Raw text (size-capped) |
| Archives (zip) | entry list | icon | entry names only |

**Thumbnail stage, as built (M2):**
- The format comes from the bytes: `%PDF-` in the first KiB, otherwise libvips' loader, limited to an allowlist (JPEG, PNG, WebP, GIF, HEIF/AVIF, TIFF, SVG) so text files never reach the CSV or matrix loaders. Anything else is `unsupported` and keeps its type icon.
- Two WebP thumbnails at quality 80, never upscaled: `thumb_s` at most 256 px on the long edge (lists) and `thumb_m` at most 640 px (grid tiles). EXIF rotation is applied, colour is converted to 8-bit sRGB, and metadata is stripped. Originals are only read.
- A `preview` WebP of at most 2048 px is added for images browsers can't show (HEIC, TIFF, SVG) and for anything over 4096 px. Other images are viewed from the original.
- A PDF's page 1 is rendered on white at 1280 px on the long edge and scaled down for the thumbs. Its page count and page size are stored.
- The Docker image uses Ubuntu's libvips with the libde265 plugin, because the libvips bundled in NetVips.Native can't decode HEVC, the codec in iPhone HEIC photos. Development builds use the bundled one, so HEIC thumbnails only work in the image (or with `-p:UseSystemLibvips=true` and a system libvips).

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
  - `POST /transcribe` (later): Whisper (faster-whisper) → timed segments.
  - `GET /health` and `GET /models`.
- **Results are stored with `engine` + `model` + `version`**, so changing the model triggers re-processing.
- **No-ML fallback:** the worker image includes **Tesseract** (`swe` + `eng`) for basic OCR. A NAS without an ML service still gets searchable scans.

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
  3. For each deleted version, lowers `blobs.ref_count`, and lowers the owner's `used_bytes` by the blob's size once they have no version left with those bytes.
  4. Deletes blobs whose `ref_count` is zero: the row (its renditions and preview state cascade), the original and its thumbnails.
- **Blob lock:** uploads and the purge take the same transaction-scoped advisory lock per blob hash. The upload holds it while it moves its temp file into `blobs/` and records the version; the purge holds it while it deletes the row and the files. So a file that someone uploads again at the moment the purge deletes it is never lost: the upload either finds the old file still referenced or stores its own copy.
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

- `nodes`: `name` (with n-gram/fuzzy tokenization for partial filename matches).
- `content_pages`: `text`, using a Swedish or English stemmer depending on the detected language, plus a language-agnostic field.
- Filterable fields: owner, mime group (document, image, video, audio, …), modified date, size, folder subtree (`path`), starred, shared-with-me.

### 13.3 Query

- Free text plus chips/operators, e.g. `type:pdf owner:me in:"Kvitton" after:2025-01-01 "exakt fras"`.
- Results are **files, not pages**. Each shows its best-matching page with a highlighted snippet. Clicking opens the viewer on that page with the hits highlighted (we have OCR bounding boxes).
- **Hybrid search (optional):** BM25 results and vector results are merged with reciprocal rank fusion. Examples: "kvitto från IKEA" finds the receipt even when the OCR is poor, and "beach sunset" finds photos.
- **Scope:** search *always* applies the same effective-permission predicate as browsing (§9).

---

## 14. Viewers

| Type | Web | Mobile |
|---|---|---|
| Images | The original, or the WebP preview for HEIC/TIFF/SVG and huge images; zoom/pan; EXIF info panel later | Same; `expo-image` |
| PDF | **pdf.js** with page thumbnails, search-in-document, OCR text-layer overlay for scans | `react-native-pdf` (or pdf.js in a WebView) |
| Office | The server's **PDF rendition** in the PDF viewer | Same |
| Video | `<video>` with HTTP Range; web rendition if the original isn't playable | `expo-video` |
| Audio | `<audio>` player with cover art; playlist for a folder | `expo-audio` (background playback) |
| Text / code / Markdown | CodeMirror (read-only) / rendered Markdown | Rendered text |
| Other | Metadata + download | Download / "open in…" |

- **Built in M2 (web):** a full-screen viewer at `/files/{id}` for images (zoom steps from 50 % to 400 %, double-click, drag to pan), PDFs (pdf.js with range requests, one page at a time fitted to the width, Page Up/Down), text (the first 1 MiB, as plain text; later a read-only CodeMirror 6 view with syntax highlighting, line numbers, folding and search for code, JSON, YAML and logs, plus rendered Markdown), and audio and video the browser can play. Everything else shows "No preview" with Download. Arrow keys or a swipe move between the files in the folder; Esc closes. PDF page thumbnails, search in the document and the OCR overlay come later.
- **`GET /nodes/{id}/preview` serves what the viewer shows** and is **not audited** (§18): the WebP preview if there is one, otherwise the original bytes inline, with range support. Markup (HTML, XML, SVG) is served as `text/plain`, with `nosniff` and a `sandbox` CSP, so a preview can never run in our origin. `GET /nodes/{id}/content` stays the audited download. Anyone who can view a file can read its bytes through the preview, so the audit log records downloads, not every way the bytes could leave.
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
  - **Admin pages swap the nav, as in Immich.** Under `/admin` the rail (and the bottom nav on phones) lists the admin pages instead: **People** (`/admin/people`: everyone who has signed in, their usage and their quota), **Storage** (`/admin/storage`: disk usage and the default quota) and **Settings** (`/admin/settings`: machine learning, virus scanning, trash and versions, sign-in). Job queues and server stats join the list when they're built. The rail's top button is "Back to files" where the New button usually sits; on phones it's a back arrow in the top bar. `/admin` opens People. A person's own settings (language for now) aren't admin pages, so they keep the normal nav.
- **Content area:** grid/list toggle, breadcrumbs, sorting, multi-select with a contextual action bar, right-click menu.
- **Details panel:** info, sharing, versions, activity.
- **Upload tray** (bottom-right) with per-file progress, pause/resume and retry.
- **Real-time updates via SignalR:** new files, thumbnails ready, changes from other devices.
- Optionally a PWA, for "Add to home screen" on phones.

### 15.3 Mobile app (React Native)

- **Onboarding:**
  1. Enter the server URL.
  2. Log in via OIDC in the system browser.
  3. Optionally add a **secondary URL** for automatic LAN/WAN switching (on home Wi-Fi, use the local URL), as Immich does.
- **Core:** browse, preview, search, share, upload, versions, trash.
- **Document scanner:** camera → edge detection and perspective correction → multi-page PDF → upload → server OCR. Uses VisionKit on iOS and ML Kit Document Scanner on Android, via `react-native-document-scanner-plugin`.
- **Share target:** "Share to Dokumentsystem" from other apps. This needs native extensions, which is why we use dev builds.
- **Offline files:** pin a file or folder to keep a local copy, synced when it changes.
- **Background uploads:** resumable tus uploads that survive app suspension. Expect iOS limitations.
- **Push notifications (later):** via **ntfy / UnifiedPush** to avoid depending on Google and Apple. The in-app notification center works without it.

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

Every text pair meets WCAG AA (4.5:1) in both themes. Icons, borders and the focus ring meet 3:1.

**Typography:** **Albert Sans** (SIL OFL) for all UI text, with the latin-ext subset for å, ä and ö. **IBM Plex Mono** only for checksums, keys and job ids. The same font files are bundled in the app with `expo-font`.

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
- **FileTypeIcon:** a Material Symbol in the `file-*` colour per MIME group. Also the thumbnail fallback. Folders stay neutral.
- **FileGrid:** folders first, then file tiles with 4:3 thumbnails. Documents (PDF, Office, text) show the top of page 1; photos are cropped to fill. A shimmer shows while a thumbnail is pending, and the thumbnail is swapped in when it is ready: for now the listing is polled (every 2 s, then every 10 s) while anything is pending, and SignalR replaces that with live updates. Video tiles show a duration badge.
- **FileList:** a sortable name / owner / modified / size table. Under 600 px of container width, the columns fold into a second line. Pages can swap the columns after the name: the Trash page shows original location / deleted / size.
  - A file with a ready thumbnail shows it in place of the type icon: 32 px, 40 px on phones, with pages showing their top as in the grid. Folders and files without a preview keep the icon, in a box of the same size so names line up; a pending thumbnail shimmers.
  - **Folders show their size:** the current version of every live file below them, at any depth. Old versions and trashed files don't count, so it isn't the same number as quota usage (§17). The listing API returns it as the folder's `size` plus `contents` (file and folder counts) from one ltree query per listing. An empty folder shows "—", and hovering a folder's size shows what it holds ("1,204 files, 38 folders").
  - Hovering a date shows the full timestamp ("23 October 2026, 17:40").
- **ActionBar:** the selection count plus Share, Download, Move, Trash and More.
- **UploadTray:** one row per tus upload, in one of the states hashing, uploading, paused, done, duplicate ("Remove this copy") or failed (Retry), each with an icon and words.
- **DuplicateDialog:** the §8.3 prompt. A batch offers Skip duplicates / Upload all / Review; a single file offers Skip / Upload anyway / Open existing.
- **ShareDialog:** add people, then people with access (inherited grants are labelled and cannot be lowered), then the public link with expiry, password and download chips. A bottom sheet on phones.
- **VersionHistory:** the Versions tab of the details panel. It marks the current and "Keep forever" versions and warns "Removed in N days" per the retention policy.
- **StorageMeter:** a warning at 80 % and a critical state at 95 %, in words as well as colour.
- **FileViewer:** the §14 viewer: a header with the file's name, Share, Download, details and More; the preview; a floating toolbar for pages and zoom; previous/next arrows on wide screens and swipes on phones. States: loading, preparing (the worker is still making the preview), no preview, and failed, each with Download.
- **NavRail**, **BottomNav**, **EmptyState** (also the drag-and-drop target that names the destination folder).

### 16.5 Content and accessibility

- Copy is in sv and en from day one. Swedish runs about 30 % longer, so never size a control to fit English text.
- **Copy doesn't assume a setup.** Dokumentsystem is open source and runs on whatever people have, so the UI never says "the NAS", "your household" or names a particular machine or provider. Say "this server", "your OIDC provider" or "a more powerful computer" instead. Product names appear only as examples ("such as Authentik or Keycloak") or when they come from config (`OIDC_PROVIDER_NAME`).
- Copy is in sentence case, with no exclamation marks and no emoji. Buttons are verbs ("Copy link"), and toasts confirm in the past tense ("Link copied"). Errors say what happened and what to do next.
- **Icons:** Material Symbols Rounded (Apache-2.0), outlined by default and filled when active or selected.
- Keyboard navigation everywhere: arrow keys in the grid, Space to select, Enter to open, Delete to trash. Focus is shown by a visible 2 px focus ring. Every icon button has a label.
- **Clickable mockup:** `packages/ui` holds the components in Storybook (`pnpm --filter @dokument/ui dev`), with toolbar switches for light/dark, sv/en and phone to desktop widths. The **Screens** stories are one app on fake data: the file browser, viewer, search results and admin settings, with selection, sharing, uploads and the duplicate prompt working locally.
- Dates in lists come from `formatFileDate` in `packages/core`: "Today 09:14", "Yesterday", "12 Mar", then "21 Jun 2025". English uses British day-month order and a 24-hour clock. Tooltips and the details panel use `formatFullDate` ("23 October 2026, 17:40").
- **Settings page** (`/settings`, "Settings" in the account menu): the person's own settings, with the normal nav, saved as soon as they change. Sections so far: Language (the same choice as the account menu's quick switch) and Date and time.
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
  - files: upload, new version, restore version, rename, move, download, trash (`node.trash`), restore (`node.restore`), delete forever (`node.delete_forever`), empty trash (`trash.empty`)
  - system: the purge removing expired trash (`node.purge`) and old versions (`version.purge`), with the actor kind `system`
  - **Not logged:** opening a preview or viewing a file. It's too noisy for its value. Downloads of the original *are* logged.
  - sharing: share granted/changed/revoked, link created/accessed/revoked
  - admin: settings changed, quota changed, user disabled
- **Partitioned by month** (from M6; M1 ships a plain table with indexes on `at` and `(target_node_id, at)`). An admin setting controls retention (default 1 year).
- **Views:**
  - users see the activity on their own files ("Activity" tab)
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

---

## 20. Notifications

- **SMTP** via MailKit. Admins configure host, port, TLS, user, password and from-address, and can send a test email.
- **In-app notification center** (SignalR, live).
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
    - later: text extraction, OCR and indexing
  - Server spans carry `dokument.user.id` for signed-in requests.
  - **Jobs carry the trace across the queue.** Enqueueing stores the current `traceparent` on the `jobs` row (`trace_parent`, nullable), and the job's span links to it. An upload's `upload finish` span then leads to its thumbnail job, even though the worker runs it later. The job is its own trace, not a child, so a slow queue doesn't stretch the upload's trace.
  - Calls to the ML service and Gotenberg propagate W3C `traceparent`. The ML service uses the Python OpenTelemetry SDK with the same `OTEL_*` variables.
  - `/health/*` and `/metrics` are not traced. Sampling is parent-based, default always-on, and can be lowered with `OTEL_TRACES_SAMPLER=parentbased_traceidratio`.
- **Metrics:**
  - ASP.NET Core and Kestrel, HttpClient, Npgsql, .NET runtime and process metrics.
  - Our own `Meter("Dokument")`, named `dokument.*`:
    - `dokument.jobs.queued` / `dokument.jobs.running` (gauges by `job.type`), `dokument.jobs.duration` (histogram by type and outcome), `dokument.jobs.failed` (counter), `dokument.jobs.lag` (age of the oldest due job)
    - `dokument.uploads.bytes` (counter), `dokument.uploads.active` (gauge), `dokument.downloads` (counter)
    - `dokument.renditions.generated` (counter by `dokument.rendition.kind`), `dokument.storage.bytes` (by `dokument.storage.kind`: originals and derived)
    - The gauges (job counts and lag, active uploads, storage) describe the whole install, so the worker samples them from Postgres every 30 s and walks the derived volume every 10 minutes, only while metrics are exported. With several workers each reports the same values under its own `service.instance.id`, so dashboards should take the max.
    - later: search query duration, OCR pages, ML endpoint up/down
  - **Prometheus scraping** is optional for people who run Prometheus without a collector: `METRICS_PORT` turns on a `/metrics` endpoint on its own port, on every interface, so it is never reachable through the public port or reverse proxy. It is the OpenTelemetry Prometheus exporter, which is still a beta package. Don't publish that port to the internet.
- **Logs:** Serilog stays the logging API. When OTLP is on, the Serilog OpenTelemetry sink exports the same structured events with their trace and span ids, next to the console output. We resolve its endpoint, protocol and headers from the same `OTEL_*` variables as the SDK (including `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT`) and give it the same resource, so logs line up with traces and metrics. The admin UI's log level applies to both.
- **Privacy rules for every signal:**
  - No file or folder names, paths, file contents, search queries, email addresses, display names, IP addresses, tokens, cookies or `Authorization` headers.
  - URLs are recorded as route templates without query strings.
  - Users and nodes appear only as their opaque ids (`dokument.user.id`, `dokument.node.id`), and only on spans, never as metric labels, which stay low-cardinality.
  - The OTLP endpoint is the admin's choice; we never send telemetry anywhere by default.
- **Built (v0.10):** everything in this section that has something to instrument. Propagation to the ML service and Gotenberg comes for free with the HttpClient instrumentation when those clients exist; the ML service's own Python SDK setup, search and OCR metrics, and the ML up/down gauge come with those features.
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
| **M1: Drive core** | Folders, tus uploads, downloads, rename/move, versioning, trash, quotas, audit, web file browser. **Built so far:** folders, rename/move, tus uploads with the duplicate check, audited downloads, versions (restore, Keep forever), quota reservation, file audit events, the web file browser, and the trash with the hourly purge that also enforces version retention. **Still to come:** starred and recent, folder uploads, live updates. |
| **M2: Previews** | Worker + job queue, metadata, thumbnails (images/PDF/video), viewers (image/PDF/video/audio/text), Gotenberg Office previews. **Built so far:** the job queue, image and PDF thumbnails with the backfill, the unaudited preview endpoint, and the web viewer for images, PDFs, text and browser-playable media. **Still to come:** metadata, video posters and transcoding, Gotenberg, the admin queue page, rendered Markdown, and a CodeMirror 6 text viewer (syntax highlighting, folding, search; lazy-loaded, read-only). |
| **M3: Search** | Text extraction, Tesseract OCR, pg_search BM25 with Swedish/English, search UI with snippets |
| **M4: Sharing** | User shares, public links, Shared with me, SMTP + in-app notifications |
| **M5: Mobile** | Expo app: browse, view, upload, search, share target, document scanner, LAN/WAN switching |
| **M6: Ops** | DB dump schedule, integrity job, admin system page, export CLI, docs, multi-arch release. OpenTelemetry (§22.1): the base wiring (OTLP export, standard instrumentation, Serilog sink) can land any time before this; each milestone adds its own spans and `dokument.*` metrics, and M6 adds the `/metrics` port and the docs' collector example. |
| **M7: ML** | ML service (OCR+, embeddings, hybrid search), remote ML URLs with fallback, transcription |
| **Later** | WebDAV, offline files, push via ntfy/UnifiedPush, groups, optional ClamAV, file-request links (public upload folders) |

---

## 25. Decisions log

| # | Question | Decision |
|---|---|---|
| 1 | Product name | **Dokumentsystem** |
| 2 | Version retention defaults | Same as Google Drive: non-current versions are kept **30 days or up to 100 versions**, whichever comes first. Admins can change this, and users can mark a version "Keep forever" (§8.2). |
| 3 | Audit preview opens? | **No.** Downloads are audited, previews are not (§18). |
| 4 | Duplicate uploads | **Tell the user**: a pre-upload check with Skip / Upload anyway / Open existing, limited to files the user can access (§8.3) |
| 5 | Public links with upload ("file requests") | **Later** (roadmap) |
| 6 | Storage layout | App-owned, content-addressed (§7) |
| 7 | Sharing model | Personal folders shared to users; no team drives (§9) |
| 8 | Virus scanning | Not in v1; hook for optional ClamAV (§21) |
| 9 | WebDAV | Later |
| 10 | Design system | **Approved** 2026-10-21: Material 3 roles, fjord-blue accent, Albert Sans, Material Symbols Rounded, 4 px grid (§16) |
| 11 | Monorepo scaffold | 2026-10-21: .NET 10 LTS, Aspire 13, Postgres 17, Node 22 + pnpm + Turborepo, Tailwind v4 on web, AGPL-3.0 `LICENSE` (§3.2, §4.1, §15.1) |
| 12 | Sessions | 2026-10-22: opaque tokens, stored hashed, expire after 30 days idle (sliding). Cookie + `X-CSRF-Token` on web, bearer on mobile. Sign-out is local, not at the IdP (§5). |
| 13 | UI components | 2026-10-22: web components live in `packages/ui` (Radix + Tailwind v4, Storybook 10). The mobile app gets its own components on the same tokens (§15.1, §16.5) |
| 14 | File storage (M1) | 2026-10-22: our own tus 1.0 endpoint; the server hashes the finished temp file once; names are `NodeName` (NFC, case-insensitive, 255 bytes); owner-only access until sharing (M4) behind one `NodeAccess` predicate; no quota until an admin sets one; audit table unpartitioned until M6 (§6–8, §18) |
| 15 | Thumbnails and previews (M2) | 2026-10-23: our own Postgres job table; renditions keyed by blob hash; WebP thumbs at 256 and 640 px plus a 2048 px preview only when the browser can't show the original; HEIC via the image's system libvips; the grid polls until SignalR; the viewer reads an unaudited `/preview` endpoint (§6, §7, §10, §14) |
| 16 | Observability | 2026-10-23: OpenTelemetry for traces, metrics and logs, off until `OTEL_EXPORTER_OTLP_ENDPOINT` is set; standard `OTEL_*` variables only; trace context stored on jobs; strict no-PII attribute rules; optional Prometheus endpoint on a separate port. On by default under the Aspire AppHost in development. Prometheus on `METRICS_PORT` uses the beta OpenTelemetry exporter (§22.1) |
| 17 | Admin settings page | 2026-10-23: admin pages with sections that save separately (2026-10-24: split into People, Storage and Settings, with an admin rail that replaces the normal nav on those pages, as in Immich, §15.2); default quota off until set, per-person default / own limit / no limit; ML URLs in priority order with encrypted API keys and live health checks; ClamAV address stored and tested but not yet used; sign-in shown read-only; language is a per-person choice in the account menu (§4.3, §11, §17, §21, §23) |
| 18 | Trash and retention | 2026-10-24: trash groups by `trashed_root_id`, listed by root on the Trash page; restore to My files when the folder is gone or trashed, numbering on name clashes; Undo after trashing; no browsing inside trashed folders yet; trash retention is an admin setting (default 30 days); the purge runs hourly in the server role and also enforces version retention; per-blob advisory lock shared with uploads; no manual per-version delete and no warning email yet (§12) |
| 19 | Sizes and times in file lists | 2026-10-24: binary units (KiB, MiB, GiB, TiB, 1 KiB = 1024 B) everywhere, including quota inputs, with no setting; folders show the size of their live contents (current versions only) and their file and folder counts; list rows show thumbnails instead of type icons when one is ready; a per-person "Show seconds in times" switch on a new Settings page (`/settings`, linked from the account menu, which also holds the language choice), and full timestamps on hover (§16.4, §16.5, §17) |
| 20 | Quota from an OIDC claim | 2026-10-24: `OIDC_QUOTA_CLAIM` sets each person's quota at every login from a byte count, a binary size (`GB` = `GiB` = 1024³) or `unlimited`; unreadable values are ignored with a warning; the claim wins and locks the admin dialog; a missing claim falls back to the default; changes are audited as `system` with the source `claim` (§17.1) |

## 26. Open questions

1. **Video:** transcode to MP4 once, or generate HLS for large files? MP4-once is simpler and probably sufficient.
