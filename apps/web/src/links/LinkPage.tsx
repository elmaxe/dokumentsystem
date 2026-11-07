import { linkUrls, type LinkFolder, type LinkNode, type LinkNodeDetails, type LinkPage as LinkPageDto } from "@dokument/api";
import { formatDuration } from "@dokument/core";
import {
  Breadcrumbs,
  Button,
  EmptyState,
  FileGrid,
  FileList,
  FileViewer,
  Icon,
  IconButton,
  kindOf,
  sortItems,
  TextField,
  dayLabel,
  lastDay,
  useFormat,
  type FileItem,
  type Sort,
} from "@dokument/ui";
import { queryOptions, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, serverInfoQuery, sessionQuery } from "../api";
import { ApiError, download, send, unwrap } from "../files/drive";
import { previewOf, type PreviewUrls } from "../files/preview";

export type LinkSearch = { folder?: string; file?: string };

type LinkItem = FileItem & { node: LinkNode };

const pageQuery = (token: string) =>
  queryOptions({
    queryKey: ["link", token],
    queryFn: (): Promise<LinkPageDto> => unwrap(api.GET("/api/v1/links/{token}", { params: { path: { token } } })),
    staleTime: Infinity,
  });

const folderQuery = (token: string, folderId: string | null) =>
  queryOptions({
    queryKey: ["link", token, "folder", folderId],
    queryFn: (): Promise<LinkFolder> =>
      unwrap(
        api.GET("/api/v1/links/{token}/nodes", {
          params: { path: { token }, query: folderId ? { folderId } : {} },
        }),
      ),
  });

const nodeQuery = (token: string, nodeId: string) =>
  queryOptions({
    queryKey: ["link", token, "node", nodeId],
    queryFn: (): Promise<LinkNodeDetails> =>
      unwrap(api.GET("/api/v1/links/{token}/nodes/{nodeId}", { params: { path: { token, nodeId } } })),
  });

/** What anyone with a public link sees, without signing in (§9): the file, or the folder to browse and open files in. */
export function LinkPage({ token, folder, file }: { token: string } & LinkSearch) {
  const { t } = useTranslation();
  const page = useQuery({ ...pageQuery(token), retry: false });
  const problem = page.error instanceof ApiError ? page.error.problem.code : undefined;

  return (
    <div className="flex min-h-dvh flex-col bg-surface text-on-surface">
      <Header />
      {page.isPending ? null : page.data ? (
        <Opened token={token} page={page.data} folder={folder} file={file} />
      ) : problem === "passwordRequired" ? (
        <PasswordForm token={token} />
      ) : (
        <Centered>
          <EmptyState
            icon="link_off"
            title={problem === "linkGone" ? t("link.goneTitle") : t("errors.generic")}
            text={problem === "linkGone" ? t("link.goneText") : undefined}
            className="max-w-md bg-surface-container-low"
          />
        </Centered>
      )}
    </div>
  );
}

function Header() {
  const { t } = useTranslation();
  const info = useQuery(serverInfoQuery);
  // Also sets the CSRF token for someone signed in, which the password form's POST needs.
  const session = useQuery(sessionQuery);
  return (
    <header className="flex h-16 shrink-0 items-center gap-2 border-b border-outline-variant px-4 sm:px-6">
      <img src="/favicon.svg" alt="" width={24} height={24} className="size-6" />
      <span className="flex-1 truncate text-title-md">{info.data?.name}</span>
      {session.data ? (
        <Link to="/" className="font-medium text-primary no-underline hover:underline">
          {t("link.openApp")}
        </Link>
      ) : (
        session.isSuccess && (
          <Link to="/signin" className="font-medium text-primary no-underline hover:underline">
            {t("link.signIn")}
          </Link>
        )
      )}
    </header>
  );
}

const Centered = ({ children }: { children: ReactNode }) => (
  <main className="flex flex-1 items-center justify-center p-4">{children}</main>
);

function PasswordForm({ token }: { token: string }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    setBusy(true);
    setError(undefined);
    try {
      await send(api.POST("/api/v1/links/{token}/unlock", { params: { path: { token } }, body: { password } }));
      await queryClient.invalidateQueries({ queryKey: ["link", token] });
    } catch (problem) {
      setError(
        problem instanceof ApiError && problem.status === 429
          ? t("link.tooMany")
          : problem instanceof ApiError && problem.problem.code === "wrongPassword"
            ? t("link.wrongPassword")
            : t("errors.generic"),
      );
    } finally {
      setBusy(false);
    }
  };

  return (
    <Centered>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          if (password) void submit();
        }}
        className="flex w-full max-w-sm flex-col gap-4 rounded-lg bg-surface-container-low p-6"
      >
        <Icon name="lock" size={28} className="self-center text-primary" />
        <h1 className="m-0 text-center text-title-lg">{t("link.passwordTitle")}</h1>
        <p className="m-0 text-center text-body-md text-on-surface-variant">{t("link.passwordText")}</p>
        <TextField
          type="password"
          label={t("link.password")}
          value={password}
          autoFocus
          autoComplete="current-password"
          onChange={(event) => setPassword(event.target.value)}
          error={error}
        />
        <Button type="submit" disabled={busy || !password}>
          {t("link.open")}
        </Button>
      </form>
    </Centered>
  );
}

function Opened({ token, page, folder, file }: { token: string; page: LinkPageDto } & LinkSearch) {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const format = useFormat();
  const urls = linkUrls(token);
  const root = page.node;
  const isFolder = root.kind === "folder";
  const folderId = isFolder ? (folder ?? null) : null;
  const listing = useQuery({ ...folderQuery(token, folderId), enabled: isFolder });
  const opened = useQuery({ ...nodeQuery(token, file ?? ""), enabled: !!file && file !== root.id });
  const [view, setView] = useState<"grid" | "list">("grid");
  const [sort, setSort] = useState<Sort>({ key: "name", dir: "asc" });

  const toItem = (node: LinkNode): LinkItem => ({
    id: node.id,
    name: node.name,
    kind: node.kind === "folder" ? "folder" : kindOf(node.name, node.mimeType),
    size: node.size ?? undefined,
    contents: node.contents ?? undefined,
    modified: node.modifiedAt,
    owner: page.sharedBy,
    thumbnail: node.thumbnail === "ready" ? urls.thumbnail(node.id) : undefined,
    thumbnailPending: node.thumbnail === "pending",
    duration: node.media?.durationSeconds != null ? formatDuration(node.media.durationSeconds) : undefined,
    node,
  });

  const go = (search: LinkSearch) => void navigate({ to: "/s/$token", params: { token }, search });
  const items = sortItems((listing.data?.items ?? []).map(toItem), sort, i18n.language);
  const meta = [
    t("link.sharedBy", { name: page.sharedBy }),
    ...(page.expiresAt ? [t("link.until", { date: dayLabel(lastDay(page.expiresAt), i18n.language) })] : []),
  ].join(" · ");

  // The file being looked at: the link's own, one in the folder on screen, or one opened from an address.
  const shown = file
    ? file === root.id
      ? root
      : (listing.data?.items.find((n) => n.id === file) ?? opened.data?.node)
    : undefined;
  const files = items.filter((item) => item.kind !== "folder");
  const index = shown ? files.findIndex((item) => item.id === shown.id) : -1;
  const previewUrls: PreviewUrls = {
    preview: (nodeId, _version, options) => urls.preview(nodeId, options),
    thumbnail: (nodeId) => urls.thumbnail(nodeId),
    waveform: (nodeId) => urls.waveform(nodeId),
    seekPreviews: (nodeId) => urls.seekPreviews(nodeId),
  };
  const save = page.allowDownload ? (id: string) => download(urls.content(id)) : undefined;

  if (shown) {
    const item = toItem(shown);
    const step = (offset: number) => {
      const next = files[index + offset];
      if (next) go({ folder, file: next.id });
    };
    return (
      <div className="fixed inset-0 z-40">
        <FileViewer
          key={item.id}
          item={item}
          preview={previewOf(shown, null, false, previewUrls)}
          onClose={() => go({ folder: folder ?? opened.data?.node.parentId ?? undefined })}
          onDownload={save && (() => save(item.id))}
          onPrevious={index > 0 ? () => step(-1) : undefined}
          onNext={index >= 0 && index < files.length - 1 ? () => step(1) : undefined}
        />
      </div>
    );
  }

  if (!isFolder) {
    const item = toItem(root);
    return (
      <Centered>
        <div className="flex w-full max-w-md flex-col items-center gap-4 rounded-lg bg-surface-container-low p-6 text-center">
          {item.thumbnail ? (
            <img src={item.thumbnail} alt="" className="max-h-64 max-w-full rounded-md object-contain" />
          ) : (
            <Icon name="description" size={48} className="text-primary" />
          )}
          <h1 className="m-0 text-title-lg [overflow-wrap:anywhere]">{root.name}</h1>
          <p className="m-0 text-body-md text-on-surface-variant">
            {[root.size != null ? format.bytes(root.size) : null, meta].filter(Boolean).join(" · ")}
          </p>
          <div className="flex flex-wrap justify-center gap-2">
            <Button icon="visibility" onClick={() => go({ file: root.id })}>
              {t("link.file")}
            </Button>
            {save && (
              <Button variant="outlined" icon="download" onClick={() => save(root.id)}>
                {t("actions.download")}
              </Button>
            )}
          </div>
          {!save && <p className="m-0 text-body-sm text-on-surface-variant">{t("link.noDownload")}</p>}
        </div>
      </Centered>
    );
  }

  const viewProps = {
    items,
    selected: [] as string[],
    onSelectedChange: () => {},
    onOpen: (item: LinkItem) => go(item.kind === "folder" ? { folder: item.id } : { folder, file: item.id }),
  };

  return (
    <main className="mx-auto flex w-full max-w-7xl flex-1 flex-col gap-4 px-4 py-6 sm:px-6">
      <div className="flex flex-wrap items-end gap-2">
        <div className="flex min-w-0 flex-1 flex-col gap-1">
          {listing.data && (
            <Breadcrumbs
              path={listing.data.path.map((p) => ({ id: p.id, name: p.name }))}
              onNavigate={(id) => go(id === root.id ? {} : { folder: id })}
            />
          )}
          <p className="m-0 text-body-md text-on-surface-variant">{meta}</p>
        </div>
        <IconButton
          icon={view === "grid" ? "view_list" : "grid_view"}
          label={view === "grid" ? t("files.listView") : t("files.gridView")}
          onClick={() => setView(view === "grid" ? "list" : "grid")}
        />
      </div>
      {listing.isError ? (
        <EmptyState
          icon="error"
          title={
            listing.error instanceof ApiError && listing.error.status === 404
              ? t("errors.notFound")
              : t("errors.generic")
          }
          className="mt-4"
        />
      ) : listing.isPending ? null : items.length === 0 ? (
        <EmptyState icon="folder_open" title={t("link.empty")} className="mt-4" />
      ) : view === "grid" ? (
        <FileGrid {...viewProps} />
      ) : (
        <FileList {...viewProps} sort={sort} onSortChange={setSort} />
      )}
      <p className="m-0 mt-auto flex items-center justify-center gap-2 pt-6 text-body-sm text-on-surface-variant">
        <Icon name="info" size={16} />
        {page.allowDownload ? t("link.noAccount") : `${t("link.noAccount")} ${t("link.noDownload")}`}
      </p>
    </main>
  );
}
