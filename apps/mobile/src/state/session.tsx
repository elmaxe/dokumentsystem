import NetInfo from "@react-native-community/netinfo";
import { useQueryClient } from "@tanstack/react-query";
import * as Crypto from "expo-crypto";
import { Image } from "expo-image";
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { AppState } from "react-native";
import { probeLocalAddress, type ProbeResult } from "../lib/localAddress";
import { connect, disconnect, setUnauthorizedHandler } from "./api";
import { drive } from "./queries";
import { persister } from "./queryClient";
import {
  forgetServer,
  forgetSession,
  loadServer,
  loadSession,
  saveServer,
  saveSession,
  type SavedServer,
  type SavedSession,
} from "./storage";

/** Which address the app talks to now, and what the last check of the local one found (docs/design.md §15.3). */
export type Route = { using: "main" | "local"; local: ProbeResult | null; checkedAt: number | null };

export type SessionState =
  | { status: "loading" }
  | { status: "signedOut"; server: SavedServer | null; ended: boolean }
  | { status: "signedIn"; server: SavedServer; session: SavedSession; route: Route };

type SessionContext = {
  state: SessionState;
  /** Remembers the server before signing in, so the app comes back to it. */
  chooseServer: (mainUrl: string) => Promise<void>;
  /** Forgets the server, back to entering an address. */
  changeServer: () => Promise<void>;
  signedIn: (session: SavedSession) => Promise<void>;
  signOut: () => Promise<void>;
  setLocalUrl: (localUrl: string | null) => Promise<void>;
  /** Asks the local address to prove itself again now. */
  checkLocal: () => Promise<ProbeResult | null>;
};

const Context = createContext<SessionContext | null>(null);

const mainRoute: Route = { using: "main", local: null, checkedAt: null };

export function SessionProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [state, setState] = useState<SessionState>({ status: "loading" });
  // The latest state for callbacks that outlive a render: network listeners and the 401 handler.
  const latest = useRef(state);
  useEffect(() => {
    latest.current = state;
  }, [state]);

  const route = useCallback(async (server: SavedServer, session: SavedSession): Promise<Route> => {
    if (!server.localUrl) {
      connect(server.mainUrl, session.token);
      return mainRoute;
    }
    const local = await probeLocalAddress({
      localUrl: server.localUrl,
      sessionId: session.sessionId,
      token: session.token,
      random: Crypto.getRandomBytes(32),
      fetch,
    });
    const using = local === "verified" ? "local" : "main";
    connect(using === "local" ? server.localUrl : server.mainUrl, session.token);
    return { using, local, checkedAt: Date.now() };
  }, []);

  const clearData = useCallback(async () => {
    disconnect();
    queryClient.clear();
    await persister.removeClient();
    await Image.clearDiskCache();
  }, [queryClient]);

  const endSession = useCallback(
    async (ended: boolean) => {
      const current = latest.current;
      if (current.status !== "signedIn") return;
      await forgetSession();
      await clearData();
      setState({ status: "signedOut", server: current.server, ended });
    },
    [clearData],
  );

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      const [server, session] = await Promise.all([loadServer(), loadSession()]);
      if (cancelled) return;
      if (!server || !session) {
        setState({ status: "signedOut", server, ended: false });
        return;
      }
      // Start on the main address so cached screens show at once; the local one takes over once it has proved itself.
      connect(server.mainUrl, session.token);
      setState({ status: "signedIn", server, session, route: mainRoute });
      const checked = await route(server, session);
      if (!cancelled) setState({ status: "signedIn", server, session, route: checked });
    })();
    return () => {
      cancelled = true;
    };
  }, [route]);

  useEffect(() => {
    setUnauthorizedHandler(() => void endSession(true));
    return () => setUnauthorizedHandler(null);
  }, [endSession]);

  const checkLocal = useCallback(async () => {
    const current = latest.current;
    if (current.status !== "signedIn" || !current.server.localUrl) return null;
    const checked = await route(current.server, current.session);
    const now = latest.current;
    if (now.status === "signedIn" && now.session.sessionId === current.session.sessionId) {
      setState({ ...now, route: checked });
    }
    return checked.local;
  }, [route]);

  // Checks the local address again when the network changes and when the app comes back to the front.
  const hasLocal = state.status === "signedIn" && !!state.server.localUrl;
  useEffect(() => {
    if (!hasLocal) return;
    let lastNetwork: string | null = null;
    const network = NetInfo.addEventListener((info) => {
      const key = `${info.type}:${info.isConnected}`;
      if (lastNetwork !== null && key !== lastNetwork) void checkLocal();
      lastNetwork = key;
    });
    const app = AppState.addEventListener("change", (next) => {
      if (next === "active") void checkLocal();
    });
    return () => {
      network();
      app.remove();
    };
  }, [hasLocal, checkLocal]);

  const value = useMemo<SessionContext>(
    () => ({
      state,
      chooseServer: async (mainUrl) => {
        const server = { mainUrl, localUrl: null };
        await saveServer(server);
        setState({ status: "signedOut", server, ended: false });
      },
      changeServer: async () => {
        await forgetServer();
        setState({ status: "signedOut", server: null, ended: false });
      },
      signedIn: async (session) => {
        const current = latest.current;
        if (current.status !== "signedOut" || !current.server) return;
        const server = current.server;
        await saveSession(session);
        connect(server.mainUrl, session.token);
        setState({ status: "signedIn", server, session, route: mainRoute });
      },
      signOut: async () => {
        // Ends the session on the server too; if it's out of reach, the session stays listed under Active devices.
        await drive.signOut().catch(() => {});
        await endSession(false);
      },
      setLocalUrl: async (localUrl) => {
        const current = latest.current;
        if (current.status !== "signedIn") return;
        const server = { ...current.server, localUrl };
        await saveServer(server);
        const checked = await route(server, current.session);
        setState({ ...current, server, route: checked });
      },
      checkLocal,
    }),
    [state, endSession, route, checkLocal],
  );

  return <Context.Provider value={value}>{children}</Context.Provider>;
}

export function useSession(): SessionContext {
  const context = useContext(Context);
  if (!context) throw new Error("useSession needs a SessionProvider");
  return context;
}

/** The signed-in state, for screens that only show while signed in. */
export function useSignedIn() {
  const { state } = useSession();
  if (state.status !== "signedIn") throw new Error("Not signed in");
  return state;
}
