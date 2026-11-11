/** The placeholders a filing rule's folder may hold, as the server reads them (docs/design.md §13.6). */
const placeholder = /\{[^{}]*\}/g;

/** Why a folder template won't do, checked as the person types; the server checks names too. */
export function folderProblem(template: string): "empty" | "placeholder" | null {
  if (template.split("/").every((folder) => folder.trim() === "")) return "empty";
  const unknown = [...template.matchAll(placeholder)].some((m) => !/^\{(year|month)\}$/i.test(m[0]));
  return unknown || /[{}]/.test(template.replace(placeholder, "")) ? "placeholder" : null;
}

/** "Kvitton/{year}" as "Kvitton / 2026", for this month. */
export function exampleFolder(template: string, now = new Date()): string {
  return template
    .split("/")
    .map((folder) => folder.trim())
    .filter(Boolean)
    .map((folder) =>
      folder.replace(placeholder, (m) =>
        m.toLowerCase() === "{year}" ? String(now.getFullYear()) : String(now.getMonth() + 1).padStart(2, "0"),
      ),
    )
    .join(" / ");
}

/** "Kvitton/{year}" as a list shows it, "Kvitton / Year", with the placeholders in the person's language. */
export function templateLabel(template: string, words: { year: string; month: string }): string {
  return template
    .split("/")
    .map((folder) =>
      folder
        .trim()
        .replace(/\{(year|month)\}/gi, (_, part: string) => (part.toLowerCase() === "year" ? words.year : words.month)),
    )
    .filter(Boolean)
    .join(" / ");
}

/**
 * The template for a folder picked in My files, keeping the placeholders that ended the old one, so picking
 * "Kvitton" for "Papper/{year}" gives "Kvitton/{year}".
 */
export function pickedTemplate(folders: string[], previous: string): string {
  const parts = previous
    .split("/")
    .map((part) => part.trim())
    .filter(Boolean);
  let first = parts.length;
  while (first > 0 && /^\{(year|month)\}$/i.test(parts[first - 1]!)) first--;
  return [...folders, ...parts.slice(first)].join("/");
}
