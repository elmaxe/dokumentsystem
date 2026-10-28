/** A server address as people type it, made into a base URL (docs/design.md §15.3). */
export type AddressResult =
  | { ok: true; url: string }
  | { ok: false; error: "empty" | "invalid" | "insecure" };

/**
 * Accepts "files.example.com", "https://files.example.com/" or "http://192.168.1.20:8080/dokument", and returns the
 * base URL without a trailing slash. Without a scheme, https is assumed. Plain http is refused unless `allowHttp`:
 * the local address may be http, the main address only in development.
 */
export function parseAddress(input: string, { allowHttp }: { allowHttp: boolean }): AddressResult {
  const trimmed = input.trim();
  if (trimmed.length === 0) return { ok: false, error: "empty" };
  const withScheme = /^[a-z][a-z0-9+.-]*:\/\//i.test(trimmed) ? trimmed : `https://${trimmed}`;
  let url: URL;
  try {
    url = new URL(withScheme);
  } catch {
    return { ok: false, error: "invalid" };
  }
  if (url.protocol !== "https:" && url.protocol !== "http:") return { ok: false, error: "invalid" };
  if (!url.hostname || url.username || url.password || url.search || url.hash) return { ok: false, error: "invalid" };
  if (url.protocol === "http:" && !allowHttp) return { ok: false, error: "insecure" };
  const path = url.pathname.replace(/\/+$/, "");
  return { ok: true, url: `${url.protocol}//${url.host}${path}` };
}

/** "files.example.com" or "192.168.1.20:8080/dokument": the address as people read it. */
export const displayAddress = (url: string) => url.replace(/^https?:\/\//, "");
