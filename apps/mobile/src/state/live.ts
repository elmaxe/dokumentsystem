import { createLiveConnection, liveRetryDelay, type LiveChanged, type LiveSignedOut, type LiveTopic } from "@dokument/api";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useSyncExternalStore } from "react";
import { AppState } from "react-native";
import { clientId } from "./api";
import { useSession } from "./session";

/** What each topic refetches, as on the web. Only queries on screen fetch at once; the rest are marked stale. */
const topicQueries: Record<LiveTopic, string[][]> = {
  files: [["folder"], ["node"], ["starred"], ["recent"]],
  recent: [["recent"]],
  trash: [["trash"]],
  storage: [["storage"]],
  account: [["auth", "session"], ["storage"]],
  filings: [["filing-rules"]],
  shares: [["shared"], ["access"], ["folder"], ["node"], ["search"]],
};

const shareListeners = new Set<() => void>();
const filingListeners = new Set<() => void>();

/** Calls back on each notice that something was shared, changed or unshared with the person. */
export function onShares(listener: () => void) {
  shareListeners.add(listener);
  return () => void shareListeners.delete(listener);
}

/** Calls back on each notice that a filing rule moved some of the person's files (§13.6). */
export function onFilings(listener: () => void) {
  filingListeners.add(listener);
  return () => void filingListeners.delete(listener);
}

let connected = false;
const listeners = new Set<() => void>();
const setConnected = (next: boolean) => {
  connected = next;
  listeners.forEach((listener) => listener());
};

/** True while changes arrive by themselves. */
export const useLiveConnected = () =>
  useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => void listeners.delete(listener);
    },
    () => connected,
  );

/**
 * Keeps the app up to date with changes made elsewhere (docs/design.md §15.4) while it's in front. In the background
 * the connection closes; coming back fetches what's on screen again, since notices sent meanwhile are gone.
 */
export function useLiveUpdates() {
  const queryClient = useQueryClient();
  const { state, sessionEnded } = useSession();
  const address = state.status === "signedIn" ? (state.route.using === "local" ? state.server.localUrl : state.server.mainUrl) : null;
  const token = state.status === "signedIn" ? state.session.token : null;
  const foreground = useForeground();
  const sessionEndedRef = useRef(sessionEnded);
  useEffect(() => {
    sessionEndedRef.current = sessionEnded;
  }, [sessionEnded]);

  useEffect(() => {
    if (!address || !token || !foreground) return;
    const connection = createLiveConnection({ baseUrl: address, token });
    let stopped = false;
    let missed = true;
    let failures = 0;
    let retry: ReturnType<typeof setTimeout> | undefined;

    const offline = () => {
      missed = true;
      setConnected(false);
    };
    const online = () => {
      setConnected(true);
      if (missed) void queryClient.invalidateQueries();
      missed = false;
    };

    connection.on("changed", (message: LiveChanged) => {
      if (message.origin === clientId) return;
      const keys = new Set(message.topics.flatMap((topic) => topicQueries[topic] ?? []).map((key) => JSON.stringify(key)));
      keys.forEach((key) => void queryClient.invalidateQueries({ queryKey: JSON.parse(key) as string[] }));
      if (message.topics.includes("shares")) shareListeners.forEach((listener) => listener());
      if (message.topics.includes("filings")) filingListeners.forEach((listener) => listener());
    });
    // Signed out here, or from Active devices somewhere else: the next request would get a 401 anyway. When this app
    // signed out itself, it has already left the signed-in state and this does nothing.
    connection.on("signedOut", (message: LiveSignedOut) => {
      stopped = true;
      void connection.stop();
      void sessionEndedRef.current(message.reason === "revoked");
    });

    connection.onreconnecting(offline);
    connection.onreconnected(online);
    connection.onclose(() => {
      if (stopped) return;
      offline();
      retry = setTimeout(start, liveRetryDelay(failures++));
    });

    async function start() {
      try {
        await connection.start();
        failures = 0;
        if (!stopped) online();
      } catch {
        if (stopped) return;
        offline();
        retry = setTimeout(start, liveRetryDelay(failures++));
      }
    }

    void start();
    return () => {
      stopped = true;
      clearTimeout(retry);
      setConnected(false);
      void connection.stop();
    };
  }, [address, token, foreground, queryClient]);
}

function useForeground(): boolean {
  return useSyncExternalStore(
    (listener) => {
      const subscription = AppState.addEventListener("change", listener);
      return () => subscription.remove();
    },
    () => AppState.currentState === "active",
  );
}
