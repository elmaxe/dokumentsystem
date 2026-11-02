import type { AdminSettings, AdminUser, ProbeResult } from "@dokument/api";
import { formatBytes, formatDuration } from "@dokument/core";
import { Avatar, Banner, Button, Dialog, Icon, IconButton, Progress, Switch, TextField, useSnackbar } from "@dokument/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useId, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import {
  admin,
  adminSettingsQuery,
  adminUsersQuery,
  mlStatusQuery,
  parseGib,
  storageReportQuery,
  textProgressQuery,
  toGib,
  transcriptionProgressQuery,
  videoCopiesQuery,
} from "./adminApi";

const sections = [
  { id: "storage", icon: "hard_drive" },
  { id: "people", icon: "group" },
  { id: "textRecognition", icon: "document_scanner" },
  { id: "videos", icon: "movie" },
  { id: "office", icon: "description" },
  { id: "ml", icon: "neurology" },
  { id: "speech", icon: "graphic_eq" },
  { id: "scanning", icon: "shield" },
  { id: "versions", icon: "history" },
  { id: "signIn", icon: "login" },
] as const;
type SectionId = (typeof sections)[number]["id"];

/** The admin pages (docs/design.md §4.3). The admin rail in the app shell moves between them. */
export function AdminPeoplePage() {
  const { t } = useTranslation();
  return (
    <AdminFrame title={t("admin.nav.people")}>
      <PeopleSection />
    </AdminFrame>
  );
}

export function AdminStoragePage() {
  const { t } = useTranslation();
  return (
    <AdminFrame title={t("admin.nav.storage")}>
      {(settings) => <StorageSection key={settings.dataUpdatedAt} settings={settings.data} />}
    </AdminFrame>
  );
}

/** Server settings. Each section saves on its own. */
export function AdminSettingsPage() {
  const { t } = useTranslation();
  return (
    <AdminFrame title={t("admin.nav.settings")}>
      {(settings) => (
        <>
          <TextRecognitionSection settings={settings.data} />
          <VideosSection key={`p${settings.dataUpdatedAt}`} settings={settings.data} />
          <OfficeSection key={`o${settings.dataUpdatedAt}`} settings={settings.data} />
          <MlSection key={`m${settings.dataUpdatedAt}`} settings={settings.data} />
          <SpeechSection key={`s${settings.dataUpdatedAt}`} settings={settings.data} />
          <ScannerSection key={`c${settings.dataUpdatedAt}`} settings={settings.data} />
          <VersionsSection key={`v${settings.dataUpdatedAt}`} settings={settings.data} />
          <SignInSection settings={settings.data} />
        </>
      )}
    </AdminFrame>
  );
}

type LoadedSettings = { data: AdminSettings; dataUpdatedAt: number };

/** Page title and body; a render function waits for the settings. */
export function AdminFrame({ title, children }: { title: string; children: ReactNode | ((settings: LoadedSettings) => ReactNode) }) {
  const { t } = useTranslation();
  const settings = useQuery({ ...adminSettingsQuery, enabled: typeof children === "function" });
  return (
    <main className="min-w-0 flex-1 overflow-y-auto px-4 pb-12 sm:px-6 lg:px-8">
      <div className="mx-auto flex max-w-220 flex-col gap-4 pt-2 lg:mx-0">
        <h1 className="m-0 text-headline-sm lg:text-headline-md">{title}</h1>
        {typeof children !== "function" ? (
          children
        ) : settings.data ? (
          children({ data: settings.data, dataUpdatedAt: settings.dataUpdatedAt })
        ) : settings.isError ? (
          <Banner tone="error">{t("errors.generic")}</Banner>
        ) : (
          <Progress label={title} />
        )}
      </div>
    </main>
  );
}

/** A card on an admin page. On a page with one card the page title is its heading. */
function Section({ id, intro, heading = true, children }: { id: SectionId; intro?: string; heading?: boolean; children: ReactNode }) {
  const { t } = useTranslation();
  const icon = sections.find((s) => s.id === id)!.icon;
  return (
    <section
      id={`admin-${id}`}
      aria-labelledby={heading ? `admin-${id}-title` : undefined}
      aria-label={heading ? undefined : t(`admin.sections.${id}`)}
      className="rounded-lg bg-surface-container-low p-4 sm:p-6"
    >
      {heading && (
        <h2 id={`admin-${id}-title`} className="m-0 mb-1 flex items-center gap-2 text-title-lg">
          <Icon name={icon} className="text-primary" />
          {t(`admin.sections.${id}`)}
        </h2>
      )}
      {intro && <p className="m-0 max-w-160 text-body-md text-on-surface-variant">{intro}</p>}
      <div className="mt-5 flex flex-col gap-5">{children}</div>
    </section>
  );
}

/** Saves one section, shows a snackbar and refreshes what the change can affect. */
function useSave() {
  const { t } = useTranslation();
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const save = async (run: () => Promise<unknown>) => {
    setBusy(true);
    try {
      await run();
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["admin"] }),
        queryClient.invalidateQueries({ queryKey: ["storage"] }),
        // The session carries the features that change how files open, such as the Word viewer.
        queryClient.invalidateQueries({ queryKey: ["auth", "session"] }),
      ]);
      toast(t("common.saved"));
    } catch {
      toast(t("errors.generic"));
    } finally {
      setBusy(false);
    }
  };
  return { busy, save };
}

function SaveButton({ disabled, onClick }: { disabled: boolean; onClick: () => void }) {
  const { t } = useTranslation();
  return (
    <div className="flex justify-end">
      <Button variant="tonal" icon="check" disabled={disabled} onClick={onClick}>
        {t("common.save")}
      </Button>
    </div>
  );
}

/** A choice between a few short options, drawn as connected pills. */
function Segmented<T extends string>({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: T;
  options: { id: T; label: string }[];
  onChange: (value: T) => void;
}) {
  return (
    <div role="radiogroup" aria-label={label} className="inline-flex h-control overflow-hidden rounded-full border border-outline">
      {options.map((option, index) => (
        <button
          key={option.id}
          type="button"
          role="radio"
          aria-checked={value === option.id}
          onClick={() => onChange(option.id)}
          className={`inline-flex cursor-pointer items-center gap-1.5 px-4 text-label-lg ${index > 0 ? "border-l border-outline" : ""} ${
            value === option.id ? "bg-secondary-container text-on-secondary-container" : "text-on-surface hover:bg-on-surface/8"
          }`}
        >
          {value === option.id && <Icon name="check" size={18} />}
          {option.label}
        </button>
      ))}
    </div>
  );
}

function StorageSection({ settings }: { settings: AdminSettings }) {
  const { t, i18n } = useTranslation();
  const report = useQuery(storageReportQuery);
  const initial = settings.storage.defaultQuotaBytes;
  const [limited, setLimited] = useState(initial !== null);
  const [gb, setGb] = useState(initial !== null ? toGib(initial) : "");
  const { busy, save } = useSave();
  const bytes = limited ? parseGib(gb) : null;
  const invalid = limited && bytes === null;
  const dirty = bytes !== initial;
  const size = (value: number) => formatBytes(value, i18n.language);

  return (
    <Section id="storage" heading={false} intro={t("admin.storage.intro")}>
      {report.data?.volumes.map((volume) => {
        const holdsOriginals = volume.holds.includes("originals");
        const used = volume.totalBytes - volume.freeBytes;
        const files = holdsOriginals ? Math.min(report.data.filesBytes, used) : 0;
        const percent = (n: number) => (volume.totalBytes > 0 ? `${(n / volume.totalBytes) * 100}%` : "0%");
        return (
          <div key={volume.holds.join()} className="flex flex-col gap-2 rounded-md bg-surface-container-lowest p-4">
            <div className="flex flex-wrap justify-between gap-3">
              <span className="text-title-sm">{holdsOriginals ? t("admin.storage.disk") : t("admin.storage.derivedDisk")}</span>
              <span className="tabular text-on-surface-variant">
                {t("admin.storage.diskFree", { free: size(volume.freeBytes), total: size(volume.totalBytes) })}
              </span>
            </div>
            <div className="flex h-2 overflow-hidden rounded-full bg-surface-container-highest" aria-hidden>
              <span className="block h-full bg-primary" style={{ width: percent(files) }} />
              <span className="block h-full bg-outline" style={{ width: percent(used - files) }} />
            </div>
            <div className="tabular flex flex-wrap gap-5 text-body-sm text-on-surface-variant">
              {holdsOriginals && <Legend className="bg-primary">{t("admin.storage.files", { size: size(files) })}</Legend>}
              <Legend className="bg-outline">{t("admin.storage.other", { size: size(used - files) })}</Legend>
            </div>
          </div>
        );
      })}
      <div className="flex flex-col gap-2">
        <span className="text-label-lg text-on-surface">{t("admin.storage.defaultQuota")}</span>
        <div className="flex flex-wrap items-start gap-3">
          <Segmented
            label={t("admin.storage.defaultQuota")}
            value={limited ? "limit" : "none"}
            options={[
              { id: "none", label: t("admin.storage.noLimit") },
              { id: "limit", label: t("admin.storage.limit") },
            ]}
            onChange={(value) => setLimited(value === "limit")}
          />
          {limited && (
            <div className="flex items-start gap-2">
              <TextField
                aria-label={t("admin.storage.limitLabel")}
                inputMode="decimal"
                value={gb}
                onChange={(event) => setGb(event.target.value)}
                error={invalid && gb !== "" ? t("admin.storage.invalid") : undefined}
                className="tabular w-28"
              />
              <span className="mt-2.5">{t("admin.storage.gib")}</span>
            </div>
          )}
        </div>
        <span className="text-body-sm text-on-surface-variant">{t("admin.storage.defaultHint")}</span>
      </div>
      <SaveButton disabled={busy || invalid || !dirty} onClick={() => void save(() => admin.putStorage(bytes))} />
    </Section>
  );
}

function Legend({ className, children }: { className: string; children: ReactNode }) {
  return (
    <span className="flex items-center gap-1.5">
      <span aria-hidden className={`inline-block size-2 rounded-[2px] ${className}`} />
      {children}
    </span>
  );
}

function PeopleSection() {
  const { t, i18n } = useTranslation();
  const users = useQuery(adminUsersQuery);
  const settings = useQuery(adminSettingsQuery);
  const provider = settings.data?.signIn.providerName ?? t("admin.people.provider");
  const [editing, setEditing] = useState<AdminUser | null>(null);
  const tones = [1, 3, 2, 0] as const;

  return (
    <Section id="people" heading={false} intro={t("admin.people.intro")}>
      {users.isPending && <Progress label={t("admin.sections.people")} />}
      <ul className="m-0 flex list-none flex-col divide-y divide-outline-variant p-0">
        {users.data?.map((user, index) => {
          const quota = user.effectiveQuotaBytes;
          const percent = quota ? Math.min(100, Math.round((user.usedBytes / quota) * 100)) : 0;
          const used = formatBytes(user.usedBytes, i18n.language);
          return (
            <li key={user.id} className="flex flex-wrap items-center gap-3 py-3">
              <Avatar name={user.displayName} tone={tones[index % tones.length]} />
              <div className="flex min-w-0 flex-[1_1_12rem] flex-col">
                <span className="flex flex-wrap items-center gap-2">
                  <span className="truncate text-title-sm text-on-surface">{user.displayName}</span>
                  {user.role === "admin" && (
                    <span className="rounded-xs bg-secondary-container px-1.5 text-label-sm text-on-secondary-container uppercase">
                      {t("admin.users.admin")}
                    </span>
                  )}
                  {user.disabled && (
                    <span className="rounded-xs bg-surface-container-highest px-1.5 text-label-sm text-on-surface-variant uppercase">
                      {t("admin.people.disabled")}
                    </span>
                  )}
                </span>
                {user.email && <span className="truncate text-body-sm text-on-surface-variant">{user.email}</span>}
              </div>
              <div className="flex min-w-0 flex-[1_1_14rem] flex-col gap-1">
                {quota !== null && (
                  <Progress
                    value={percent}
                    tone={percent >= 95 ? "error" : percent >= 80 ? "warning" : "primary"}
                    label={t("storage.title")}
                  />
                )}
                <span className="tabular flex items-center gap-1 text-body-sm text-on-surface-variant">
                  {quota !== null && percent >= 80 && (
                    <Icon
                      name={percent >= 95 ? "error" : "warning"}
                      size={16}
                      filled
                      className={percent >= 95 ? "text-error" : "text-warning"}
                    />
                  )}
                  {user.quotaSource === "claim"
                    ? t("admin.people.usage", {
                        used,
                        quota: quota === null ? t("admin.people.noLimit") : formatBytes(quota, i18n.language),
                        source: t("admin.people.setBy", { provider }),
                      })
                    : quota === null
                      ? t("admin.people.usageUnlimited", { used })
                      : t("admin.people.usage", {
                          used,
                          quota: formatBytes(quota, i18n.language),
                          source: user.quota.kind === "limit" ? t("admin.people.ownLimit") : t("admin.people.defaultLimit"),
                        })}
                </span>
              </div>
              <IconButton icon="edit" label={t("admin.people.edit", { name: user.displayName })} onClick={() => setEditing(user)} />
            </li>
          );
        })}
      </ul>
      {editing && <QuotaDialog key={editing.id} user={editing} provider={provider} onClose={() => setEditing(null)} />}
    </Section>
  );
}

function QuotaDialog({ user, provider, onClose }: { user: AdminUser; provider: string; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const settings = useQuery(adminSettingsQuery);
  // The provider's quota claim wins (§17.1), so the dialog only shows it.
  const locked = user.quotaSource === "claim";
  const [kind, setKind] = useState(user.quota.kind);
  const [gb, setGb] = useState(user.quota.bytes ? toGib(user.quota.bytes) : "");
  const { busy, save } = useSave();
  const bytes = kind === "limit" ? parseGib(gb) : null;
  const defaultQuota = settings.data?.storage.defaultQuotaBytes ?? null;
  const options = [
    {
      id: "default" as const,
      label: t("admin.quota.useDefault"),
      hint:
        defaultQuota === null
          ? t("admin.quota.defaultNone")
          : t("admin.quota.defaultIs", { quota: formatBytes(defaultQuota, i18n.language) }),
    },
    { id: "limit" as const, label: t("admin.quota.own") },
    { id: "unlimited" as const, label: t("admin.quota.none"), hint: t("admin.quota.noneHint") },
  ];

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={t("admin.quota.title", { name: user.displayName })}
      description={t("admin.quota.uses", { used: formatBytes(user.usedBytes, i18n.language) })}
      width="sm"
      footer={
        locked ? (
          <Button onClick={onClose}>{t("common.close")}</Button>
        ) : (
          <>
            <Button variant="text" onClick={onClose}>
              {t("common.cancel")}
            </Button>
            <Button
              disabled={busy || (kind === "limit" && bytes === null)}
              onClick={() => void save(() => admin.putQuota(user.id, kind, bytes)).then(onClose)}
            >
              {t("common.save")}
            </Button>
          </>
        )
      }
    >
      {locked && (
        <Banner tone="info" className="mb-3">
          {t("admin.quota.fromClaim", { provider, name: user.displayName })}
        </Banner>
      )}
      <fieldset disabled={locked} className="m-0 flex flex-col gap-1 border-0 p-0 disabled:opacity-60">
        <legend className="sr-only">{t("admin.storage.defaultQuota")}</legend>
        {options.map((option) => (
          <label
            key={option.id}
            className={`flex items-start gap-3 rounded-md p-3 ${kind === option.id ? "bg-secondary-container" : locked ? "" : "hover:bg-on-surface/8"} ${locked ? "cursor-default" : "cursor-pointer"}`}
          >
            <input
              type="radio"
              name="quota"
              checked={kind === option.id}
              onChange={() => setKind(option.id)}
              className="mt-0.5 size-5 accent-primary"
            />
            <span className="flex min-w-0 flex-col gap-2">
              <span className="flex flex-col">
                <span className="text-title-sm text-on-surface">{option.label}</span>
                {option.hint && <span className="text-body-sm text-on-surface-variant">{option.hint}</span>}
              </span>
              {option.id === "limit" && kind === "limit" && (
                <span className="flex items-start gap-2">
                  <TextField
                    aria-label={t("admin.quota.ownLabel")}
                    inputMode="decimal"
                    value={gb}
                    onChange={(event) => setGb(event.target.value)}
                    error={bytes === null && gb !== "" ? t("admin.storage.invalid") : undefined}
                    className="tabular w-28"
                  />
                  <span className="mt-2.5 text-on-surface">{t("admin.storage.gib")}</span>
                </span>
              )}
            </span>
          </label>
        ))}
      </fieldset>
    </Dialog>
  );
}

type MlRow = { url: string; apiKey: string | null; keepApiKey: boolean; hasApiKey: boolean };

function MlSection({ settings }: { settings: AdminSettings }) {
  const { t } = useTranslation();
  const status = useQuery(mlStatusQuery);
  const saved: MlRow[] = settings.ml.endpoints.map((e) => ({ url: e.url, apiKey: null, keepApiKey: true, hasApiKey: e.hasApiKey }));
  const [rows, setRows] = useState(saved);
  const [url, setUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [test, setTest] = useState<ProbeResult | "testing" | null>(null);
  const { busy, save } = useSave();
  const dirty = JSON.stringify(rows) !== JSON.stringify(saved);
  const duplicate = rows.some((r) => r.url.replace(/\/+$/, "") === url.trim().replace(/\/+$/, ""));
  const validUrl = /^https?:\/\/[^\s/?#]+[^\s?#]*$/.test(url.trim());
  const firstUp = status.data?.find((s) => s.status.reachable)?.url;
  const fromEnvironment = status.data?.find((s) => s.fromEnvironment);

  const move = (index: number, by: -1 | 1) => {
    const next = [...rows];
    [next[index], next[index + by]] = [next[index + by]!, next[index]!];
    setRows(next);
  };

  const runTest = async () => {
    setTest("testing");
    try {
      setTest(await admin.testMl(url.trim(), apiKey.trim() || null));
    } catch {
      setTest({ reachable: false, version: null, error: t("errors.generic") });
    }
  };

  return (
    <Section id="ml" intro={t("admin.ml.listIntro")}>
      {rows.length === 0 && !fromEnvironment ? (
        <p className="m-0 text-body-md text-on-surface-variant">{t("admin.ml.empty")}</p>
      ) : (
        <ol className="m-0 flex list-none flex-col gap-2 p-0">
          {rows.map((row, index) => {
            const probe = status.data?.find((s) => s.url === row.url)?.status;
            return (
              <li key={row.url} className="flex flex-wrap items-center gap-3 rounded-md bg-surface-container-lowest py-2 pr-2 pl-4">
                <span className="tabular w-4 text-title-sm text-on-surface-variant">{index + 1}</span>
                <div className="flex min-w-0 flex-[1_1_14rem] flex-col">
                  <span className="font-mono text-mono-sm break-all text-on-surface">{row.url}</span>
                  <span className="text-body-sm text-on-surface-variant">
                    {row.hasApiKey || row.apiKey ? t("admin.ml.apiKeySet") : t("admin.ml.noApiKey")}
                  </span>
                </div>
                <MlStatus probe={probe} checking={status.isFetching && !probe} inUse={row.url === firstUp} unsaved={!row.keepApiKey} />
                <div className="flex">
                  <IconButton icon="arrow_upward" label={t("admin.ml.moveUp", { url: row.url })} disabled={index === 0} onClick={() => move(index, -1)} />
                  <IconButton
                    icon="arrow_downward"
                    label={t("admin.ml.moveDown", { url: row.url })}
                    disabled={index === rows.length - 1}
                    onClick={() => move(index, 1)}
                  />
                  <IconButton icon="delete" label={t("admin.ml.remove", { url: row.url })} onClick={() => setRows(rows.filter((_, i) => i !== index))} />
                </div>
              </li>
            );
          })}
          {fromEnvironment && (
            <li className="flex flex-wrap items-center gap-3 rounded-md bg-surface-container-lowest py-2 pr-2 pl-4">
              <Icon name="lock" size={16} className="w-4 text-on-surface-variant" />
              <div className="flex min-w-0 flex-[1_1_14rem] flex-col">
                <span className="font-mono text-mono-sm break-all text-on-surface">{fromEnvironment.url}</span>
                <span className="text-body-sm text-on-surface-variant">{t("admin.ml.fromEnvironment")}</span>
              </div>
              <MlStatus probe={fromEnvironment.status} checking={false} inUse={fromEnvironment.url === firstUp} unsaved={false} />
            </li>
          )}
        </ol>
      )}
      <form
        className="flex flex-col gap-3 rounded-md border border-dashed border-outline p-4"
        onSubmit={(event) => {
          event.preventDefault();
          if (!validUrl || duplicate) return;
          setRows([...rows, { url: url.trim().replace(/\/+$/, ""), apiKey: apiKey.trim() || null, keepApiKey: false, hasApiKey: false }]);
          setUrl("");
          setApiKey("");
          setTest(null);
        }}
      >
        <span className="text-title-sm text-on-surface">{t("admin.ml.add")}</span>
        <div className="grid gap-3 sm:grid-cols-2">
          <TextField
            label={t("admin.ml.urlLabel")}
            value={url}
            onChange={(event) => {
              setUrl(event.target.value);
              setTest(null);
            }}
            placeholder="http://gpu-box.lan:3003"
            error={duplicate ? t("admin.ml.duplicate") : undefined}
            mono
          />
          <TextField
            label={t("admin.ml.apiKey")}
            type="password"
            autoComplete="off"
            value={apiKey}
            onChange={(event) => setApiKey(event.target.value)}
            hint={t("admin.ml.apiKeyHint")}
          />
        </div>
        <ProbeBanner result={test} okText={(p) => [t("admin.ml.testOk"), p.version && t("admin.ml.version", { version: p.version })].filter(Boolean).join(" · ")} failText={(e) => t("admin.ml.testFailed", { error: e })} />
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="outlined" icon="network_check" disabled={!validUrl || test === "testing"} onClick={() => void runTest()}>
            {t("admin.ml.test")}
          </Button>
          <Button type="submit" variant="outlined" icon="add" disabled={!validUrl || duplicate}>
            {t("admin.ml.addButton")}
          </Button>
        </div>
      </form>
      <Banner tone="info">{t("admin.ml.usedFor")}</Banner>
      <SaveButton
        disabled={busy || !dirty}
        onClick={() => void save(() => admin.putMl(rows.map(({ url, apiKey, keepApiKey }) => ({ url, apiKey, keepApiKey }))))}
      />
    </Section>
  );
}

function MlStatus({ probe, checking, inUse, unsaved }: { probe?: ProbeResult; checking: boolean; inUse: boolean; unsaved: boolean }) {
  const { t } = useTranslation();
  if (unsaved) return null;
  if (!probe) {
    return checking ? <Chip tone="neutral" icon="hourglass_empty">{t("admin.ml.checking")}</Chip> : null;
  }
  if (!probe.reachable) {
    return (
      <Chip tone="error" icon="cloud_off" title={probe.error ?? undefined}>
        {t("admin.ml.unreachableShort")}
      </Chip>
    );
  }
  return (
    <Chip tone="success" icon="check_circle">
      {[inUse ? t("admin.ml.inUse") : t("admin.ml.answers"), probe.version && t("admin.ml.version", { version: probe.version })]
        .filter(Boolean)
        .join(" · ")}
    </Chip>
  );
}

function Chip({ tone, icon, title, children }: { tone: "neutral" | "error" | "success"; icon: string; title?: string; children: ReactNode }) {
  const tones = {
    neutral: "bg-secondary-container text-on-secondary-container",
    error: "bg-error-container text-on-error-container",
    success: "bg-success-container text-on-surface",
  };
  return (
    <span title={title} className={`inline-flex h-6 items-center gap-1 rounded-sm px-2 text-label-md whitespace-nowrap ${tones[tone]}`}>
      <Icon name={icon} size={16} filled={tone !== "neutral"} className={tone === "success" ? "text-success" : undefined} />
      {children}
    </span>
  );
}

function ProbeBanner({
  result,
  okText,
  failText,
}: {
  result: ProbeResult | "testing" | null;
  okText: (result: ProbeResult) => string;
  failText: (error: string) => string;
}) {
  const { t } = useTranslation();
  if (result === null) return null;
  if (result === "testing") return <Progress label={t("admin.ml.testing")} />;
  return result.reachable ? (
    <Banner tone="success">{okText(result)}</Banner>
  ) : (
    <Banner tone="error">{failText(result.error ?? "")}</Banner>
  );
}

/** Search and text recognition (§13.2): whether scans and photos are read, and how far the worker has come. */
function TextRecognitionSection({ settings }: { settings: AdminSettings }) {
  const { t, i18n } = useTranslation();
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  const progress = useQuery(textProgressQuery);
  const { busy, save } = useSave();
  const enabled = settings.textRecognition.enabled;
  const id = useId();
  const number = (n: number) => n.toLocaleString(i18n.language);

  const reread = async () => {
    try {
      queryClient.setQueryData(textProgressQuery.queryKey, await admin.rereadText());
      toast(t("admin.textRecognition.rereadStarted"));
    } catch {
      toast(t("errors.generic"));
    }
  };

  return (
    <Section id="textRecognition" intro={t("admin.textRecognition.intro")}>
      <div className="flex items-center gap-4">
        <label htmlFor={id} className="flex min-w-0 flex-1 cursor-pointer flex-col">
          <span className="text-title-sm">{t("admin.textRecognition.label")}</span>
          <span className="text-body-sm text-on-surface-variant">{t("admin.textRecognition.languages")}</span>
        </label>
        <Switch
          id={id}
          checked={enabled}
          onCheckedChange={(checked) => !busy && void save(() => admin.putTextRecognition(checked))}
        />
      </div>
      {progress.data && (
        <div className="flex flex-col gap-2 rounded-md bg-surface-container p-4 text-body-md">
          <span className="flex items-center gap-2 text-title-sm">
            <Icon name="check_circle" filled className="text-success" size={20} />
            {t("admin.textRecognition.read", { count: progress.data.read })}
          </span>
          {progress.data.waiting + progress.data.running > 0 && (
            <span className="tabular flex items-center gap-2 text-on-surface-variant">
              <Icon name="hourglass_top" size={20} />
              {t("admin.textRecognition.queue", { waiting: number(progress.data.waiting), running: number(progress.data.running) })}
            </span>
          )}
          {progress.data.failed > 0 && (
            <span className="flex items-center gap-2 text-on-surface-variant">
              <Icon name="error" size={20} className="text-error" />
              {t("admin.textRecognition.failed", { count: progress.data.failed })}
            </span>
          )}
          <span className="flex items-start gap-2 text-body-sm text-on-surface-variant">
            <Icon name="info" size={20} />
            {enabled ? t("admin.textRecognition.engine") : t("admin.textRecognition.offNote")}
          </span>
        </div>
      )}
      <div className="flex justify-end">
        <Button variant="outlined" icon="refresh" onClick={() => void reread()}>
          {t("admin.textRecognition.reread")}
        </Button>
      </div>
    </Section>
  );
}

/** Speech to text (§11.1): whether recordings are transcribed, with which model, and how far it has come. */
function SpeechSection({ settings }: { settings: AdminSettings }) {
  const { t, i18n } = useTranslation();
  const progress = useQuery(transcriptionProgressQuery);
  const current = settings.transcription;
  const [model, setModel] = useState(current.model);
  const { busy, save } = useSave();
  const enabledId = useId();
  const videosId = useId();
  const ml = useQuery(mlStatusQuery);
  const hasMl = settings.ml.endpoints.length > 0 || !!ml.data?.some((s) => s.fromEnvironment);
  const number = (n: number) => n.toLocaleString(i18n.language);
  const put = (change: Partial<{ enabled: boolean; model: string; videos: boolean }>) => {
    const next = { enabled: current.enabled, model, videos: current.videos, ...change };
    if (!busy) void save(() => admin.putTranscription(next.enabled, next.model, next.videos));
  };
  const stats = progress.data;
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  const [confirmRedo, setConfirmRedo] = useState(false);

  const redo = async () => {
    setConfirmRedo(false);
    try {
      queryClient.setQueryData(transcriptionProgressQuery.queryKey, await admin.redoTranscripts());
      toast(t("admin.speech.redoStarted"));
    } catch {
      toast(t("errors.generic"));
    }
  };

  return (
    <Section id="speech" intro={t("admin.speech.intro")}>
      {!hasMl && (
        <Banner
          icon="info"
          action={
            <Button variant="text" onClick={() => document.getElementById("admin-ml")?.scrollIntoView({ behavior: "smooth" })}>
              {t("admin.speech.addService")}
            </Button>
          }
        >
          {t("admin.speech.needsMl")}
        </Banner>
      )}
      <div className="flex items-center gap-4">
        <label htmlFor={enabledId} className="flex min-w-0 flex-1 cursor-pointer flex-col">
          <span className="text-title-sm">{t("admin.speech.label")}</span>
          <span className="text-body-sm text-on-surface-variant">{t("admin.speech.languages")}</span>
        </label>
        <Switch id={enabledId} checked={current.enabled} onCheckedChange={(enabled) => put({ enabled })} />
      </div>
      <div className="flex items-center gap-4">
        <label htmlFor={videosId} className="flex min-w-0 flex-1 cursor-pointer flex-col">
          <span className="text-title-sm">{t("admin.speech.videos")}</span>
          <span className="text-body-sm text-on-surface-variant">{t("admin.speech.videosHint")}</span>
        </label>
        <Switch id={videosId} checked={current.videos} onCheckedChange={(videos) => put({ videos })} />
      </div>
      <label className="flex flex-col gap-2">
        <span className="text-label-lg text-on-surface">{t("admin.speech.model")}</span>
        <select
          value={model}
          onChange={(event) => setModel(event.target.value)}
          className="h-control max-w-120 rounded-xs border border-outline bg-surface-container-lowest px-3 text-body-lg text-on-surface"
        >
          {current.models.map((id) => (
            <option key={id} value={id}>
              {modelLabel(t, id)}
            </option>
          ))}
        </select>
        <span className="max-w-160 text-body-sm text-on-surface-variant">{t("admin.speech.modelHint")}</span>
      </label>
      <SaveButton disabled={busy || model === current.model} onClick={() => put({ model })} />
      {stats && hasMl && (
        <div className="flex flex-col gap-2 rounded-md bg-surface-container p-4 text-body-md">
          <span className="flex items-center gap-2 text-title-sm">
            <Icon name="check_circle" filled className="text-success" size={20} />
            {t("admin.speech.done", { count: stats.done })}
          </span>
          {stats.waiting + stats.running > 0 && (
            <span className="tabular flex items-center gap-2 text-on-surface-variant">
              <Icon name="hourglass_top" size={20} />
              {t("admin.speech.queue", {
                waiting: number(stats.waiting),
                running: number(stats.running),
                length: formatDuration(stats.waitingSeconds),
              })}
            </span>
          )}
          {stats.failed > 0 && (
            <span className="flex items-center gap-2 text-on-surface-variant">
              <Icon name="error" size={20} className="text-error" />
              {t("admin.speech.failed", { count: stats.failed })}
            </span>
          )}
          <span className="flex items-start gap-2 text-body-sm text-on-surface-variant">
            <Icon name="info" size={20} />
            {current.enabled ? t("admin.speech.engine") : t("admin.speech.offNote")}
          </span>
        </div>
      )}
      {hasMl && current.enabled && (
        <div className="flex justify-end">
          <Button variant="outlined" icon="refresh" onClick={() => setConfirmRedo(true)}>
            {t("admin.speech.redo")}
          </Button>
        </div>
      )}
      <Dialog
        open={confirmRedo}
        onOpenChange={setConfirmRedo}
        title={t("admin.speech.redoTitle")}
        description={t("admin.speech.redoText")}
        width="sm"
        footer={
          <>
            <Button variant="text" onClick={() => setConfirmRedo(false)}>
              {t("common.cancel")}
            </Button>
            <Button onClick={() => void redo()}>{t("admin.speech.redo")}</Button>
          </>
        }
      />
    </Section>
  );
}

/** A model's name as the admin sees it: what `auto` means, and that KB-Whisper is the Swedish one. */
function modelLabel(t: ReturnType<typeof useTranslation>["t"], id: string) {
  if (id === "auto") return t("admin.speech.auto");
  const kb = /^kb-whisper-(\w+)$/.exec(id);
  return kb ? t("admin.speech.kbWhisper", { size: kb[1] }) : id;
}

type VideoSettings = AdminSettings["videos"];

/** Playable copies of videos and audio browsers can't play as they are (§14): when they're made, and their size. */
function VideosSection({ settings }: { settings: AdminSettings }) {
  const { t, i18n } = useTranslation();
  const toast = useSnackbar();
  const queryClient = useQueryClient();
  const copiesQuery = useQuery(videoCopiesQuery);
  const [copies, setCopies] = useState(settings.videos.copies);
  const [maxHeight, setMaxHeight] = useState(String(settings.videos.maxHeight));
  const { busy, save } = useSave();
  const dirty = copies !== settings.videos.copies || Number(maxHeight) !== settings.videos.maxHeight;
  const stats = copiesQuery.data;
  const whenOptions: { id: VideoSettings["copies"]; label: string; hint: string }[] = [
    { id: "background", label: t("admin.videos.background"), hint: t("admin.videos.backgroundHint") },
    { id: "onOpen", label: t("admin.videos.onOpen"), hint: t("admin.videos.onOpenHint") },
    { id: "never", label: t("admin.videos.never"), hint: t("admin.videos.neverHint") },
  ];

  const deleteCopies = async () => {
    try {
      queryClient.setQueryData(videoCopiesQuery.queryKey, await admin.deleteVideoCopies());
      toast(t("admin.videos.deleted"));
    } catch {
      toast(t("errors.generic"));
    }
  };

  return (
    <Section id="videos" intro={t("admin.videos.intro")}>
      <fieldset className="m-0 flex flex-col gap-1 border-0 p-0">
        <legend className="mb-2 text-label-lg text-on-surface">{t("admin.videos.when")}</legend>
        {whenOptions.map((option) => (
          <label
            key={option.id}
            className={`flex cursor-pointer items-start gap-3 rounded-md p-3 ${copies === option.id ? "bg-secondary-container" : "hover:bg-on-surface/8"}`}
          >
            <input
              type="radio"
              name="video-copies"
              checked={copies === option.id}
              onChange={() => setCopies(option.id)}
              className="mt-0.5 size-5 accent-primary"
            />
            <span className="flex flex-col">
              <span className="text-title-sm text-on-surface">{option.label}</span>
              <span className="text-body-sm text-on-surface-variant">{option.hint}</span>
            </span>
          </label>
        ))}
      </fieldset>
      <div className="flex flex-col gap-2">
        <span className="text-label-lg text-on-surface">{t("admin.videos.size")}</span>
        <Segmented
          label={t("admin.videos.size")}
          value={maxHeight}
          options={[
            { id: "720", label: "720p" },
            { id: "1080", label: "1080p" },
            { id: "0", label: t("admin.videos.original") },
          ]}
          onChange={setMaxHeight}
        />
        <span className="text-body-sm text-on-surface-variant">{t("admin.videos.sizeHint")}</span>
      </div>
      <SaveButton
        disabled={busy || !dirty}
        onClick={() => void save(() => admin.putVideos({ copies, maxHeight: Number(maxHeight) }))}
      />
      {stats && (
        <div className="flex flex-col gap-2 rounded-md bg-surface-container p-4 text-body-md">
          <span className="tabular flex items-center gap-2 text-title-sm">
            <Icon name="check_circle" filled className="text-success" size={20} />
            {t("admin.videos.made", { count: stats.count, size: formatBytes(stats.bytes, i18n.language) })}
          </span>
          {stats.waiting > 0 && (
            <span className="tabular flex items-center gap-2 text-on-surface-variant">
              <Icon name="hourglass_top" size={20} />
              {t("admin.videos.waiting", { count: stats.waiting })}
            </span>
          )}
          {stats.failed > 0 && (
            <span className="flex items-center gap-2 text-on-surface-variant">
              <Icon name="error" size={20} className="text-error" />
              {t("admin.videos.failed", { count: stats.failed })}
            </span>
          )}
          <span className="flex items-start gap-2 text-body-sm text-on-surface-variant">
            <Icon name="info" size={20} />
            {t("admin.videos.engine")}
          </span>
        </div>
      )}
      {stats && stats.count + stats.failed > 0 && (
        <div className="flex justify-end">
          <Button variant="outlined" icon="delete_sweep" onClick={() => void deleteCopies()}>
            {t("admin.videos.delete")}
          </Button>
        </div>
      )}
    </Section>
  );
}

/** Office previews (§10.3, §14): where Gotenberg runs, checked before saving. */
function OfficeSection({ settings }: { settings: AdminSettings }) {
  const { t } = useTranslation();
  const initial = settings.office.gotenbergUrl ?? "";
  const configured = settings.office.configured;
  const wordId = useId();
  const [url, setUrl] = useState(initial);
  const [test, setTest] = useState<ProbeResult | "testing" | null>(null);
  const { busy, save } = useSave();

  const runTest = async () => {
    setTest("testing");
    try {
      setTest(await admin.testOffice(url.trim()));
    } catch {
      setTest({ reachable: false, version: null, error: t("errors.generic") });
    }
  };

  return (
    <Section id="office" intro={t("admin.office.intro")}>
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start">
        <TextField
          label={t("admin.office.address")}
          value={url}
          onChange={(event) => {
            setUrl(event.target.value);
            setTest(null);
          }}
          placeholder="http://gotenberg:3000"
          hint={configured ? t("admin.office.configured") : t("admin.office.addressHint")}
          readOnly={configured}
          mono
          className="flex-1"
        />
        <Button variant="outlined" icon="network_check" disabled={!url.trim() || test === "testing"} onClick={() => void runTest()} className="sm:mt-6">
          {t("admin.ml.test")}
        </Button>
      </div>
      <ProbeBanner
        result={test}
        okText={(p) => t("admin.office.connected", { version: p.version })}
        failText={(e) => t("admin.office.failed", { error: e })}
      />
      {!initial && !url.trim() && <Banner tone="info">{t("admin.office.off")}</Banner>}
      {!initial && url.trim() && <Banner tone="info">{t("admin.office.backfill")}</Banner>}
      {!configured && (
        <SaveButton disabled={busy || url.trim() === initial} onClick={() => void save(() => admin.putOffice(url.trim() || null))} />
      )}
      <div className="flex items-center gap-4 border-t border-outline-variant pt-4">
        <label htmlFor={wordId} className="flex min-w-0 flex-1 cursor-pointer flex-col">
          <span className="text-title-sm">{t("admin.office.wordViewer")}</span>
          <span className="text-body-sm text-on-surface-variant">{t("admin.office.wordViewerHint")}</span>
        </label>
        <Switch
          id={wordId}
          checked={settings.wordViewer.enabled}
          onCheckedChange={(checked) => !busy && void save(() => admin.putWordViewer(checked))}
        />
      </div>
    </Section>
  );
}

function ScannerSection({ settings }: { settings: AdminSettings }) {
  const { t } = useTranslation();
  const initial = settings.scanner.address ?? "";
  const [address, setAddress] = useState(initial);
  const [test, setTest] = useState<ProbeResult | "testing" | null>(null);
  const { busy, save } = useSave();

  const runTest = async () => {
    setTest("testing");
    try {
      setTest(await admin.testScanner(address.trim()));
    } catch {
      setTest({ reachable: false, version: null, error: t("errors.generic") });
    }
  };

  return (
    <Section id="scanning" intro={t("admin.scanning.intro")}>
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start">
        <TextField
          label={t("admin.scanning.address")}
          value={address}
          onChange={(event) => {
            setAddress(event.target.value);
            setTest(null);
          }}
          placeholder="tcp://clamav:3310"
          hint={t("admin.scanning.addressHint")}
          mono
          className="flex-1"
        />
        <Button variant="outlined" icon="network_check" disabled={!address.trim() || test === "testing"} onClick={() => void runTest()} className="sm:mt-6">
          {t("admin.ml.test")}
        </Button>
      </div>
      <ProbeBanner
        result={test}
        okText={(p) => t("admin.scanning.connected", { version: p.version })}
        failText={(e) => t("admin.scanning.failed", { error: e })}
      />
      {!initial && !address && <Banner tone="warning">{t("admin.scanning.notConfigured")}</Banner>}
      {initial && <Banner tone="info">{t("admin.scanning.notWired")}</Banner>}
      <SaveButton
        disabled={busy || address.trim() === initial}
        onClick={() => void save(() => admin.putScanner(address.trim() || null))}
      />
    </Section>
  );
}

/** Trash and versions (§8.2, §12): how long deleted items and older versions are kept. */
function VersionsSection({ settings }: { settings: AdminSettings }) {
  const { t } = useTranslation();
  const [trash, setTrash] = useState(String(settings.trash.keepDays));
  const [days, setDays] = useState(String(settings.versions.keepDays));
  const [count, setCount] = useState(String(settings.versions.maxVersions));
  const { busy, save } = useSave();
  const trashDays = Number(trash);
  const keepDays = Number(days);
  const maxVersions = Number(count);
  const inDays = (n: number) => Number.isInteger(n) && n >= 1 && n <= 3650;
  const valid = inDays(trashDays) && inDays(keepDays) && Number.isInteger(maxVersions) && maxVersions >= 1 && maxVersions <= 10_000;
  const trashDirty = trashDays !== settings.trash.keepDays;
  const versionsDirty = keepDays !== settings.versions.keepDays || maxVersions !== settings.versions.maxVersions;
  const defaults = trash === "30" && days === "30" && count === "100";

  return (
    <Section id="versions" intro={t("admin.versions.intro")}>
      <NumberField label={t("admin.versions.trashFor")} unit={t("admin.versions.daysUnit")} value={trash} onChange={setTrash} />
      <div className="grid gap-5 sm:grid-cols-2">
        <NumberField label={t("admin.versions.keepFor")} unit={t("admin.versions.daysUnit")} value={days} onChange={setDays} />
        <NumberField label={t("admin.versions.atMost")} unit={t("admin.versions.versionsUnit")} value={count} onChange={setCount} />
      </div>
      {valid ? (
        <p className="m-0 flex items-start gap-2 text-body-sm text-on-surface-variant">
          <Icon name="info" size={20} />
          {t("admin.versions.applies")}
        </p>
      ) : (
        <Banner tone="error">{t("admin.versions.invalid")}</Banner>
      )}
      <div className="flex flex-wrap justify-end gap-2">
        <Button
          variant="text"
          disabled={defaults}
          onClick={() => {
            setTrash("30");
            setDays("30");
            setCount("100");
          }}
        >
          {t("admin.versions.reset")}
        </Button>
        <Button
          variant="tonal"
          icon="check"
          disabled={busy || !valid || !(trashDirty || versionsDirty)}
          onClick={() =>
            void save(async () => {
              if (trashDirty) await admin.putTrash(trashDays);
              if (versionsDirty) await admin.putVersions(keepDays, maxVersions);
            })
          }
        >
          {t("common.save")}
        </Button>
      </div>
    </Section>
  );
}

function NumberField({ label, unit, value, onChange }: { label: string; unit: string; value: string; onChange: (value: string) => void }) {
  const id = useId();
  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={id} className="text-label-lg text-on-surface">
        {label}
      </label>
      <span className="flex items-center gap-2">
        <TextField id={id} type="number" min={1} value={value} onChange={(event) => onChange(event.target.value)} className="tabular w-32" />
        {unit}
      </span>
    </div>
  );
}

function SignInSection({ settings }: { settings: AdminSettings }) {
  const { t } = useTranslation();
  const signIn = settings.signIn;
  const rows: [string, ReactNode][] = [
    [t("admin.signIn.provider"), signIn.providerName ?? "OIDC"],
    [t("admin.signIn.issuer"), <span className="font-mono text-mono-sm">{signIn.issuer}</span>],
    [
      t("admin.signIn.whoCanSignIn"),
      signIn.userGroup ? <Group name={signIn.userGroup} /> : t("admin.signIn.anyone"),
    ],
    [t("admin.signIn.admins"), <Group name={signIn.adminGroup} />],
    [
      t("admin.signIn.quota"),
      signIn.quotaClaim ? <Group name={signIn.quotaClaim} template="admin.signIn.quotaClaim" /> : t("admin.signIn.quotaHere"),
    ],
  ];
  return (
    <Section id="signIn" intro={t("admin.signIn.readOnlyIntro")}>
      {signIn.enabled ? (
        <dl className="m-0 grid gap-x-4 gap-y-2 text-body-md sm:grid-cols-[10rem_minmax(0,1fr)]">
          {rows.map(([term, value]) => (
            <div key={term} className="contents">
              <dt className="text-on-surface-variant">{term}</dt>
              <dd className="m-0 break-words text-on-surface max-sm:mb-2">{value}</dd>
            </div>
          ))}
        </dl>
      ) : (
        <Banner tone="warning">{t("admin.signIn.off")}</Banner>
      )}
    </Section>
  );
}

/** A sentence with a group or claim name from the provider set in monospace. */
function Group({ name, template = "admin.signIn.membersOf" }: { name: string; template?: "admin.signIn.membersOf" | "admin.signIn.quotaClaim" }) {
  const { t } = useTranslation();
  const [before, after] = t(template, { group: "\u0000" }).split("\u0000");
  return (
    <>
      {before}
      <span className="font-mono text-mono-sm">{name}</span>
      {after}
    </>
  );
}
