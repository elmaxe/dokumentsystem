import { createApiClient, type ApiClient, type Problem } from "@dokument/api";
import * as Crypto from "expo-crypto";

/** This app, as live notices name it: it skips the notices about its own changes (docs/design.md §15.4). */
export const clientId = Crypto.randomUUID();

type Connection = { client: ApiClient; baseUrl: string; token: string };

let connection: Connection | null = null;
let onUnauthorized: (() => void) | null = null;

/**
 * Points the API at an address with the bearer session. Called on sign-in and whenever the app switches between the
 * main and local address (§15.3); queries made after it use the new address.
 */
export function connect(baseUrl: string, token: string) {
  const client = createApiClient({ baseUrl, token, clientId });
  client.use({
    onResponse({ response }) {
      // The session was ended somewhere else or expired: the app goes back to signing in.
      if (response.status === 401) onUnauthorized?.();
      return response;
    },
  });
  connection = { client, baseUrl, token };
}

export function disconnect() {
  connection = null;
}

/** Called once when the server stops accepting the session. */
export function setUnauthorizedHandler(handler: (() => void) | null) {
  onUnauthorized = handler;
}

function current(): Connection {
  if (!connection) throw new Error("Not signed in");
  return connection;
}

export const api = () => current().client;

/** The address in use now, for URLs that load outside the API client: thumbnails, downloads, live updates. */
export const baseUrl = () => current().baseUrl;

/** The header that sends the session with requests made outside the API client. */
export const authHeaders = () => ({ Authorization: `Bearer ${current().token}` });

/** A refused API request, with the server's problem details. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: Problem = {},
  ) {
    super(problem.detail ?? problem.title ?? `Request failed with ${status}`);
  }
}

/** For requests that answer 204 No Content. */
export async function send(request: Promise<{ error?: unknown; response: Response }>): Promise<void> {
  const { error, response } = await request;
  if (!response.ok) throw new ApiError(response.status, (error ?? {}) as Problem);
}

export async function unwrap<T>(request: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await request;
  if (!response.ok || data === undefined) throw new ApiError(response.status, (error ?? {}) as Problem);
  return data;
}
