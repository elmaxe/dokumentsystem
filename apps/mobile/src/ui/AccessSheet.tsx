import type { AccessPerson, NodeDto, Person } from "@dokument/api";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, ScrollView, View } from "react-native";
import { sessionQuery } from "../state/queries";
import { accessQuery, invalidateSharing, shareError, sharing } from "../state/sharing";
import { useTheme } from "../state/theme";
import { Avatar } from "./Avatar";
import { Button } from "./Button";
import { Notice } from "./Notice";
import { Sheet } from "./Sheet";
import { toast } from "./Snackbar";
import { Text } from "./Text";

/**
 * Who can see something that isn't the person's own (docs/design.md §9, §15.3), read-only, with Leave where it was
 * shared with them directly. Leaving asks first.
 */
export function AccessSheet({
  node,
  leave: leaving,
  onClose,
  onLeft,
}: {
  node: NodeDto;
  /** Opens on the question, for Leave in an item's actions. */
  leave?: boolean;
  onClose: () => void;
  onLeft?: () => void;
}) {
  const { t } = useTranslation();
  const theme = useTheme();
  const queryClient = useQueryClient();
  const me = useQuery(sessionQuery).data?.user.id;
  const access = useQuery(accessQuery(node.id));
  const [asking, setAsking] = useState(!!leaving);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const close = () => {
    setAsking(false);
    setBusy(false);
    setError(null);
    onClose();
  };
  const data = access.data;
  const direct = data?.people.some((p) => p.person.id === me && !!p.shareId) ?? false;

  const leave = async () => {
    setBusy(true);
    setError(null);
    try {
      await sharing.leave(node.id);
    } catch (problem) {
      setBusy(false);
      setError(shareError(t, problem));
      return;
    }
    toast(t("toasts.left", { name: node.name }));
    close();
    onLeft?.();
    await invalidateSharing(queryClient);
  };

  return (
    <Sheet
      open
      onClose={close}
      title={asking ? t("share.leaveTitle", { name: node.name }) : t("share.peopleWithAccess")}
    >
      {asking ? (
        <View className="mx-5 gap-4">
          <Text className="text-on-surface-variant">
            {t("share.leaveConfirm", { name: node.name, owner: node.ownerName })}
          </Text>
          {error ? <Notice tone="error" icon="error" text={error} /> : null}
          <View className="flex-row justify-end gap-2">
            <Button
              variant="text"
              label={t("common.cancel")}
              onPress={() => (leaving ? close() : setAsking(false))}
            />
            <Button variant="danger" label={t("share.leave")} busy={busy} onPress={() => void leave()} />
          </View>
        </View>
      ) : !data ? (
        <View className="items-center py-8">
          {access.isError ? (
            <Text className="text-on-surface-variant">{t("errors.generic")}</Text>
          ) : (
            <ActivityIndicator color={theme.color("primary")} />
          )}
        </View>
      ) : (
        <>
          {data.yourAccess !== "owner" ? (
            <Text variant="body-md" className="mx-5 mb-2 text-on-surface-variant">
              {t("share.onlyOwner", { name: data.owner.name })}
            </Text>
          ) : null}
          <ScrollView style={{ maxHeight: 400 }}>
            <PersonRow person={data.owner} me={me} role={t("share.roles.owner")} />
            {data.people.map((entry) => (
              <PersonRow
                key={entry.person.id}
                person={entry.person}
                me={me}
                role={t(`share.roles.${entry.role}`)}
                note={inheritedNote(t, entry)}
              />
            ))}
          </ScrollView>
          {direct ? (
            <View className="flex-row justify-end px-4 pt-2">
              <Button variant="danger" icon="logout" label={t("share.leave")} onPress={() => setAsking(true)} />
            </View>
          ) : null}
        </>
      )}
    </Sheet>
  );
}

type TFunction = ReturnType<typeof useTranslation>["t"];

/** "Editor of “Projects”, so here too", naming the folder only when the person can see it (§9). */
export function inheritedNote(t: TFunction, entry: AccessPerson): string | undefined {
  if (!entry.inheritedRole || entry.shareId) return undefined;
  return entry.inheritedFrom
    ? t(`share.inherited.${entry.inheritedRole}`, { folder: entry.inheritedFrom.name })
    : undefined;
}

/** One person with access: their initial, name, email or where their access comes from, and their role. */
export function PersonRow({
  person,
  me,
  role,
  note,
  trailing,
}: {
  person: Person;
  me: string | undefined;
  role?: string;
  note?: string;
  trailing?: ReactNode;
}) {
  const { t } = useTranslation();
  const name = person.id === me ? t("share.you", { name: person.name }) : person.name;
  const sub = note ?? person.email ?? "";
  return (
    <View
      accessible={!trailing}
      accessibilityLabel={[name, role, sub].filter(Boolean).join(", ")}
      className="min-h-16 flex-row items-center gap-4 py-2 pl-5 pr-4"
    >
      <Avatar name={person.name} size={40} />
      <View className="min-w-0 flex-1">
        <Text numberOfLines={1} className="text-on-surface">
          {name}
        </Text>
        {sub ? (
          <Text variant="body-sm" numberOfLines={1} className="text-on-surface-variant">
            {sub}
          </Text>
        ) : null}
      </View>
      {trailing ?? (role ? <Text variant="label-lg" className="text-on-surface-variant">{role}</Text> : null)}
    </View>
  );
}
