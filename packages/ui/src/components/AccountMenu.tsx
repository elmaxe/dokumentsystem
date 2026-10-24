import { DropdownMenu } from "radix-ui";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Icon } from "./Icon";

/** "browser" follows the browser's language; the others are the UI languages (§23). */
export type LanguageChoice = "browser" | "sv" | "en";

export type AccountMenuProps = {
  trigger: ReactNode;
  name: string;
  email?: string | null;
  language: LanguageChoice;
  /** The language the UI is in now, shown next to "Same as browser". */
  currentLanguage: "sv" | "en";
  onLanguageChange: (language: LanguageChoice) => void;
  /** Times to the second ("09:14:07"); saved per person so it follows them to every device. */
  showSeconds: boolean;
  onShowSecondsChange: (showSeconds: boolean) => void;
  /**
   * Trash, for narrow screens: the bottom nav has no room for it (§16.3). Hidden where the nav rail shows it.
   */
  onTrash?: () => void;
  /** Shown to admins only. */
  onAdmin?: () => void;
  onSignOut: () => void;
};

const content =
  "z-50 min-w-60 overflow-hidden rounded-sm bg-surface-container py-2 text-on-surface shadow-elevation-2";
const item =
  "flex h-control cursor-pointer items-center gap-3 px-4 text-body-md outline-none select-none data-highlighted:bg-on-surface/8 data-[state=open]:bg-on-surface/8 pointer-coarse:h-touch pointer-coarse:text-body-lg";
const separator = "my-2 h-px bg-outline-variant";

/**
 * The menu behind the avatar: who is signed in, their language and display settings, Trash on phones, admin
 * settings and sign-out.
 */
export function AccountMenu({
  trigger,
  name,
  email,
  language,
  currentLanguage,
  onLanguageChange,
  showSeconds,
  onShowSecondsChange,
  onTrash,
  onAdmin,
  onSignOut,
}: AccountMenuProps) {
  const { t } = useTranslation();
  const names: Record<"sv" | "en", string> = { sv: "Svenska", en: "English" };
  const options: { id: LanguageChoice; label: string; hint?: string; lang?: string }[] = [
    {
      id: "browser",
      label: t("account.languageBrowser"),
      hint: t("account.languageNow", { language: names[currentLanguage] }),
    },
    { id: "sv", label: names.sv, lang: "sv" },
    { id: "en", label: names.en, lang: "en" },
  ];
  return (
    <DropdownMenu.Root modal={false}>
      <DropdownMenu.Trigger asChild>{trigger}</DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="end" sideOffset={4} collisionPadding={8} className={content}>
          <DropdownMenu.Label className="flex flex-col px-4 pt-1 pb-3">
            <span className="text-title-sm">{name}</span>
            {email && <span className="text-body-sm text-on-surface-variant">{email}</span>}
          </DropdownMenu.Label>
          <DropdownMenu.Separator className={separator} />
          <DropdownMenu.Sub>
            <DropdownMenu.SubTrigger className={item}>
              <Icon name="language" className="text-on-surface-variant" />
              <span className="flex-1">{t("account.language")}</span>
              <span className="text-on-surface-variant">
                {language === "browser" ? t("account.languageBrowserShort") : names[language]}
              </span>
              <Icon name="chevron_right" className="text-on-surface-variant" />
            </DropdownMenu.SubTrigger>
            <DropdownMenu.Portal>
              <DropdownMenu.SubContent sideOffset={4} collisionPadding={8} className={content}>
                <DropdownMenu.RadioGroup
                  value={language}
                  onValueChange={(value) => onLanguageChange(value as LanguageChoice)}
                >
                  {options.map((option) => (
                    <DropdownMenu.RadioItem
                      key={option.id}
                      value={option.id}
                      lang={option.lang}
                      className={`${item} ${option.hint ? "h-auto py-2" : ""}`}
                    >
                      <span className="size-6 shrink-0">
                        <DropdownMenu.ItemIndicator>
                          <Icon name="check" className="text-primary" />
                        </DropdownMenu.ItemIndicator>
                      </span>
                      <span className="flex flex-col">
                        {option.label}
                        {option.hint && (
                          <span className="text-body-sm text-on-surface-variant">{option.hint}</span>
                        )}
                      </span>
                    </DropdownMenu.RadioItem>
                  ))}
                </DropdownMenu.RadioGroup>
              </DropdownMenu.SubContent>
            </DropdownMenu.Portal>
          </DropdownMenu.Sub>
          <DropdownMenu.CheckboxItem
            checked={showSeconds}
            onCheckedChange={onShowSecondsChange}
            onSelect={(event) => event.preventDefault()}
            className={`group ${item}`}
          >
            <Icon name="schedule" className="text-on-surface-variant" />
            <span className="flex-1">{t("account.showSeconds")}</span>
            {/* Drawn like Switch; the menu item itself is the checkbox screen readers hear. */}
            <span
              aria-hidden
              className="relative inline-flex h-6 w-10 shrink-0 items-center rounded-full border-2 border-outline bg-surface-container-highest transition-colors duration-(--duration-medium) group-data-[state=checked]:border-primary group-data-[state=checked]:bg-primary"
            >
              <span className="block size-3 translate-x-1 rounded-full bg-outline transition-all duration-(--duration-medium) ease-emphasized group-data-[state=checked]:size-4 group-data-[state=checked]:translate-x-4.5 group-data-[state=checked]:bg-on-primary" />
            </span>
          </DropdownMenu.CheckboxItem>
          {onTrash && (
            <DropdownMenu.Item onSelect={onTrash} className={`${item} md:hidden`}>
              <Icon name="delete" className="text-on-surface-variant" />
              {t("nav.trash")}
            </DropdownMenu.Item>
          )}
          {onAdmin && (
            <DropdownMenu.Item onSelect={onAdmin} className={item}>
              <Icon name="admin_panel_settings" className="text-on-surface-variant" />
              {t("account.admin")}
            </DropdownMenu.Item>
          )}
          <DropdownMenu.Separator className={separator} />
          <DropdownMenu.Item onSelect={onSignOut} className={item}>
            <Icon name="logout" className="text-on-surface-variant" />
            {t("auth.signOut")}
          </DropdownMenu.Item>
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}
