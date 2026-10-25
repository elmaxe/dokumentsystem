import { createLiveConnection, csrfHeader, liveRetryDelay, type LiveChanged, type LiveSignedOut, type LiveTopic } from "@dokument/api";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useEffect, useState, useSyncExternalStore } from "react";
import { clientId, sessionQuery } from "./api";

/** What each topic refetches. Only queries on screen fetch at once; the rest are marked stale for next time. */
const topicQueries: Record<LiveTopic, string[][]> = {
  files: [["folder"], ["node"], ["versions"], ["starred"], ["recent"]],
  recent: [["recent"]],
  trash: [["trash"]],
  storage: [["storage"]],
  account: [["auth", "session"], ["storage"], ["filing-rules"]],
  filings: [["filing-rules"]],
};

/** How long the server can be out of reach before the page says so. Short drops pass unnoticed. */
const offlineNotice = 30_000;

type LiveState = {
  connected: boolean;
  /** When the connection was lost, or first failed to open. */
  offlineSince: number | null;
  /** Counts changes to files made somewhere else: another tab, another device or the worker. */
  remoteFileChanges: number;
};

let state: LiveState = { connected: false, offlineSince: null, remoteFileChanges: 0 };
const listeners = new Set<() => void>();
const fileChangeListeners = new Set<() => void>();
const filingListeners = new Set<() => void>();

function update(next: Partial<LiveState>) {
  state = { ...state, ...next };
  listeners.forEach((listener) => listener());
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => void listeners.delete(listener);
}

/** True while changes arrive by themselves, so nothing needs polling. */
export const isLiveConnected = () => state.connected;

/** Goes up each time files change somewhere else, before the refetch that shows the change starts. */
export const useRemoteFileChanges = () => useSyncExternalStore(subscribe, () => state.remoteFileChanges);

/** Calls back when files change somewhere else, before the refetch that shows the change starts. */
export function onRemoteFileChange(listener: () => void) {
  fileChangeListeners.add(listener);
  return () => void fileChangeListeners.delete(listener);
}

/** Calls back when a filing rule moved some of the person's files (§13.5). */
export function onFilings(listener: () => void) {
  filingListeners.add(listener);
  return () => void filingListeners.delete(listener);
}

/** True once the server has been out of reach for a while (§15.4). */
export function useLiveOffline(): boolean {
  const since = useSyncExternalStore(subscribe, () => state.offlineSince);
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (since === null) return;
    const wait = since + offlineNotice - Date.now();
    if (wait <= 0) return;
    const timer = setTimeout(() => setNow(Date.now()), wait);
    return () => clearTimeout(timer);
  }, [since]);
  return since !== null && now - since >= offlineNotice;
}

/**
 * Keeps the signed-in app up to date with changes made elsewhere (docs/design.md §15.4): another tab, another device
 * or the worker. Notices only say what kind of data changed; the queries showing it fetch it again through the API.
 */
export function useLiveUpdates() {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const session = useQuery(sessionQuery);
  const sessionId = session.data?.sessionId;
  const csrfToken = session.data?.csrfToken;

  useEffect(() => {
    if (!sessionId) return;
    const connection = createLiveConnection({ headers: csrfToken ? { [csrfHeader]: csrfToken } : undefined });
    let stopped = false;
    // Things may have changed between the page's first fetches and the connection opening.
    let missed = true;
    let failures = 0;
    let retry: ReturnType<typeof setTimeout> | undefined;

    const offline = () => {
      missed = true;
      update({ connected: false, offlineSince: state.offlineSince ?? Date.now() });
    };
    const online = () => {
      update({ connected: true, offlineSince: null });
      // Notices sent while there was no connection are gone, so fetch everything on screen again.
      if (missed) void queryClient.invalidateQueries();
      missed = false;
    };

    connection.on("changed", (message: LiveChanged) => {
      if (message.origin === clientId) return;
      if (message.topics.includes("files")) {
        update({ remoteFileChanges: state.remoteFileChanges + 1 });
        fileChangeListeners.forEach((listener) => listener());
      }
      if (message.topics.includes("filings")) filingListeners.forEach((listener) => listener());
      const keys = new Set(message.topics.flatMap((topic) => topicQueries[topic] ?? []).map((key) => JSON.stringify(key)));
      keys.forEach((key) => void queryClient.invalidateQueries({ queryKey: JSON.parse(key) as string[] }));
    });

    connection.on("signedOut", (message: LiveSignedOut) => {
      stopped = true;
      void connection.stop();
      queryClient.setQueryData(sessionQuery.queryKey, null);
      queryClient.removeQueries({ predicate: (query) => query.queryKey[0] !== "auth" });
      void navigate({ to: "/signin", search: message.reason === "revoked" ? { reason: "ended" } : {} });
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
      update({ connected: false, offlineSince: null });
      void connection.stop();
    };
  }, [sessionId, csrfToken, queryClient, navigate]);
}
