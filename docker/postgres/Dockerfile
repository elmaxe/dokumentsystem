# Dokumentsystem Postgres: official Postgres + ParadeDB pg_search (BM25) + pgvector + VectorChord.
# See docs/design.md §13.1. Extension versions are pinned here and checked by the server on startup.
#
# The extensions are copied out of the upstream images, which are published for amd64 and arm64,
# so this image is multi-arch without per-arch download URLs.

ARG PG_MAJOR=17
ARG PG_SEARCH_VERSION=0.20.6
ARG VCHORD_VERSION=0.5.3

FROM paradedb/paradedb:${PG_SEARCH_VERSION}-pg${PG_MAJOR} AS pg_search
FROM tensorchord/vchord-postgres:pg${PG_MAJOR}-v${VCHORD_VERSION} AS vchord

FROM postgres:${PG_MAJOR}-bookworm
ARG PG_MAJOR
ARG PG_SEARCH_VERSION
ARG VCHORD_VERSION

# pg_search links against the ICU build that ParadeDB ships in /usr/local/lib.
COPY --from=pg_search /usr/local/lib/libicu*.so.76* /usr/local/lib/
COPY --from=pg_search /usr/lib/postgresql/${PG_MAJOR}/lib/pg_search.so /usr/lib/postgresql/${PG_MAJOR}/lib/
COPY --from=pg_search /usr/share/postgresql/${PG_MAJOR}/extension/pg_search* /usr/share/postgresql/${PG_MAJOR}/extension/

COPY --from=vchord /usr/lib/postgresql/${PG_MAJOR}/lib/vector.so /usr/lib/postgresql/${PG_MAJOR}/lib/vchord.so /usr/lib/postgresql/${PG_MAJOR}/lib/
COPY --from=vchord /usr/share/postgresql/${PG_MAJOR}/extension/vector* /usr/share/postgresql/${PG_MAJOR}/extension/vchord* /usr/share/postgresql/${PG_MAJOR}/extension/

RUN ldconfig \
 && sed -ri "s/^#?shared_preload_libraries = .*/shared_preload_libraries = 'pg_search,vchord'/" /usr/share/postgresql/postgresql.conf.sample \
 && grep -q "^shared_preload_libraries = 'pg_search,vchord'" /usr/share/postgresql/postgresql.conf.sample

LABEL org.opencontainers.image.title="dokumentsystem-postgres" \
      org.opencontainers.image.source="https://github.com/elmaxe/dokumentsystem" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      dokumentsystem.pg_search.version="${PG_SEARCH_VERSION}" \
      dokumentsystem.vchord.version="${VCHORD_VERSION}"
