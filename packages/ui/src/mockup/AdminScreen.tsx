import { formatBytes } from "@dokument/core";
import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Avatar } from "../components/Avatar";
import { Banner } from "../components/Banner";
import { Button, IconButton } from "../components/Button";
import { Progress } from "../components/Progress";
import { useSnackbar } from "../lib/snackbar";
import { Switch } from "../components/Switch";
import { TextField } from "../components/TextField";

const sections = ["signIn", "ml", "scanning", "versions", "users"] as const;
const icons = { signIn: "login", ml: "neurology", scanning: "shield", versions: "history", users: "group" };

function Section({ id, title, icon, intro, children }: { id: string; title: string; icon: string; intro: string; children: ReactNode }) {
  return (
    <section id={`admin-${id}`} aria-labelledby={`admin-${id}-title`} className="scroll-mt-4 rounded-lg bg-surface-container-low p-4 sm:p-6">
      <h2 id={`admin-${id}-title`} className="m-0 flex items-center gap-2 text-title-lg">
        <span className="icon text-primary">{icon}</span>
        {title}
      </h2>
      <p className="mt-1 mb-5 max-w-160 text-body-md text-on-surface-variant">{intro}</p>
      <div className="flex max-w-160 flex-col gap-5">{children}</div>
    </section>
  );
}

function Toggle({ label, hint, checked, onChange }: { label: string; hint?: string; checked: boolean; onChange: (checked: boolean) => void }) {
  return (
    <label className="flex cursor-pointer items-center gap-4">
      <span className="flex min-w-0 flex-1 flex-col">
        <span className="text-body-lg text-on-surface sm:text-body-md">{label}</span>
        {hint && <span className="text-body-sm text-on-surface-variant">{hint}</span>}
      </span>
      <Switch checked={checked} onCheckedChange={onChange} />
    </label>
  );
}

const users = [
  { name: "Axel", email: "axel@hemma.se", admin: true, used: 412_000_000_000, quota: 500_000_000_000, tone: 1 as const },
  { name: "Karin Lindqvist", email: "karin@hemma.se", admin: false, used: 88_000_000_000, quota: 200_000_000_000, tone: 2 as const },
  { name: "Lennart Eriksson", email: "lennart@hemma.se", admin: false, used: 48_500_000_000, quota: 50_000_000_000, tone: 3 as const },
];

/** Server settings for admins. The ML service and the virus scanner are separate containers, set by URL. */
export function AdminScreen() {
  const { t, i18n } = useTranslation();
  const toast = useSnackbar();
  const [mlUrl, setMlUrl] = useState("http://gpu-box.lan:3003");
  const [ml, setMl] = useState<"idle" | "testing" | "ok" | "error">("idle");
  const [features, setFeatures] = useState({ ocr: true, semantic: true, transcribe: false });
  const [scanUrl, setScanUrl] = useState("");
  const [autoCreate, setAutoCreate] = useState(true);

  const test = () => {
    setMl("testing");
    window.setTimeout(() => setMl(/^https?:\/\/[^\s/]+/.test(mlUrl) ? "ok" : "error"), 1200);
  };

  return (
    <div className="flex gap-8 pt-2">
      <nav aria-label={t("admin.title")} className="sticky top-2 hidden w-56 shrink-0 self-start lg:block">
        <ul className="m-0 flex list-none flex-col gap-1 p-0">
          {sections.map((id) => (
            <li key={id}>
              <a
                href={`#admin-${id}`}
                className="flex h-10 items-center gap-3 rounded-xl px-4 font-medium text-on-surface-variant no-underline hover:bg-on-surface/8 hover:text-on-surface"
              >
                <span className="icon">{icons[id]}</span>
                {t(`admin.sections.${id}`)}
              </a>
            </li>
          ))}
        </ul>
      </nav>
      <div className="flex min-w-0 flex-1 flex-col gap-4">
        <div className="flex items-center gap-4">
          <h1 className="m-0 min-w-0 flex-1 text-headline-sm lg:text-headline-md">{t("admin.title")}</h1>
          <Button icon="check" onClick={() => toast(t("common.saved"))}>
            {t("common.save")}
          </Button>
        </div>

        <Section id="signIn" icon={icons.signIn} title={t("admin.sections.signIn")} intro={t("admin.signIn.intro")}>
          <TextField label={t("admin.signIn.issuer")} defaultValue="https://auth.hemma.se/application/o/dokumentsystem/" />
          <TextField label={t("admin.signIn.clientId")} defaultValue="dokumentsystem" mono />
          <TextField label={t("admin.signIn.clientSecret")} type="password" placeholder="••••••••••••" hint={t("admin.signIn.secretSet")} />
          <TextField label={t("admin.signIn.adminGroup")} defaultValue="dokument-admins" hint={t("admin.signIn.adminGroupHint")} />
          <Toggle label={t("admin.signIn.autoCreate")} hint={t("admin.signIn.autoCreateHint")} checked={autoCreate} onChange={setAutoCreate} />
        </Section>

        <Section id="ml" icon={icons.ml} title={t("admin.sections.ml")} intro={t("admin.ml.intro")}>
          <div className="flex flex-col gap-3 sm:flex-row sm:items-start">
            <TextField
              label={t("admin.ml.url")}
              value={mlUrl}
              onChange={(event) => {
                setMlUrl(event.target.value);
                setMl("idle");
              }}
              hint={t("admin.ml.urlHint")}
              mono
              className="flex-1"
            />
            <Button variant="outlined" icon="network_check" onClick={test} disabled={ml === "testing" || !mlUrl} className="sm:mt-6">
              {t("admin.ml.test")}
            </Button>
          </div>
          {ml === "testing" && <Progress label={t("admin.ml.testing")} />}
          {ml === "ok" && (
            <Banner tone="success">{t("admin.ml.connected", { version: "1.4.0", models: "PaddleOCR, CLIP ViT-B/32, Whisper small" })}</Banner>
          )}
          {ml === "error" && <Banner tone="error">{t("admin.ml.unreachable")}</Banner>}
          <fieldset className="m-0 flex flex-col gap-4 border-0 p-0" disabled={!mlUrl}>
            <legend className="mb-3 text-title-sm text-on-surface">{t("admin.ml.features")}</legend>
            <Toggle label={t("admin.ml.ocr")} checked={features.ocr} onChange={(ocr) => setFeatures({ ...features, ocr })} />
            <Toggle label={t("admin.ml.semantic")} checked={features.semantic} onChange={(semantic) => setFeatures({ ...features, semantic })} />
            <Toggle label={t("admin.ml.transcribe")} checked={features.transcribe} onChange={(transcribe) => setFeatures({ ...features, transcribe })} />
          </fieldset>
        </Section>

        <Section id="scanning" icon={icons.scanning} title={t("admin.sections.scanning")} intro={t("admin.scanning.intro")}>
          <TextField label={t("admin.scanning.url")} value={scanUrl} onChange={(event) => setScanUrl(event.target.value)} placeholder="tcp://clamav:3310" hint={t("admin.scanning.urlHint")} mono />
          {!scanUrl && <Banner tone="warning">{t("admin.scanning.notConfigured")}</Banner>}
        </Section>

        <Section id="versions" icon={icons.versions} title={t("admin.sections.versions")} intro={t("admin.versions.intro")}>
          <div className="grid gap-5 sm:grid-cols-3">
            <TextField label={t("admin.versions.days")} type="number" defaultValue={30} min={1} className="tabular" />
            <TextField label={t("admin.versions.count")} type="number" defaultValue={100} min={1} className="tabular" />
            <TextField label={t("admin.versions.trashDays")} type="number" defaultValue={30} min={1} className="tabular" />
          </div>
        </Section>

        <Section id="users" icon={icons.users} title={t("admin.sections.users")} intro={t("admin.users.intro")}>
          <ul className="m-0 flex list-none flex-col divide-y divide-outline-variant p-0">
            {users.map((user) => {
              const percent = Math.round((user.used / user.quota) * 100);
              return (
                <li key={user.email} className="flex items-center gap-3 py-3">
                  <Avatar name={user.name} tone={user.tone} />
                  <div className="flex min-w-0 flex-1 flex-col gap-1">
                    <span className="flex flex-wrap items-center gap-2">
                      <span className="truncate font-medium text-on-surface">{user.name}</span>
                      <span className="rounded-xs bg-secondary-container px-1.5 text-label-sm text-on-secondary-container uppercase">
                        {user.admin ? t("admin.users.admin") : t("admin.users.member")}
                      </span>
                    </span>
                    <Progress value={percent} tone={percent >= 95 ? "error" : percent >= 80 ? "warning" : "primary"} label={t("storage.title")} className="max-w-60" />
                    <span className="tabular text-body-sm text-on-surface-variant">
                      {t("storage.used", { used: formatBytes(user.used, i18n.language), quota: formatBytes(user.quota, i18n.language) })}
                    </span>
                  </div>
                  <IconButton icon="edit" label={t("admin.users.edit", { name: user.name })} onClick={() => toast(t("toasts.notInMockup"))} />
                </li>
              );
            })}
          </ul>
        </Section>
      </div>
    </div>
  );
}
