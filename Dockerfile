# Dokumentsystem: one image, two roles (ROLE=server or ROLE=worker). See docs/design.md §3.1 and §4.
# Build stages run on the build machine's architecture and cross-compile, so amd64 and arm64 builds are fast.

ARG NODE_VERSION=22
ARG DOTNET_VERSION=10.0

# The Word viewer's engine (docs/design.md §14): Scriptor, built from the commit pinned in packages/scriptor. Its own
# stage, so it's only rebuilt when that pin or the build script changes.
FROM --platform=$BUILDPLATFORM node:${NODE_VERSION}-bookworm-slim AS nodejs
FROM --platform=$BUILDPLATFORM rust:1-bookworm AS scriptor
COPY --from=nodejs /usr/local/bin/node /usr/local/bin/node
RUN rustup target add wasm32-unknown-unknown && cargo install wasm-pack --locked --version 0.15.1
WORKDIR /repo/packages/scriptor
COPY packages/scriptor/package.json packages/scriptor/build.mjs ./
RUN SCRIPTOR_REQUIRED=1 node build.mjs

FROM --platform=$BUILDPLATFORM node:${NODE_VERSION}-bookworm-slim AS web
WORKDIR /repo
RUN corepack enable
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml turbo.json tsconfig.base.json ./
COPY apps/web/package.json apps/web/
COPY packages/api/package.json packages/api/
COPY packages/core/package.json packages/core/
COPY packages/i18n/package.json packages/i18n/
COPY packages/scriptor/package.json packages/scriptor/
COPY packages/tokens/package.json packages/tokens/
COPY packages/ui/package.json packages/ui/
RUN pnpm install --frozen-lockfile
COPY docs/design-system docs/design-system
COPY apps/web apps/web
COPY packages packages
COPY --from=scriptor /repo/packages/scriptor/dist packages/scriptor/dist
# Not turbo's ^build: that would also build packages/ui's Storybook, which the image doesn't serve.
RUN pnpm --filter @dokument/tokens build && pnpm --filter @dokument/web build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS server
ARG TARGETARCH
ARG VERSION=0.0.0-dev
WORKDIR /repo
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/Dokument.Domain/*.csproj src/Dokument.Domain/
COPY src/Dokument.Application/*.csproj src/Dokument.Application/
COPY src/Dokument.Infrastructure/*.csproj src/Dokument.Infrastructure/
COPY src/Dokument.Server/*.csproj src/Dokument.Server/
COPY src/Dokument.Worker/*.csproj src/Dokument.Worker/
COPY src/Dokument.Host/*.csproj src/Dokument.Host/
RUN dotnet restore src/Dokument.Host -a $TARGETARCH -p:UseSystemLibvips=true
COPY src src
RUN dotnet publish src/Dokument.Host -c Release -a $TARGETARCH --no-restore -o /app \
      -p:Version=$VERSION -p:OpenApiGenerateDocuments=false -p:UseSystemLibvips=true

# The app without the web UI: what Aspire runs as the dev worker (`--target runtime`), so it has the same tools.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
# libvips for thumbnails (docs/design.md §10.3), with libde265 so HEIC photos decode. PDFium ships with the app.
# exiftool reads metadata and Tesseract recognizes text in scans, in Swedish and English (§13.2). FFmpeg makes video
# posters and playable copies (§14); Debian's is built with zimg, for tone mapping HDR.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libvips42t64 libheif-plugin-libde265 \
        libimage-exiftool-perl tesseract-ocr tesseract-ocr-swe tesseract-ocr-eng ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=server /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ROLE=server
EXPOSE 8080
# The built-in non-root "app" user. Compose overrides it with PUID:PGID so files on the NAS get the right owner.
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 CMD ["dotnet", "/app/dokument.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "/app/dokument.dll"]

FROM runtime
COPY --from=web /repo/apps/web/dist ./wwwroot/

LABEL org.opencontainers.image.title="dokumentsystem" \
      org.opencontainers.image.source="https://github.com/elmaxe/dokumentsystem" \
      org.opencontainers.image.licenses="AGPL-3.0-only"
