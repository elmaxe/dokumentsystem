import createClient, { type Middleware } from "openapi-fetch";
import type { components, paths } from "./schema";

export type { components, paths };
export type ServerInfo = components["schemas"]["ServerInfo"];
export type AuthConfig = components["schemas"]["AuthConfig"];
export type CurrentSession = components["schemas"]["CurrentSession"];
export type UserProfile = components["schemas"]["UserProfile"];
export type SessionInfo = components["schemas"]["SessionInfo"];

/** The header that carries the session's CSRF token on mutations made with the session cookie (docs/design.md §5). */
export const csrfHeader = "X-CSRF-Token";

/** Where the browser goes to sign in. It's a page navigation, not a fetch, because it redirects to the IdP. */
export function loginUrl(returnTo: string, baseUrl = ""): string {
  return `${baseUrl}/api/v1/auth/login?returnTo=${encodeURIComponent(returnTo)}`;
}

/**
 * A client for the Dokumentsystem REST API. Web uses same-origin cookies and passes `csrfToken`, which is read
 * before each mutation; mobile passes a base URL and bearer token.
 */
export function createApiClient(
  options: { baseUrl?: string; token?: string; csrfToken?: () => string | null | undefined } = {},
) {
  const client = createClient<paths>({
    baseUrl: options.baseUrl ?? "",
    credentials: "include",
    headers: options.token ? { Authorization: `Bearer ${options.token}` } : undefined,
  });

  const { csrfToken } = options;
  if (csrfToken) {
    const csrf: Middleware = {
      onRequest({ request }) {
        const token = csrfToken();
        if (token && !["GET", "HEAD", "OPTIONS"].includes(request.method)) {
          request.headers.set(csrfHeader, token);
        }
        return request;
      },
    };
    client.use(csrf);
  }

  return client;
}

export type ApiClient = ReturnType<typeof createApiClient>;
