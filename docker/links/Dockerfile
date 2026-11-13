# The links gateway (docs/design.md §9.1): the only part on the internet when Dokumentsystem itself stays private.
# Built from the repository root: docker build -f docker/links/Dockerfile .
# The final image is chiseled: no shell, no package manager, a non-root user, and nothing but the gateway and the link
# page. Run it read-only with every capability dropped (docker-compose.yml, profile "links").

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
RUN pnpm --filter @dokument/tokens build \
    && pnpm --filter @dokument/web exec vite build --config vite.link.config.ts

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS gateway
ARG TARGETARCH
ARG VERSION=0.0.0-dev
WORKDIR /repo
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/Dokument.Links/*.csproj src/Dokument.Links/
RUN dotnet restore src/Dokument.Links -a $TARGETARCH
COPY src/Dokument.Links src/Dokument.Links
RUN dotnet publish src/Dokument.Links -c Release -a $TARGETARCH --no-restore -o /app -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled
WORKDIR /app
COPY --from=gateway /app ./
COPY --from=web /repo/apps/web/dist-links ./wwwroot/
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 CMD ["dotnet", "/app/dokument-links.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "/app/dokument-links.dll"]

LABEL org.opencontainers.image.title="dokumentsystem-links" \
      org.opencontainers.image.source="https://github.com/elmaxe/dokumentsystem" \
      org.opencontainers.image.licenses="AGPL-3.0-only"
