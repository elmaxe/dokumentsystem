import { Fragment } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "../lib/cn";
import { Icon } from "./Icon";

export type Crumb = { id: string; name: string };

export type BreadcrumbsProps = {
  path: Crumb[];
  onNavigate: (id: string) => void;
  className?: string;
};

/** The folder path. The last crumb is the page title; earlier ones collapse to "…" on phones. */
export function Breadcrumbs({ path, onNavigate, className }: BreadcrumbsProps) {
  const { t } = useTranslation();
  const last = path.length - 1;
  return (
    <nav aria-label={t("files.breadcrumbs")} className={cn("min-w-0", className)}>
      <ol className="m-0 flex min-w-0 list-none items-center gap-1 p-0">
        {path.map((crumb, index) => (
          <Fragment key={crumb.id}>
            {index > 0 && (
              <li
                aria-hidden
                className={cn(
                  "flex text-on-surface-variant",
                  index < last - 1 && "max-sm:hidden",
                )}
              >
                <Icon name="chevron_right" size="sm" />
              </li>
            )}
            <li
              className={cn(
                "min-w-0",
                index === last ? "shrink" : "shrink-[2]",
                index < last - 1 && "max-sm:hidden",
              )}
            >
              {index === last ? (
                <h1
                  aria-current="page"
                  className="m-0 truncate text-headline-sm text-on-surface lg:text-headline-md"
                >
                  {crumb.name}
                </h1>
              ) : (
                <button
                  type="button"
                  onClick={() => onNavigate(crumb.id)}
                  className="block max-w-full cursor-pointer truncate rounded-sm px-1 text-title-md font-medium text-on-surface-variant hover:bg-on-surface/8 hover:text-on-surface"
                >
                  {index === last - 1 && last > 1 ? (
                    <>
                      <span className="sm:hidden">…</span>
                      <span className="max-sm:hidden">{crumb.name}</span>
                    </>
                  ) : (
                    crumb.name
                  )}
                </button>
              )}
            </li>
          </Fragment>
        ))}
      </ol>
    </nav>
  );
}
