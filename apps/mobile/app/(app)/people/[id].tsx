import type { AccessPerson, Person, ShareRole } from "@dokument/api";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { router, useLocalSearchParams } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, FlatList, Pressable, TextInput, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { nodeQuery, sessionQuery } from "../../../src/state/queries";
import {
  accessQuery,
  invalidateSharing,
  peopleQuery,
  shareError,
  sharing,
} from "../../../src/state/sharing";
import { useTheme } from "../../../src/state/theme";
import { inheritedNote, PersonRow } from "../../../src/ui/AccessSheet";
import { Avatar } from "../../../src/ui/Avatar";
import { Button, IconButton } from "../../../src/ui/Button";
import { Icon } from "../../../src/ui/Icon";
import { Notice } from "../../../src/ui/Notice";
import { Sheet, SheetAction, useKept } from "../../../src/ui/Sheet";
import { toast } from "../../../src/ui/Snackbar";
import { font, Text } from "../../../src/ui/Text";

const roles: ShareRole[] = ["viewer", "editor"];

/**
 * Sharing something of the person's own with people (docs/design.md §9, §15.3): add people by name or email as Viewer
 * or Editor, then the owner and everyone with access, whose role can be changed or taken away. A role from a folder
 * above can only be raised here, never lowered.
 */
export default function SharePeople() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const { t } = useTranslation();
  const theme = useTheme();
  const insets = useSafeAreaInsets();
  const queryClient = useQueryClient();
  const me = useQuery(sessionQuery).data?.user.id;
  const node = useQuery(nodeQuery(id)).data?.node;
  const access = useQuery(accessQuery(id));
  const [typed, setTyped] = useState("");
  const query = typed.trim();
  const people = useQuery({ ...peopleQuery(query), enabled: !!query });
  const [role, setRole] = useState<ShareRole>("viewer");
  const [sheet, setSheet] = useState<"newRole" | "help" | AccessPerson | null>(null);
  const [removing, setRemoving] = useState<AccessPerson | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const name = node?.name ?? "";
  const data = access.data;
  const roleName = (r: ShareRole) => t(`share.roles.${r}`).toLowerCase();
  const has = new Set([
    data?.owner.id,
    ...(data?.people.filter((p) => p.shareId).map((p) => p.person.id) ?? []),
  ]);

  const change = async (run: () => Promise<unknown>, done: string) => {
    setError(null);
    setBusy(true);
    try {
      await run();
      toast(done);
    } catch (problem) {
      setError(shareError(t, problem));
    }
    setBusy(false);
    await invalidateSharing(queryClient);
  };

  const add = (person: Person) => {
    setTyped("");
    void change(
      () => sharing.share(id, person.id, role),
      t("toasts.shareAdded", { name: person.name }),
    );
  };
  const setPersonRole = (entry: AccessPerson, next: ShareRole) => {
    setSheet(null);
    if (next === entry.role && entry.shareId) return;
    // A role from a folder above is raised with a share here; one here is changed in place.
    void change(
      () =>
        entry.shareId
          ? sharing.change(entry.shareId, next)
          : sharing.share(id, entry.person.id, next),
      t("toasts.shareChanged", { name: entry.person.name, role: roleName(next) }),
    );
  };
  const remove = (entry: AccessPerson) => {
    setRemoving(null);
    const shareId = entry.shareId;
    if (!shareId) return;
    void change(
      () => sharing.remove(shareId),
      t("toasts.shareRemoved", { name: entry.person.name }),
    );
  };

  const candidates = (people.data ?? []).filter((person) => person.id !== data?.owner.id);
  const chosen = sheet && typeof sheet === "object" ? sheet : null;
  // Shown while their sheets slide away after they were set back to null.
  const shownPerson = useKept(chosen);
  const shownRemoving = useKept(removing);

  return (
    <View
      className="flex-1 bg-surface"
      style={{ paddingTop: insets.top, paddingBottom: insets.bottom }}
    >
      <View className="h-topbar flex-row items-center gap-1 pl-1 pr-2">
        <IconButton
          icon="close"
          label={t("common.close")}
          onPress={() => router.back()}
          color="on-surface"
        />
        <Text
          variant="title-lg"
          numberOfLines={1}
          accessibilityRole="header"
          className="flex-1 text-on-surface"
        >
          {t("share.title", { name })}
        </Text>
        {busy ? <ActivityIndicator color={theme.color("primary")} /> : null}
        <IconButton
          icon="help"
          label={t("share.help")}
          onPress={() => setSheet("help")}
          color="on-surface"
        />
      </View>
      <View className="mx-4 my-2 min-h-14 flex-row items-center gap-3 rounded-sm border border-outline bg-surface-container-lowest pl-3 pr-1.5">
        <Icon name="person_add" />
        <TextInput
          value={typed}
          onChangeText={setTyped}
          placeholder={t("mobile.sharing.addPlaceholder")}
          accessibilityLabel={t("share.addPeople")}
          autoCapitalize="none"
          autoCorrect={false}
          inputMode="email"
          placeholderTextColor={theme.color("on-surface-variant")}
          selectionColor={theme.color("primary")}
          style={{
            ...font(400),
            fontSize: 16,
            color: theme.color("on-surface"),
            flex: 1,
            minHeight: 48,
          }}
        />
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("share.newRole", { role: t(`share.roles.${role}`) })}
          onPress={() => setSheet("newRole")}
          className="h-9 flex-row items-center gap-0.5 rounded-sm bg-secondary-container pl-3 pr-1 active:opacity-80"
        >
          <Text variant="label-lg" className="text-on-secondary-container">
            {t(`share.roles.${role}`)}
          </Text>
          <Icon name="arrow_drop_down" size={20} color="on-secondary-container" />
        </Pressable>
      </View>
      {error ? (
        <View className="mx-4 mb-2">
          <Notice tone="error" icon="error" text={error} />
        </View>
      ) : null}

      {query ? (
        <FlatList
          data={candidates}
          keyExtractor={(person) => person.id}
          keyboardShouldPersistTaps="handled"
          ListHeaderComponent={
            <Text variant="title-sm" className="px-4 pb-1 pt-2 text-on-surface-variant">
              {t("share.matches")}
            </Text>
          }
          ListEmptyComponent={
            people.isPending ? undefined : (
              <Text variant="body-md" className="px-4 py-3 text-on-surface-variant">
                {t("share.noMatches")}
              </Text>
            )
          }
          ListFooterComponent={
            <Text variant="body-sm" className="px-4 py-3 text-on-surface-variant">
              {t("mobile.sharing.onlySignedIn")}
            </Text>
          }
          renderItem={({ item }) => {
            const already = has.has(item.id);
            return (
              <Pressable
                accessibilityRole="button"
                accessibilityState={{ disabled: already }}
                disabled={already || busy}
                onPress={() => add(item)}
                className={`min-h-16 flex-row items-center gap-4 px-4 py-2 active:bg-surface-container-high ${already ? "opacity-60" : ""}`}
              >
                <Avatar name={item.name} id={item.id} avatar={item.avatar} size={40} />
                <View className="min-w-0 flex-1">
                  <Text numberOfLines={1} className="text-on-surface">
                    {item.name}
                  </Text>
                  <Text variant="body-sm" numberOfLines={1} className="text-on-surface-variant">
                    {already ? t("mobile.sharing.alreadyHas") : (item.email ?? "")}
                  </Text>
                </View>
              </Pressable>
            );
          }}
        />
      ) : !data ? (
        <View className="flex-1 items-center justify-center">
          {access.isError ? (
            <Notice tone="error" icon="error" text={t("errors.generic")} />
          ) : (
            <ActivityIndicator color={theme.color("primary")} />
          )}
        </View>
      ) : (
        <FlatList
          data={data.people}
          keyExtractor={(entry) => entry.person.id}
          ListHeaderComponent={
            <>
              <Text variant="title-sm" className="px-4 pb-1 pt-4 text-on-surface-variant">
                {t("share.peopleWithAccess")}
              </Text>
              <PersonRow person={data.owner} me={me} role={t("share.roles.owner")} />
            </>
          }
          renderItem={({ item }) => (
            <PersonRow
              person={item.person}
              me={me}
              note={inheritedNote(t, item)}
              trailing={
                <Pressable
                  accessibilityRole="button"
                  accessibilityLabel={`${t("share.changeRole", { name: item.person.name })}, ${t(`share.roles.${item.role}`)}`}
                  onPress={() => setSheet(item)}
                  className="h-10 flex-row items-center gap-0.5 rounded-sm pl-3 pr-1 active:bg-surface-container-high"
                >
                  <Text variant="label-lg" className="text-on-surface-variant">
                    {t(`share.roles.${item.role}`)}
                  </Text>
                  <Icon name="arrow_drop_down" size={20} />
                </Pressable>
              }
            />
          )}
          ListFooterComponent={
            <View className="gap-3 px-4 py-4">
              <Text variant="body-sm" className="text-on-surface-variant">
                {t("share.rolesHint")}
              </Text>
              <View className="flex-row items-center gap-2">
                <Icon name="link" size={18} />
                <Text variant="body-sm" className="flex-1 text-on-surface-variant">
                  {t("mobile.sharing.linksLater")}
                </Text>
              </View>
            </View>
          }
        />
      )}

      <Sheet
        open={sheet === "newRole"}
        onClose={() => setSheet(null)}
        title={t("mobile.sharing.addAs")}
      >
        {roles.map((r) => (
          <RoleChoice
            key={r}
            role={r}
            selected={role === r}
            onPress={() => {
              setRole(r);
              setSheet(null);
            }}
          />
        ))}
      </Sheet>
      <Sheet open={sheet === "help"} onClose={() => setSheet(null)} title={t("share.help")}>
        <Text className="mx-5 mb-2 text-on-surface-variant">{t("share.rolesHint")}</Text>
        <Text className="mx-5 mb-2 text-on-surface-variant">{t("mobile.sharing.inheritHint")}</Text>
      </Sheet>
      <Sheet open={!!chosen} onClose={() => setSheet(null)} title={shownPerson?.person.name}>
        {shownPerson
          ? roles.map((r) => (
              <RoleChoice
                key={r}
                role={r}
                selected={shownPerson.role === r}
                // A role from a folder above can't be lowered here (§9).
                disabled={shownPerson.inheritedRole === "editor" && r === "viewer"}
                onPress={() => setPersonRole(shownPerson, r)}
              />
            ))
          : null}
        {shownPerson?.shareId ? (
          <>
            <View className="my-2 h-px bg-outline-variant" />
            <SheetAction
              icon={<Icon name="person_remove" color="error" />}
              label={t("share.removeAccess")}
              danger
              onPress={() => {
                setSheet(null);
                setRemoving(shownPerson);
              }}
            />
          </>
        ) : null}
      </Sheet>
      <Sheet
        open={!!removing}
        onClose={() => setRemoving(null)}
        title={
          shownRemoving
            ? t("mobile.sharing.removeTitle", { name: shownRemoving.person.name })
            : undefined
        }
      >
        <View className="mx-5 gap-4">
          <Text className="text-on-surface-variant">
            {shownRemoving
              ? t("mobile.sharing.removeText", { name: shownRemoving.person.name, item: name })
              : ""}
          </Text>
          <View className="flex-row justify-end gap-2">
            <Button variant="text" label={t("common.cancel")} onPress={() => setRemoving(null)} />
            <Button
              variant="danger"
              label={t("share.removeAccess")}
              onPress={() => (removing ? remove(removing) : undefined)}
            />
          </View>
        </View>
      </Sheet>
    </View>
  );
}

function RoleChoice({
  role,
  selected,
  disabled,
  onPress,
}: {
  role: ShareRole;
  selected: boolean;
  disabled?: boolean;
  onPress: () => void;
}) {
  const { t } = useTranslation();
  return (
    <Pressable
      accessibilityRole="radio"
      accessibilityState={{ checked: selected, disabled }}
      disabled={disabled}
      onPress={onPress}
      className={`min-h-16 flex-row items-center gap-4 px-5 py-2 active:bg-surface-container-highest ${disabled ? "opacity-40" : ""}`}
    >
      <Icon
        name={selected ? "radio_button_checked" : "radio_button_unchecked"}
        color={selected ? "primary" : "on-surface-variant"}
      />
      <View className="flex-1">
        <Text className="text-on-surface">{t(`share.roles.${role}`)}</Text>
        <Text variant="body-sm" className="text-on-surface-variant">
          {t(`mobile.sharing.${role}Hint`)}
        </Text>
      </View>
    </Pressable>
  );
}
