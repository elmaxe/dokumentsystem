import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { DetailsTab } from "../components/DetailsPanel";
import { DuplicateDialog, type DuplicateMatch } from "../components/DuplicateDialog";
import { ShareDialog } from "../components/ShareDialog";
import { useSnackbar } from "../lib/snackbar";
import type { UploadItem } from "../components/UploadTray";
import { isFolder, type Sort } from "../lib/files";
import { kindOf } from "./actions";
import { initialNodes, people, versions as initialVersions, type Node } from "./data";
import { AppShell } from "./AppShell";
import { MockupContext, folderPath, type MockupState, type Route } from "./store";

export type MockupAppProps = {
  initialRoute?: Route;
  initialSelected?: string[];
  initialView?: "grid" | "list";
  initialDetails?: { open: boolean; tab: DetailsTab };
  initialUploads?: UploadItem[];
  /** Opens the share dialog for this item on load. */
  initialShare?: string;
  /** Picks these files on load, to show the duplicate prompt. */
  initialPick?: { name: string; size: number }[];
};

type Pending = { ids: string[]; matches: DuplicateMatch[]; total: number; existing: Node[] };

const tick = 300;

/**
 * The clickable mockup from docs/design.md §16.5: the file browser, viewer, search results and
 * admin settings, on fake data with local state. Nothing talks to a server.
 */
export function MockupApp({
  initialRoute = { screen: "files", place: "files", folderId: null },
  initialSelected = [],
  initialView = "grid",
  initialDetails = { open: false, tab: "info" },
  initialUploads = [],
  initialShare,
  initialPick,
}: MockupAppProps) {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const [route, setRoute] = useState<Route>(initialRoute);
  const [nodes, setNodes] = useState(initialNodes);
  const [selected, setSelected] = useState(initialSelected);
  const [view, setView] = useState(initialView);
  const [sort, setSort] = useState<Sort>({ key: "name", dir: "asc" });
  const [details, setDetails] = useState(initialDetails);
  const [uploads, setUploads] = useState(initialUploads);
  const [versions, setVersions] = useState(initialVersions);
  const [share, setShare] = useState<MockupState["share"]>({
    itemId: initialShare ?? null,
    people,
    link: { hasPassword: false, allowDownload: true },
  });
  const [pending, setPending] = useState<Pending | null>(null);
  const targets = useRef(new Map<string, { parentId: string | null; url?: string; duplicate?: boolean }>());

  const navigate = (next: Route) => {
    setRoute(next);
    if (next.screen !== "viewer") setSelected([]);
  };

  const currentFolder = route.screen === "files" && route.place === "files" ? route.folderId : null;
  const folderName = (id: string | null) =>
    id ? (nodes.find((n) => n.id === id)?.name ?? t("nav.myFiles")) : t("nav.myFiles");
  const pathOf = (node: Node) =>
    [t("nav.myFiles"), ...folderPath(nodes, node.parentId).map((n) => n.name)].join(" / ");

  const begin = (ids: string[]) =>
    setUploads((list) =>
      list.map((u) => (ids.includes(u.id) ? { ...u, state: "uploading", uploaded: 0 } : u)),
    );

  const startUpload: MockupState["startUpload"] = (files) => {
    const stamp = Date.now();
    const items: UploadItem[] = files.map((file, i) => {
      const id = `up-${stamp}-${i}`;
      targets.current.set(id, { parentId: currentFolder, url: file.url });
      return {
        id,
        name: file.name,
        kind: kindOf(file.name),
        state: "hashing",
        size: Math.max(file.size, 1),
        folder: folderName(currentFolder),
      };
    });
    setUploads((list) => [...items, ...list]);
    // Hashing happens on the device before upload; duplicates are found by hash (§8.3).
    window.setTimeout(() => {
      const existing = items.map((item) =>
        nodes.find((n) => !isFolder(n) && n.name === item.name && !n.sharedWithMe),
      );
      const dupIds = items.filter((_, i) => existing[i]).map((item) => item.id);
      begin(items.filter((item) => !dupIds.includes(item.id)).map((item) => item.id));
      if (dupIds.length > 0) {
        const found = existing.filter((n): n is Node => !!n);
        setPending({
          ids: dupIds,
          total: items.length,
          existing: found,
          matches: found.map((n) => ({
            name: n.name,
            kind: n.kind,
            path: pathOf(n),
            trashed: n.trashedDaysLeft !== undefined,
          })),
        });
      }
    }, 900);
  };

  const resolveDuplicates = (choice: "skip" | "upload" | "secondary") => {
    if (!pending) return;
    if (choice === "skip" || (choice === "secondary" && pending.total === 1)) {
      setUploads((list) => list.filter((u) => !pending.ids.includes(u.id)));
      toast(t("toasts.skippedDuplicates", { count: pending.ids.length }));
      if (choice === "secondary" && pending.existing[0]) {
        setRoute({ screen: "viewer", fileId: pending.existing[0].id, back: route });
      }
    } else {
      pending.ids.forEach((id, i) => {
        const target = targets.current.get(id);
        if (target) target.duplicate = true;
        setUploads((list) =>
          list.map((u) => (u.id === id ? { ...u, duplicateOf: pending.matches[i]?.path } : u)),
        );
      });
      begin(pending.ids);
    }
    setPending(null);
  };

  const uploadsRef = useRef(uploads);
  useEffect(() => {
    uploadsRef.current = uploads;
  });

  // Moves uploads along until they finish, then adds the new files to their folder.
  const uploading = uploads.some((u) => u.state === "uploading");
  useEffect(() => {
    if (!uploading) return;
    const timer = window.setInterval(() => {
      const finished: UploadItem[] = [];
      const next = uploadsRef.current.map((u): UploadItem => {
        if (u.state !== "uploading") return u;
        const step = u.size * (0.05 + Math.random() * 0.05);
        const uploaded = Math.min(u.size, (u.uploaded ?? 0) + step);
        if (uploaded < u.size) {
          return { ...u, uploaded, secondsLeft: ((u.size - uploaded) / step) * (tick / 1000) };
        }
        finished.push(u);
        return { ...u, uploaded, state: targets.current.get(u.id)?.duplicate ? "duplicate" : "done" };
      });
      uploadsRef.current = next;
      setUploads(next);
      if (finished.length > 0) {
        setNodes((list) => [
          ...list.filter((n) => !finished.some((u) => n.id === u.id)),
          ...finished.map(
            (u): Node => ({
              id: u.id,
              parentId: targets.current.get(u.id)?.parentId ?? null,
              name: u.name,
              kind: u.kind,
              size: u.size,
              modified: new Date().toISOString(),
              owner: "Axel",
              thumbnail: targets.current.get(u.id)?.url,
              thumbnailPending: !targets.current.get(u.id)?.url && ["pdf", "doc", "image", "video", "sheet", "slides"].includes(u.kind),
            }),
          ),
        ]);
      }
    }, tick);
    return () => window.clearInterval(timer);
  }, [uploading]);

  // Thumbnails "arrive over SignalR" a little after an upload finishes (§16.4).
  const waiting = nodes.some((n) => n.thumbnailPending && n.id.startsWith("up-"));
  useEffect(() => {
    if (!waiting) return;
    const timer = window.setTimeout(
      () => setNodes((list) => list.map((n) => (n.id.startsWith("up-") ? { ...n, thumbnailPending: false } : n))),
      2500,
    );
    return () => window.clearTimeout(timer);
  }, [waiting]);

  const picked = useRef(false);
  useEffect(() => {
    if (initialPick && !picked.current) {
      picked.current = true;
      startUpload(initialPick);
    }
  });

  const act: MockupState["act"] = (action, ids) => {
    const items = nodes.filter((n) => ids.includes(n.id));
    const first = items[0];
    switch (action) {
      case "open":
        if (first && isFolder(first)) navigate({ screen: "files", place: "files", folderId: first.id });
        else if (first) setRoute({ screen: "viewer", fileId: first.id, back: route });
        break;
      case "share":
        if (first) setShare({ ...share, itemId: first.id });
        break;
      case "download":
        toast(t("toasts.downloading", { count: items.filter((n) => !isFolder(n)).length || 1 }));
        break;
      case "star":
      case "unstar":
        setNodes((list) => list.map((n) => (ids.includes(n.id) ? { ...n, starred: action === "star" } : n)));
        toast(t(action === "star" ? "toasts.starred" : "toasts.unstarred", { count: ids.length }));
        break;
      case "details":
        setSelected(ids.slice(0, 1));
        setDetails({ open: true, tab: "info" });
        break;
      case "trash": {
        const before = nodes;
        setNodes((list) => list.map((n) => (ids.includes(n.id) ? { ...n, trashedDaysLeft: 30 } : n)));
        setSelected([]);
        toast(t("toasts.trashed", { count: ids.length }), {
          label: t("common.undo"),
          onClick: () => setNodes(() => before),
        });
        break;
      }
      case "restore":
        setNodes((list) =>
          list.map((n) => (ids.includes(n.id) ? { ...n, trashedDaysLeft: undefined } : n)),
        );
        setSelected([]);
        toast(t("toasts.restored", { count: ids.length }));
        break;
      case "deleteForever":
        setNodes((list) => list.filter((n) => !ids.includes(n.id)));
        setSelected([]);
        break;
      default:
        toast(t("toasts.notInMockup"));
    }
  };

  const state: MockupState = {
    route,
    navigate,
    nodes,
    setNodes: (update) => setNodes(update),
    selected,
    setSelected,
    view,
    setView,
    sort,
    setSort,
    details,
    setDetails,
    uploads,
    setUploads: (update) => setUploads(update),
    startUpload,
    share,
    setShare,
    versions,
    setVersions,
    act,
  };

  const mockCandidates = [
    { id: "maja", name: "Maja Lindqvist", email: "maja@example.com" },
    { id: "olle", name: "Olle Eriksson", email: "olle@example.com" },
  ];
  const shareItem = useMemo(() => nodes.find((n) => n.id === share.itemId), [nodes, share.itemId]);

  return (
    <MockupContext value={state}>
      <AppShell />
      <ShareDialog
        open={!!shareItem}
        onOpenChange={(open) => !open && setShare({ ...share, itemId: null })}
        name={shareItem?.name ?? ""}
        people={share.people}
        candidates={mockCandidates}
        onAdd={(candidate, role) =>
          setShare({
            ...share,
            people: [...share.people, { id: candidate.id, name: candidate.name, email: candidate.email ?? "", role, removable: true }],
          })
        }
        onRoleChange={(person, role) =>
          setShare({
            ...share,
            people:
              role === "remove"
                ? share.people.filter((p) => p !== person)
                : share.people.map((p) => (p === person ? { ...p, role, removable: true } : p)),
          })
        }
        link={share.link}
        onLinkToggle={(on) => setShare({ ...share, link: on ? { hasPassword: false, allowDownload: true } : null })}
        onCopyLink={() => toast(t("toasts.linkCopied"))}
      />
      <DuplicateDialog
        open={!!pending}
        onOpenChange={(open) => !open && resolveDuplicates("skip")}
        total={pending?.total ?? 0}
        duplicates={pending?.matches ?? []}
        onSkip={() => resolveDuplicates("skip")}
        onUploadAll={() => resolveDuplicates("upload")}
        onSecondary={() => resolveDuplicates("secondary")}
      />
    </MockupContext>
  );
}
