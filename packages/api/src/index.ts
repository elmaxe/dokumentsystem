import createClient from "openapi-fetch";
import type { components, paths } from "./schema";

export type { components, paths };
export type ServerInfo = components["schemas"]["ServerInfo"];

/** A client for the Dokumentsystem REST API. Web uses same-origin cookies; mobile passes a base URL and bearer token. */
export function createApiClient(options: { baseUrl?: string; token?: string } = {}) {
  return createClient<paths>({
    baseUrl: options.baseUrl ?? "",
    credentials: "include",
    headers: options.token ? { Authorization: `Bearer ${options.token}` } : undefined,
  });
}

export type ApiClient = ReturnType<typeof createApiClient>;
