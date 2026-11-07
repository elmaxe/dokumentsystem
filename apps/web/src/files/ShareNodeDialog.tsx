import type { ShareRole } from "@dokument/api";
import { ShareDialog, useSnackbar } from "@dokument/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { sessionQuery } from "../api";
import { accessQuery, ApiError, drive, errorMessage, peopleQuery } from "./drive";
import { accessPeople, invalidateSharing } from "./sharing";

/** Who can see a file or folder, and for its owner the way to change that (§9). */
export function ShareNodeDialog({ node, onClose }: { node: { id: string; name: string } | null; onClose: () => void }) {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  const you = useQuery(sessionQuery).data?.user.id;
  const access = useQuery({ ...accessQuery(node?.id ?? ""), enabled: !!node });
  const [typed, setTyped] = useState("");
  const people = useQuery({ ...peopleQuery(typed), enabled: !!node && !!typed });
  const [error, setError] = useState<string>();

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

  if (!node) return null;
  const role = (r: ShareRole) => t(`share.roles.${r}`).toLowerCase();
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
    />
  );
}
