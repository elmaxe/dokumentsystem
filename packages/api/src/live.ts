import { HttpTransportType, HubConnectionBuilder, LogLevel, type HubConnection } from "@microsoft/signalr";

/** The live updates hub (docs/design.md §15.4). */
export const livePath = "/api/v1/live";

/** Sent with mutations: the server names it as the origin of the change, so this client can skip its own notices. */
export const liveClientHeader = "X-Dokument-Client";

/** What changed, broadly. Each says which data to fetch again; notices never carry ids or names. */
export type LiveTopic = "files" | "recent" | "trash" | "storage" | "account";

export type LiveChanged = { topics: LiveTopic[]; origin?: string | null };

/** "signedOut": signed out in this browser, perhaps in another tab. "revoked": ended from another device. */
export type LiveSignedOut = { reason: "signedOut" | "revoked" };

/** Retries for as long as the client is open: at once, then backing off to every 30 s. */
const retryDelays = [0, 2_000, 5_000, 10_000, 30_000];

export const liveRetryDelay = (previousRetries: number) =>
  retryDelays[Math.min(previousRetries, retryDelays.length - 1)]!;

/**
 * A connection to the live updates hub. Web passes the session's CSRF header, which SignalR's own POSTs need like any
 * other mutation; mobile passes a base URL and bearer token. Call `start()` once handlers are attached.
 */
export function createLiveConnection(
  options: { baseUrl?: string; token?: string; headers?: Record<string, string> } = {},
): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(`${options.baseUrl ?? ""}${livePath}`, {
      transport: HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
      withCredentials: true,
      headers: options.headers,
      accessTokenFactory: options.token ? () => options.token! : undefined,
    })
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (context) => liveRetryDelay(context.previousRetryCount) })
    .configureLogging(LogLevel.Warning)
    .build();
}
