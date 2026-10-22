# Dokumentsystem — Design Document

> Status: **Draft v0.4** · Last updated: 2026-10-22
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
      └───────┬───────────────────────────┬──────────┘
              │                           │
   ┌──────────▼─────────┐        ┌────────▼──────────────┐
   │ PostgreSQL         │◄───────┤ worker (ROLE=worker)  │
   │ + pg_search        │  jobs  │ metadata · thumbnails │
   │ + vchord/pgvector  │        │ text extraction · PDF │
   └────────────────────┘        │ renditions · transcode│
                                 │ trash purge · backups │
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
| `server` | API, auth, uploads, downloads/streaming, SignalR, serves the web SPA | yes (rw originals) | yes |
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
  Dokument.Worker/          worker role: job handlers (thumbnails, extraction, purge, backup …)
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

- **Environment variables, for bootstrap only:** DB connection (`DB_HOST`, `DB_PORT`, `DB_USERNAME`, `DB_PASSWORD`, `DB_DATABASE`, or `ConnectionStrings__Dokument`), OIDC issuer/client, paths, `PUBLIC_URL`, `ROLE`.
- **Everything else is admin settings stored in the DB** and edited in the UI:
  - quota defaults
  - trash and version retention
  - SMTP
  - backup schedule
  - ML URLs
  - Gotenberg URL
  - job concurrency
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
  - An optional claim sets the quota tier.
- **Rate limiting:** 20 login starts per minute per IP.
- **Break-glass:** a CLI command in the container (`dokument admin reset-oidc` / `create-admin-token`) for when the IdP is broken.
- **Local development:** Aspire runs Keycloak with a `dokument` realm. Its test users are alice (admin), bob (user) and carol (no groups); each password is the username.
- **Built in M0:** login, sessions, CSRF and active devices. **Still to come:**
  - API keys and the break-glass CLI
  - auth audit events, with the audit table in M1
  - the quota claim, with quotas in M1
  - the mobile hand-off, in M5

---

## 6. Data model (sketch)

```
users(id, oidc_issuer, oidc_subject, email, display_name, role {user|admin}, quota_bytes NULL→default,
      used_bytes, reserved_bytes, locale, created_at, last_login_at, disabled_at,
      UNIQUE (oidc_issuer, oidc_subject))

nodes(id, owner_id, parent_id NULL, kind {folder|file}, name, name_normalized,
      path ltree,                 -- materialized path of ids, for subtree queries
      current_version_id NULL,    -- files only
      starred_by …,               -- (separate table)
      created_at, modified_at, trashed_at NULL, trashed_root_id NULL,
      UNIQUE (parent_id, name_normalized) WHERE trashed_at IS NULL)

file_versions(id, node_id, version_no, blob_id, size, mime_type, original_name,
              uploaded_by, uploaded_at, client_modified_at, comment NULL)

blobs(id = sha256, size, storage_key, ref_count, created_at)

renditions(id, version_id, kind {thumb_s|thumb_m|preview_pdf|preview_jpg|video_web|poster|…},
           storage_key, mime_type, width, height, pipeline_version, created_at)

metadata(version_id, kind {exif|xmp|id3|pdf_info|ffprobe…}, data jsonb)

content_pages(version_id, page_no, source {text_layer|ocr|transcript}, lang, text,
              ocr_engine, ocr_version)           -- BM25-indexed
embeddings(version_id, page_no NULL, model, vector vchord) -- optional

shares(id, node_id, grantee_user_id, role {viewer|editor}, created_by, created_at, expires_at NULL)
links(id, node_id, token_hash, role {viewer}, allow_download, password_hash NULL,
      expires_at NULL, created_by, created_at, revoked_at NULL)

jobs(id, type, payload jsonb, status, priority, attempts, run_after, locked_by, locked_until,
     last_error, created_at)                      -- or the library's tables
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
- **Name uniqueness within a folder:** NFC-normalized and case-insensitive. Conflicts are resolved like Drive: either "file (1).pdf" or "upload as new version", chosen by the user.
- **`path ltree`** makes subtree operations cheap: permission inheritance, "trash folder", "folder size", "search in folder".

---

## 7. Storage layout

```
/data/originals/            # PRECIOUS — back this up
  blobs/ab/cd/abcdef…sha256 # content-addressed, write-once, never modified
  tmp/                      # in-flight uploads (tus), same filesystem for atomic rename
/data/derived/              # REGENERABLE — backup optional
  thumbs/{version_id}/{kind}.webp
  previews/{version_id}/…   # PDF renditions, web-playable video, posters
/data/backups/              # DB dumps (see §13)
```

- **Write path:**
  1. Stream the upload to `tmp/`, hashing it as it arrives.
  2. Verify the size and the checksum, if the client supplied one.
  3. `fsync`, then `rename()` to `blobs/…`. If the blob already exists, drop the temp file.
  4. Commit the DB transaction.
- **Blob deletion** happens only in the purge job (§12): when `ref_count` drops to 0, the blob is deleted **after** the DB commit (outbox pattern).
- **Integrity job:** a scheduled sweep finds orphaned blobs, missing blobs and checksum mismatches (bit rot), and reports to admins.
- **Export:** `dokument export --user X --to /path` writes the real folder tree with original names and mtimes. This is the "if the app dies, my files are still mine" guarantee.

---

## 8. Uploads and versioning

### 8.1 Upload flow

1. **Client → `POST /uploads` (tus create)** with the target folder, name, size and optional SHA-256.
   - The server checks permission and **reserves quota** (`reserved_bytes += size`). If it doesn't fit, it rejects immediately.
2. Client → `PATCH` chunks. The upload can resume after network loss or app restart.
3. **On completion:**
   - Hash, store the blob, and create the node or version.
   - Release the reservation and add the size to `used_bytes`.
   - Enqueue the processing jobs.
   - Emit a SignalR event and an audit event.
4. Abandoned uploads expire after 24 h, which releases the reservation and deletes the temp data.

Also:
- The client sends `client_modified_at` (the file's mtime) for display. The original bytes stay untouched.
- **Folder upload** (drag-and-drop a directory) creates the folder tree first, then uploads the files with limited parallelism.

### 8.2 Versioning

- Uploading to an existing file (explicit "upload new version", or a name collision where the user chooses "replace") → `version_no + 1`.
- **Restoring an old version** creates a *new* version pointing at the old blob. History is never rewritten.
- **Retention** (admin setting; defaults follow Google Drive): a non-current version is purged when it is **older than 30 days** or when the file has **more than 100 versions**, whichever comes first.
  - The current version is always kept.
  - Users can mark a version **"Keep forever"**, which exempts it from purging. It still counts toward quota.
- **Quota counts all versions**, deduplicated per blob per owner.
- Renditions and extracted text exist **per version**. Search indexes **only the current version**.

### 8.3 Duplicate detection

On disk, identical bytes are always stored once (§6). On top of that, **users are told** when they upload a file they already have.

- **Before transfer:** the client computes SHA-256 locally, streamed so large files don't fill memory (`hash-wasm` in a Web Worker on web; a native streaming hash on mobile).
  - It then calls `POST /uploads/check` with `[{name, size, sha256}]` for the whole batch.
- **The server answers per file:** `new`, or `duplicate` with the matching locations ("Kvitton/2025/ikea.pdf", and "in trash" if that's where it is).
- **UX:**
  - **Single file:** "You already have this file in *Kvitton/2025*." Choices: **Skip** / **Upload anyway** / **Open existing**.
  - **Batch:** one summary, "12 of 40 files are already in Dokumentsystem." Choices: **Skip duplicates** (default) / **Upload all** / **Review**.
  - Selecting "Upload anyway" creates a second node that points at the same blob. This is instant: no bytes are transferred and no extra quota is used.
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

- A job is enqueued **per version per stage**.
- Jobs are **idempotent**: re-running one overwrites its outputs.
- **Retries** with exponential backoff. After N attempts → `failed`, visible in the admin UI.
- **`pipeline_version` per stage:** bumping it (e.g. a better thumbnailer) lets an admin choose "re-run stage X for all".
- **Concurrency per job type**, configurable in the UI. Defaults are NAS-friendly, e.g. thumbnails 2, video 1, OCR 1.
- **Priorities:** interactive work (a user just uploaded) before bulk imports and rescans.
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
| JPEG/PNG/WebP/GIF/HEIC/AVIF/RAW | exiftool | **NetVips** (libvips + libheif + libraw) → WebP thumbs and a JPEG/WebP preview | OCR (ML) |
| PDF | PdfPig / exiftool | **PDFium** renders page 1 (thumb) and pages on demand | PdfPig text layer → OCR when there is none |
| DOCX/XLSX/PPTX/ODF/RTF | OpenXML SDK / exiftool | **Gotenberg** (LibreOffice) → PDF rendition → PDFium | OpenXML/ODF text extraction; fall back to text from the PDF rendition |
| Video | ffprobe | ffmpeg poster frame; **web-playable rendition only if needed** (H.264/AAC MP4, or HLS for large files). Hardware acceleration: VAAPI/QSV/NVENC. | Transcript (ML, optional) |
| Audio | ffprobe / ID3 | Embedded cover art, or a generic icon | Transcript (ML, optional) |
| Text/Markdown/code/CSV/JSON | — | Rendered first lines as thumb (optional) | Raw text (size-capped) |
| Archives (zip) | entry list | icon | entry names only |

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
  - **A list of ML URLs, in priority order, each with its own API key.** Example: `http://gaming-pc:3003` (GPU) first, `http://ml:3003` (on the NAS) as fallback.
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

- **Trash a node:** set `trashed_at` on it. Every descendant gets `trashed_root_id = node`. This is a subtree update via `path`.
- **Restore** brings back exactly the items with that `trashed_root_id`. If the original parent is gone, the node is restored to the root ("My files").
- **Trash counts toward quota**, as in Google Drive.
- **Retention:** an admin setting (default 30 days). A daily purge job **permanently deletes** expired items:
  - decrement `blobs.ref_count`
  - delete renditions
  - write an audit event
  - delete blobs after commit
- "Empty trash" and "Delete forever" are available to the owner. Both require confirmation and are audited.
- An email warning "N items will be permanently deleted in 3 days" is optional, per user preference.
- Old versions beyond the retention policy are purged by the same job.

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
| Images | Preview rendition (HEIC/RAW → WebP), zoom/pan, EXIF info panel | Same; `expo-image` |
| PDF | **pdf.js** with page thumbnails, search-in-document, OCR text-layer overlay for scans | `react-native-pdf` (or pdf.js in a WebView) |
| Office | The server's **PDF rendition** in the PDF viewer | Same |
| Video | `<video>` with HTTP Range; web rendition if the original isn't playable | `expo-video` |
| Audio | `<audio>` player with cover art; playlist for a folder | `expo-audio` (background playback) |
| Text / code / Markdown | CodeMirror (read-only) / rendered Markdown | Rendered text |
| Other | Metadata + download | Download / "open in…" |

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
packages/i18n   sv + en strings (i18next), shared by web and mobile
```

The web and mobile apps share **logic and tokens, not UI components**. The web needs:
- drag-and-drop to and from the OS
- rubber-band and shift-click selection
- keyboard shortcuts
- context menus
- virtualized grids

These are better in React DOM than through React Native Web.

### 15.2 Web (desktop + mobile browser)

- **Layout:** a left nav rail, a top search bar and a content area. On mobile it collapses to a bottom nav.
  - The nav: My files, Shared with me, Recent, Starred, Trash, and a storage meter.
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

Touch interactions replace the desktop ones: long-press instead of right-click and rubber-band selection, and tap to open instead of double-click.

### 16.4 Core components

Web components are Radix primitives styled in the shadcn/ui pattern (we own the code). The artifact's previews are the visual reference.

- **Button** (filled / tonal / outlined / text / danger) and **IconButton**. At most one filled button per view.
- **Chip:** search filters that mirror the operators ("Type: PDF" for `type:pdf`).
- **FileTypeIcon:** a Material Symbol in the `file-*` colour per MIME group. Also the thumbnail fallback. Folders stay neutral.
- **FileGrid:** folders first, then file tiles with 4:3 thumbnails. A shimmer shows while a thumbnail is pending; the thumbnail is swapped in over SignalR. Video tiles show a duration badge.
- **FileList:** a sortable name / owner / modified / size table. Under 600 px of container width, the columns fold into a second line.
- **ActionBar:** the selection count plus Share, Download, Move, Trash and More.
- **UploadTray:** one row per tus upload, in one of the states hashing, uploading, paused, done, duplicate ("Remove this copy") or failed (Retry), each with an icon and words.
- **DuplicateDialog:** the §8.3 prompt. A batch offers Skip duplicates / Upload all / Review; a single file offers Skip / Upload anyway / Open existing.
- **ShareDialog:** add people, then people with access (inherited grants are labelled and cannot be lowered), then the public link with expiry, password and download chips. A bottom sheet on phones.
- **VersionHistory:** the Versions tab of the details panel. It marks the current and "Keep forever" versions and warns "Removed in N days" per the retention policy.
- **StorageMeter:** a warning at 80 % and a critical state at 95 %, in words as well as colour.
- **NavRail**, **BottomNav**, **EmptyState** (also the drag-and-drop target that names the destination folder).

### 16.5 Content and accessibility

- Copy is in sv and en from day one. Swedish runs about 30 % longer, so never size a control to fit English text.
- Copy is in sentence case, with no exclamation marks and no emoji. Buttons are verbs ("Copy link"), and toasts confirm in the past tense ("Link copied"). Errors say what happened and what to do next.
- **Icons:** Material Symbols Rounded (Apache-2.0), outlined by default and filled when active or selected.
- Keyboard navigation everywhere: arrow keys in the grid, Space to select, Enter to open, Delete to trash. Focus is shown by a visible 2 px focus ring. Every icon button has a label.
- **Still to do before coding the UI:** a clickable mockup of the main screens (file browser, viewer, search results, admin settings), built from these components.

---

## 17. Quotas

- **Admin settings:** a default quota for new users, and a per-user override, which may be unlimited.
- **Counted:** originals of all versions owned by the user, including trash, with blobs deduplicated per user. **Not counted:** renditions and thumbnails.
- **Enforcement:** reserve at upload start and settle at completion (§8). `used_bytes` is updated in the same transaction. A nightly reconciliation job recomputes it and logs any drift.
- **UX:** a storage meter in the nav, a breakdown page (by type, largest files, trash, old versions), and email plus in-app warnings at 80% and 95%.
- **Admin view:** per-user usage, total disk usage, and the free space on the volume.

---

## 18. Audit log

- **Append-only `audit_events`**, written **in the same transaction** as the change itself.
- **Events:**
  - auth: login, logout, session revoked, API key created
  - files: upload, new version, restore version, rename, move, download, trash, restore, permanent delete
  - **Not logged:** opening a preview or viewing a file. It's too noisy for its value. Downloads of the original *are* logged.
  - sharing: share granted/changed/revoked, link created/accessed/revoked
  - admin: settings changed, quota changed, user disabled
- **Partitioned by month.** An admin setting controls retention (default 1 year).
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
- **Secrets** (SMTP password, ML API keys) are encrypted at rest in the DB using ASP.NET Data Protection keys stored in a volume.
- **Dependency hygiene:** Renovate, image scanning (Trivy) in CI, pinned base images.
- **Virus scanning: not in v1.** On a household NAS the risk doesn't justify ~3–4 GB RAM for ClamAV.
  - The pipeline has a `scan` stage hook. Later it can call an optional **ClamAV** container (`clamav/clamav`, the official image) over clamd's `INSTREAM` protocol.
  - Infected files would be quarantined and blocked from download.

---

## 22. Observability and operations

- Structured logging (Serilog → console/JSON). Log level can be changed from the admin UI.
- OpenTelemetry traces and metrics: optional OTLP export, plus a Prometheus `/metrics` endpoint.
- **Admin "System" page:** versions, extension versions, disk usage, queue stats, ML endpoint status, last backup, integrity report.
- **CLI inside the container (`dokument …`):** `admin`, `export`, `integrity-check`, `reindex`, `regenerate`, `migrate`.

---

## 23. Internationalization

- **sv and en from day one:** UI, emails and date/number formats.
- OCR languages are configurable (default `swe+eng`). The search stemmer is chosen per detected page language.

---

## 24. Roadmap

| Milestone | Scope |
|---|---|
| **M0: Foundations** | Repo/monorepo setup, CI, Postgres image, .NET solution, Aspire, OIDC login + sessions, design tokens, clickable mockup |
| **M1: Drive core** | Folders, tus uploads, downloads, rename/move, versioning, trash, quotas, audit, web file browser |
| **M2: Previews** | Worker + job queue, metadata, thumbnails (images/PDF/video), viewers (image/PDF/video/audio/text), Gotenberg Office previews |
| **M3: Search** | Text extraction, Tesseract OCR, pg_search BM25 with Swedish/English, search UI with snippets |
| **M4: Sharing** | User shares, public links, Shared with me, SMTP + in-app notifications |
| **M5: Mobile** | Expo app: browse, view, upload, search, share target, document scanner, LAN/WAN switching |
| **M6: Ops** | DB dump schedule, integrity job, admin system page, export CLI, docs, multi-arch release |
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

## 26. Open questions

1. **Video:** transcode to MP4 once, or generate HLS for large files? MP4-once is simpler and probably sufficient.
