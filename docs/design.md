# Dokumentsystem — Design Document

> Status: **Draft v0.1** · Last updated: 2026-10-21
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
| Web | Vite + React + Tailwind + Radix/shadcn |
| Mobile | Expo / React Native + NativeWind |
| Design | Material 3–inspired, Immich / Google Photos look; shared design tokens |
| Virus scanning | Not in v1. A pipeline hook allows optional ClamAV later. |
| WebDAV | Later (post v1) |
| License | AGPL-3.0, matching Immich and compatible with pg_search and VectorChord |

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

### 3.2 Solution layout (.NET)

```
src/
  Dokument.Domain/          entities, value objects, domain rules (no I/O)
  Dokument.Application/     use cases, permissions, quota logic, job definitions
  Dokument.Infrastructure/  EF Core, storage, queue, SMTP, ML client, Gotenberg client
  Dokument.Server/          ASP.NET Core host (API, tus, SignalR, auth)
  Dokument.Worker/          job handlers (thumbnails, extraction, purge, backup …)
  Dokument.Host/            single entrypoint; picks role(s) from ROLE env var
  Dokument.AppHost/         .NET Aspire for local dev orchestration
tests/
  *.UnitTests, *.IntegrationTests (Testcontainers + our Postgres image)
ml/                         ML service (Python, see §10)
web/, mobile/, packages/    frontend monorepo (see §15)
```

---

## 4. Deployment (NAS)

### 4.1 Compose sketch

```yaml
services:
  server:
    image: ghcr.io/<org>/dokumentsystem:${VERSION}
    environment: { ROLE: server, PUID: 1000, PGID: 1000, ... }
    volumes:
      - ${ORIGINALS_PATH}:/data/originals
      - ${DERIVED_PATH}:/data/derived
      - ${BACKUP_PATH}:/data/backups
    ports: ["8080:8080"]
    depends_on: [postgres]
  worker:
    image: ghcr.io/<org>/dokumentsystem:${VERSION}
    environment: { ROLE: worker, ... }
    volumes:
      - ${ORIGINALS_PATH}:/data/originals:ro
      - ${DERIVED_PATH}:/data/derived
      - ${BACKUP_PATH}:/data/backups
  ml:            # optional; can instead run on another machine
    image: ghcr.io/<org>/dokumentsystem-ml:${VERSION}   # -cuda / -openvino variants
  gotenberg:     # optional; enables Office previews
    image: gotenberg/gotenberg:8
  postgres:
    image: ghcr.io/<org>/dokumentsystem-postgres:${VERSION}
    volumes: [ "${DB_PATH}:/var/lib/postgresql/data" ]
```

### 4.2 NAS requirements

- **Multi-arch images** (amd64 + arm64). Many Synology and QNAP boxes are ARM.
- **`PUID`/`PGID`**, so files on the mounted volume get the owner the user expects. Containers run as non-root.
- **Low-resource profile:**
  - One worker with low concurrency.
  - Gotenberg and ML optional, so the base install fits in about 1 GB RAM.
  - Office previews and OCR are disabled until their services are configured.
- **The DB volume must be on local disk**, not a network share. Postgres on SMB/NFS is a corruption risk.
- Healthchecks on every service. `/health/live` and `/health/ready` endpoints.

### 4.3 Configuration

- **Environment variables, for bootstrap only:** DB connection, OIDC issuer/client, paths, `PUBLIC_URL`, `ROLE`.
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
  - Web: the server performs the flow (confidential client).
  - Mobile: the app opens the system browser and redirects back to a custom scheme. The server then exchanges the code.
- **Sessions:** after a successful OIDC login, the server issues **its own session**:
  - Web: httpOnly, Secure, SameSite=Lax cookie, with CSRF protection on mutations.
  - Mobile: an opaque bearer token stored in the secure enclave/keystore.
  - Sessions can be listed and revoked by the user ("active devices") and by admins.
- **API keys:** personal, scoped (read-only / read-write), for scripts, rclone and WebDAV later.
- **User provisioning:** users are created on first login (JIT), with a stable identity from `iss` + `sub`.
  - An optional **allow-list**: only users in IdP group `dokument-users` may log in.
  - `dokument-admins` group → admin role. Mapping is re-evaluated on each login.
  - An optional claim sets the quota tier.
- **Break-glass:** a CLI command in the container (`dokument admin reset-oidc` / `create-admin-token`) for when the IdP is broken.

---

## 6. Data model (sketch)

```
users(id, oidc_issuer, oidc_subject, email, display_name, role, quota_bytes NULL→default,
      used_bytes, reserved_bytes, locale, created_at, disabled_at)

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
sessions(id, user_id, kind, token_hash, device_name, created_at, last_seen_at, revoked_at)
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
- **Retention:** an admin setting, e.g. *keep the last N versions* and/or *versions newer than X days*. The current version is always kept.
- **Quota counts all versions**, deduplicated per blob per owner.
- Renditions and extracted text exist **per version**. Search indexes **only the current version**.

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
- **Public links:** viewer-only, with optional password, expiry and allow-download flag. Access goes through `/s/{token}` and is rate-limited. Every access is audited.
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
- **Cost:** we ship and maintain **our own Postgres image**, as Immich does.
  - Base: official `postgres` + `pg_search` + `pgvector` + `vchord`.
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
apps/web        Vite + React + TanStack Router/Query (desktop-class file manager, responsive)
apps/mobile     Expo (dev builds, not Expo Go) + Expo Router + NativeWind
packages/api    TS client generated from the server OpenAPI spec (openapi-typescript / orval)
packages/core   shared hooks, formatting, upload queue logic, permission helpers
packages/tokens design tokens → Tailwind config (web) + NativeWind config (mobile)
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

**Look and feel:** Material 3–inspired, close to Immich and Google Photos.
- Calm neutral surfaces, one primary accent, rounded corners (12–16 px on cards), generous whitespace.
- **Dark mode is first-class.**

- **Tokens (`packages/tokens`):**
  - color roles (`primary`, `on-primary`, `surface`, `surface-container-{low,high}`, `outline`, `error`, …) for light and dark
  - type scale
  - spacing (4 px grid)
  - radii, elevation, motion durations

  Generated from one source into the Tailwind theme (web) and NativeWind (mobile).
- **Web components:** Radix primitives styled in the shadcn/ui pattern (we own the code). Includes button, icon button, chip, menu, dialog, sheet, tooltip, toast, tabs, data grid/list, breadcrumbs, progress, avatar and empty states.
- **Icons:** Material Symbols (Apache-2.0), with consistent weight and size tokens.
- **File-type visuals:** a coloured type badge and icon per MIME group (PDF red, doc blue, sheet green, …), used as the thumbnail fallback.
- **Accessibility:** keyboard navigation everywhere, visible focus, WCAG AA contrast in both themes, `prefers-reduced-motion`.
- **Deliverable before coding the UI:** a clickable mockup of the main screens (file browser, viewer, search results, share dialog, admin settings) to settle the look.

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
  - files: upload, new version, restore version, rename, move, download, preview open (configurable), trash, restore, permanent delete
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
| **Later** | WebDAV, offline files, push via ntfy/UnifiedPush, groups, optional ClamAV |

---

## 25. Open questions

1. **Project name** (the repo is `dokumentsystem`; is that the product name too?).
2. **Version retention defaults:** keep the last N versions and/or versions newer than X days? Google's default is 100 versions / 30 days.
3. **Should "preview opened" be an audit event** by default? It's useful, but noisy.
4. **Duplicate detection UX:** only dedup storage silently, or also tell users "you already have this file in /X"?
5. **Video:** transcode to MP4 once, or generate HLS for large files? MP4-once is simpler and probably sufficient.
6. **Should public links allow uploads** ("file request" folders, e.g. for family to drop photos)?
