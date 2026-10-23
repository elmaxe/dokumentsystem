# Dokumentsystem: one image, two roles (ROLE=server or ROLE=worker). See docs/design.md §3.1 and §4.
# Build stages run on the build machine's architecture and cross-compile, so amd64 and arm64 builds are fast.

ARG NODE_VERSION=22
ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM node:${NODE_VERSION}-bookworm-slim AS web
WORKDIR /repo
RUN corepack enable
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml turbo.json tsconfig.base.json ./
COPY apps/web/package.json apps/web/
COPY packages/api/package.json packages/api/
COPY packages/core/package.json packages/core/
COPY packages/i18n/package.json packages/i18n/
COPY packages/tokens/package.json packages/tokens/
COPY packages/ui/package.json packages/ui/
RUN pnpm install --frozen-lockfile
COPY docs/design-system docs/design-system
COPY apps/web apps/web
COPY packages packages
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
RUN dotnet restore src/Dokument.Host -a $TARGETARCH
COPY src src
RUN dotnet publish src/Dokument.Host -c Release -a $TARGETARCH --no-restore -o /app \
      -p:Version=$VERSION -p:OpenApiGenerateDocuments=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}
WORKDIR /app
COPY --from=server /app ./
COPY --from=web /repo/apps/web/dist ./wwwroot/
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ROLE=server
EXPOSE 8080
# The built-in non-root "app" user. Compose overrides it with PUID:PGID so files on the NAS get the right owner.
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 CMD ["dotnet", "/app/dokument.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "/app/dokument.dll"]

LABEL org.opencontainers.image.title="dokumentsystem" \
      org.opencontainers.image.source="https://github.com/elmaxe/dokumentsystem" \
      org.opencontainers.image.licenses="AGPL-3.0-only"
