import type { ShareRole } from "@dokument/api";
import { LinkSettingsDialog, ShareDialog, useSnackbar, type LinkSettingsChange } from "@dokument/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { sessionQuery } from "../api";
import { accessQuery, ApiError, drive, errorMessage, linkQuery, peopleQuery } from "./drive";
import { accessPeople, invalidateSharing } from "./sharing";

/** The moment a link stops working, for the last day it should work: the start of the next day, here. */
const endOf = (day: string) => {
  const [year = 0, month = 1, date = 1] = day.split("-").map(Number);
  return new Date(year, month - 1, date + 1).toISOString();
};

/** Who can see a file or folder, and for its owner the way to change that and its public link (§9). */
export function ShareNodeDialog({
  node,
  onClose,
}: {
  node: { id: string; name: string; kind?: string } | null;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  const you = useQuery(sessionQuery).data?.user.id;
  const access = useQuery({ ...accessQuery(node?.id ?? ""), enabled: !!node });
  const [typed, setTyped] = useState("");
  const people = useQuery({ ...peopleQuery(typed), enabled: !!node && !!typed });
  const [error, setError] = useState<string>();
  const owner = access.data?.yourAccess === "owner";
  const link = useQuery({ ...linkQuery(node?.id ?? ""), enabled: !!node && owner });
  const [settings, setSettings] = useState(false);
  const [linkError, setLinkError] = useState<string>();
  const [busy, setBusy] = useState(false);

  const refused = (problem: unknown) =>
    problem instanceof ApiError && problem.problem.code === "forbidden"
      ? t("errors.ownerOnly")
      : problem instanceof ApiError && problem.problem.code === "invalidRequest" && problem.problem.detail
        ? problem.problem.detail
        : errorMessage(t, problem);

  const change = async (run: () => Promise<unknown>, done: string) => {
    setError(undefined);
    try {
      await run();
      toast(done);
    } catch (problem) {
      setError(refused(problem));
    }
    await invalidateSharing(queryClient);
  };

  const copy = async () => {
    const url = link.data?.url;
    if (!url) return;
    try {
      await navigator.clipboard.writeText(url);
      toast(t("toasts.linkCopied"));
    } catch {
      toast(t("errors.generic"));
    }
  };

  const toggleLink = async (on: boolean) => {
    if (!node) return;
    setError(undefined);
    try {
      if (on) await drive.saveLink(node.id, { allowDownload: true, expiresAt: null, password: null, removePassword: false });
      else await drive.revokeLink(node.id);
      toast(t(on ? "toasts.linkOn" : "toasts.linkOff"));
    } catch (problem) {
      setError(refused(problem));
    }
    await invalidateSharing(queryClient);
  };

  const saveSettings = async (change: LinkSettingsChange) => {
    if (!node) return;
    setLinkError(undefined);
    setBusy(true);
    try {
      await drive.saveLink(node.id, {
        allowDownload: change.allowDownload,
        expiresAt: change.lastDay ? endOf(change.lastDay) : null,
        password: change.password ?? null,
        removePassword: change.removePassword,
      });
      toast(t("toasts.linkSaved"));
      setSettings(false);
    } catch (problem) {
      setLinkError(
        problem instanceof ApiError && problem.status === 400
          ? (Object.values((problem.problem as { errors?: Record<string, string[]> }).errors ?? {})[0]?.[0] ??
              problem.problem.detail ??
              errorMessage(t, problem))
          : errorMessage(t, problem),
      );
    } finally {
      setBusy(false);
    }
    await invalidateSharing(queryClient);
  };

  if (!node) return null;
  const role = (r: ShareRole) => t(`share.roles.${r}`).toLowerCase();
  if (settings && link.data) {
    return (
      <LinkSettingsDialog
        open
        onOpenChange={(open) => {
          if (open) return;
          setLinkError(undefined);
          setSettings(false);
        }}
        name={node.name}
        isFolder={node.kind === "folder"}
        link={link.data}
        onCopy={() => void copy()}
        onSave={(change) => void saveSettings(change)}
        onDelete={() => {
          setSettings(false);
          void toggleLink(false);
        }}
        error={linkError}
        busy={busy}
      />
    );
  }
  return (
    <ShareDialog
      open
      onOpenChange={(open) => {
        if (open) return;
        setError(undefined);
        setTyped("");
        onClose();
      }}
      name={node.name}
      people={access.data ? accessPeople(access.data, you) : []}
      canManage={access.data?.yourAccess === "owner"}
      candidates={(people.data ?? []).map((p) => ({ id: p.id, name: p.name, email: p.email }))}
      onQueryChange={setTyped}
      onAdd={(candidate, r) =>
        void change(() => drive.share(node.id, candidate.id, r), t("toasts.shareAdded", { name: candidate.name }))
      }
      onRoleChange={(person, r) => {
        const entry = access.data?.people.find((p) => p.person.id === person.id);
        if (!entry || !person.id) return;
        const personId = person.id;
        if (r === "remove") {
          if (entry.shareId) {
            const shareId = entry.shareId;
            void change(() => drive.removeShare(shareId), t("toasts.shareRemoved", { name: person.name }));
          }
          return;
        }
        // A role from a folder above is raised with a share here; one here is changed in place.
        void change(
          () => (entry.shareId ? drive.changeShare(entry.shareId, r) : drive.share(node.id, personId, r)),
          t("toasts.shareChanged", { name: person.name, role: role(r) }),
        );
      }}
      error={error}
      link={owner && link.isSuccess ? link.data : undefined}
      onLinkToggle={(on) => void toggleLink(on)}
      onCopyLink={() => void copy()}
      onLinkSettings={() => setSettings(true)}
    />
  );
}
