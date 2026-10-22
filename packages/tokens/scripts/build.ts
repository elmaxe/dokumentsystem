// Generates the web and mobile themes from the single source of truth, docs/design-system/tokens.json.
//
//   dist/theme.css          Tailwind v4 theme. Light values in @theme, dark values override the same
//                           variables under [data-theme="dark"] and prefers-color-scheme (docs/design.md §16.2).
//   dist/index.js (+ .d.ts) The same tokens as typed objects, plus per-theme CSS variable maps for
//                           NativeWind's vars() on mobile.
//   dist/tailwind-preset.js A JS preset for Tailwind-config-based setups (NativeWind).

import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

type Themed = { light: string; dark: string };
type Named<T> = { name: string; value: T };
type TypeStyle = {
  name: string;
  fontSize: string;
  lineHeight: string;
  fontWeight: number;
  letterSpacing?: string;
};
type Source = {
  // A colour is either themed or an alias such as "{primary}".
  color: { tokens: Named<Themed | string>[] };
  type: { families: Record<string, string>; groups: { family: string; styles: TypeStyle[] }[] };
  spacing: { tokens: Named<string>[] };
  radius: { tokens: Named<string>[] };
  shadow: { tokens: Named<Themed>[] };
  size: { tokens: Named<string>[] };
  duration: { note: string; tokens: Named<string>[] };
};

const here = dirname(fileURLToPath(import.meta.url));
const sourcePath = join(here, "../../../docs/design-system/tokens.json");
const outDir = join(here, "../dist");
const source = JSON.parse(readFileSync(sourcePath, "utf8")) as Source;

const colors: Named<Themed>[] = source.color.tokens.map(({ name, value }) => {
  if (typeof value !== "string") return { name, value };
  const target = source.color.tokens.find((t) => `{${t.name}}` === value)?.value;
  if (!target || typeof target === "string")
    throw new Error(`Cannot resolve colour alias ${name}: ${value}`);
  return { name, value: target };
});

const header = "Generated from docs/design-system/tokens.json by packages/tokens. Do not edit.";
const strip = (name: string, prefix: string) => name.replace(new RegExp(`^${prefix}-`), "");

// The spacing tokens are a 4px grid (space-N = N × 4px), which is exactly Tailwind v4's --spacing multiplier.
const grid = 4;
for (const { name, value } of source.spacing.tokens) {
  const step = Number(strip(name, "space"));
  if (Number.parseFloat(value) !== step * grid) {
    throw new Error(`${name} is ${value}, but the 4px grid expects ${step * grid}px`);
  }
}

const easing = {
  emphasized: "cubic-bezier(0.2, 0, 0, 1)",
  "emphasized-accelerate": "cubic-bezier(0.3, 0, 1, 1)",
};

const colorVars = (theme: keyof Themed) =>
  colors.map((t) => [`--color-${t.name}`, t.value[theme]] as const);
const shadowVars = (theme: keyof Themed) =>
  source.shadow.tokens.map((t) => [`--shadow-${t.name}`, t.value[theme]] as const);
const themeVars = (theme: keyof Themed) =>
  Object.fromEntries([...colorVars(theme), ...shadowVars(theme)]);

const typeStyles = source.type.groups.flatMap((g) => g.styles);
const decl = (entries: Iterable<readonly [string, string | number]>, indent = "  ") =>
  [...entries].map(([k, v]) => `${indent}${k}: ${v};`).join("\n");

const themeCss = `/* ${header} */

@theme {
${decl([
  ...colorVars("light"),
  ...Object.entries(source.type.families).map(([k, v]) => [`--font-${k}`, v] as const),
  ...typeStyles.flatMap((s) => [
    [`--text-${s.name}`, s.fontSize] as const,
    [`--text-${s.name}--line-height`, s.lineHeight] as const,
    [`--text-${s.name}--font-weight`, s.fontWeight] as const,
    ...(s.letterSpacing ? [[`--text-${s.name}--letter-spacing`, s.letterSpacing] as const] : []),
  ]),
  ["--spacing", `${grid}px`],
  ...source.size.tokens.map((t) => [`--spacing-${strip(t.name, "size")}`, t.value] as const),
  ...source.radius.tokens.map((t) => [`--radius-${strip(t.name, "radius")}`, t.value] as const),
  ...shadowVars("light"),
  ...Object.entries(easing).map(([k, v]) => [`--ease-${k}`, v] as const),
  ...source.duration.tokens.map((t) => [`--${t.name}`, t.value] as const),
])}
}

:root {
  color-scheme: light;
}

@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    color-scheme: dark;
${decl(Object.entries(themeVars("dark")), "    ")}
  }
}

:root[data-theme="dark"] {
  color-scheme: dark;
${decl(Object.entries(themeVars("dark")))}
}

@media (prefers-reduced-motion: reduce) {
  :root {
${decl(
  source.duration.tokens.map((t) => [`--${t.name}`, "0ms"] as const),
  "    ",
)}
  }
}
`;

const tokens = {
  color: Object.fromEntries(colors.map((t) => [t.name, t.value])),
  fontFamily: source.type.families,
  typography: Object.fromEntries(typeStyles.map(({ name, ...style }) => [name, style])),
  spacing: Object.fromEntries(source.spacing.tokens.map((t) => [t.name, t.value])),
  size: Object.fromEntries(source.size.tokens.map((t) => [t.name, t.value])),
  radius: Object.fromEntries(source.radius.tokens.map((t) => [t.name, t.value])),
  shadow: Object.fromEntries(source.shadow.tokens.map((t) => [t.name, t.value])),
  duration: Object.fromEntries(source.duration.tokens.map((t) => [t.name, t.value])),
  easing,
};

const indexJs = `// ${header}
export const tokens = ${JSON.stringify(tokens, null, 2)};

/** CSS variables per theme, for NativeWind's vars() or inline styles. */
export const themeVars = ${JSON.stringify({ light: themeVars("light"), dark: themeVars("dark") }, null, 2)};
`;

const union = (names: string[]) => names.map((n) => JSON.stringify(n)).join(" | ");
const indexDts = `// ${header}
type Themed = { light: string; dark: string };
export type ColorToken = ${union(colors.map((t) => t.name))};
export type TypeStyleToken = ${union(typeStyles.map((s) => s.name))};
export declare const tokens: {
  color: Record<ColorToken, Themed>;
  fontFamily: Record<${union(Object.keys(source.type.families))}, string>;
  typography: Record<TypeStyleToken, { fontSize: string; lineHeight: string; fontWeight: number; letterSpacing?: string }>;
  spacing: Record<${union(source.spacing.tokens.map((t) => t.name))}, string>;
  size: Record<${union(source.size.tokens.map((t) => t.name))}, string>;
  radius: Record<${union(source.radius.tokens.map((t) => t.name))}, string>;
  shadow: Record<${union(source.shadow.tokens.map((t) => t.name))}, Themed>;
  duration: Record<${union(source.duration.tokens.map((t) => t.name))}, string>;
  easing: Record<${union(Object.keys(easing))}, string>;
};
export declare const themeVars: Record<"light" | "dark", Record<string, string>>;
`;

const preset = {
  theme: {
    extend: {
      colors: Object.fromEntries(colors.map((t) => [t.name, `var(--color-${t.name})`])),
      fontFamily: Object.fromEntries(
        Object.entries(source.type.families).map(([k, v]) => [k, v.split(/,\s*/)]),
      ),
      fontSize: Object.fromEntries(
        typeStyles.map((s) => [
          s.name,
          [
            s.fontSize,
            {
              lineHeight: s.lineHeight,
              fontWeight: String(s.fontWeight),
              ...(s.letterSpacing ? { letterSpacing: s.letterSpacing } : {}),
            },
          ],
        ]),
      ),
      spacing: Object.fromEntries(source.size.tokens.map((t) => [strip(t.name, "size"), t.value])),
      borderRadius: Object.fromEntries(
        source.radius.tokens.map((t) => [strip(t.name, "radius"), t.value]),
      ),
      boxShadow: Object.fromEntries(
        source.shadow.tokens.map((t) => [t.name, `var(--shadow-${t.name})`]),
      ),
    },
  },
};

const presetJs = `// ${header}
export default ${JSON.stringify(preset, null, 2)};
`;
const presetDts = `// ${header}
declare const preset: { theme: { extend: Record<string, Record<string, unknown>> } };
export default preset;
`;

mkdirSync(outDir, { recursive: true });
writeFileSync(join(outDir, "theme.css"), themeCss);
writeFileSync(join(outDir, "index.js"), indexJs);
writeFileSync(join(outDir, "index.d.ts"), indexDts);
writeFileSync(join(outDir, "tailwind-preset.js"), presetJs);
writeFileSync(join(outDir, "tailwind-preset.d.ts"), presetDts);
console.log(`tokens: wrote ${colors.length} colours and ${typeStyles.length} type styles to dist/`);
