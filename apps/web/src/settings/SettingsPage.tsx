import { Icon, Switch, useFormat } from "@dokument/ui";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useAccountSettings } from "./account";

/**
 * The person's own settings (§16.5, §23), opened from the account menu. Everything here saves as soon as it
 * changes and is stored on the account, so it follows the person to every device.
 */
export function SettingsPage() {
  const { t, i18n } = useTranslation();
  const format = useFormat();
  const settings = useAccountSettings();
  const names = { sv: "Svenska", en: "English" } as const;
  const current = i18n.language === "sv" ? "sv" : "en";
  const languages = [
    {
      id: "browser" as const,
      label: t("account.languageBrowser"),
      hint: t("account.languageNow", { language: names[current] }),
    },
    { id: "sv" as const, label: names.sv, lang: "sv" },
    { id: "en" as const, label: names.en, lang: "en" },
  ];
  const chosen = settings.language ?? "browser";
  // A fixed example, so the hint shows what the switch changes rather than the time the page opened.
  const example = new Date();
  example.setHours(9, 14, 7);

  return (
    <main className="min-w-0 flex-1 overflow-y-auto px-4 pb-12 sm:px-6 lg:px-8">
      <div className="mx-auto flex max-w-220 flex-col gap-4 pt-2 lg:mx-0">
        <h1 className="m-0 text-headline-sm lg:text-headline-md">{t("settings.title")}</h1>
        <Section icon="language" title={t("account.language")}>
          <fieldset className="m-0 flex flex-col gap-1 border-0 p-0">
            <legend className="sr-only">{t("account.language")}</legend>
            {languages.map((option) => (
              <label
                key={option.id}
                lang={option.lang}
                className={`flex cursor-pointer items-start gap-3 rounded-md p-3 ${chosen === option.id ? "bg-secondary-container" : "hover:bg-on-surface/8"}`}
              >
                <input
                  type="radio"
                  name="language"
                  checked={chosen === option.id}
                  onChange={() => void settings.setLanguage(option.id)}
                  className="mt-0.5 size-5 accent-primary"
                />
                <span className="flex flex-col">
                  <span className="text-title-sm text-on-surface">{option.label}</span>
                  {option.hint && <span className="text-body-sm text-on-surface-variant">{option.hint}</span>}
                </span>
              </label>
            ))}
          </fieldset>
        </Section>
        <Section icon="schedule" title={t("settings.time.title")}>
          <div className="flex items-center gap-4">
            <label htmlFor="show-seconds" className="flex min-w-0 flex-1 cursor-pointer flex-col">
              <span className="text-title-sm text-on-surface">{t("settings.time.seconds")}</span>
              <span className="tabular text-body-sm text-on-surface-variant">
                {t("settings.time.example", { example: format.date(example) })}
              </span>
            </label>
            <Switch
              id="show-seconds"
              checked={settings.showSeconds}
              onCheckedChange={(value) => void settings.setShowSeconds(value)}
            />
          </div>
        </Section>
      </div>
    </main>
  );
}

function Section({ icon, title, children }: { icon: string; title: string; children: ReactNode }) {
  return (
    <section aria-label={title} className="rounded-lg bg-surface-container-low p-4 sm:p-6">
      <h2 className="m-0 mb-4 flex items-center gap-2 text-title-lg">
        <Icon name={icon} className="text-primary" />
        {title}
      </h2>
      {children}
    </section>
  );
}
