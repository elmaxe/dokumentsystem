import NetInfo from "@react-native-community/netinfo";
import { useQueryClient } from "@tanstack/react-query";
import * as Crypto from "expo-crypto";
import { Image } from "expo-image";
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import * as WebBrowser from "expo-web-browser";
import { AppState } from "react-native";
import { probeLocalAddress, tryLocalOn, type ProbeResult } from "../lib/localAddress";
import { currentNetwork, networkOf } from "../lib/wifi";
import { connect, disconnect, setUnauthorizedHandler } from "./api";
import { drive } from "./queries";
import { persister } from "./queryClient";
import { forgetUploads } from "./uploads";
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

/**
 * Which address the app talks to now, and what the last check of the local one found (docs/design.md §15.3):
 * "otherNetwork" when the phone isn't on one of the home Wi-Fi networks, so it wasn't tried.
 */
export type LocalCheck = ProbeResult | "otherNetwork";
export type Route = { using: "main" | "local"; local: LocalCheck | null; checkedAt: number | null };

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
  /** The server ended this session (signed out or ended under Active devices elsewhere): back to signing in. */
  sessionEnded: (revoked: boolean) => Promise<void>;
  setLocalUrl: (localUrl: string | null) => Promise<void>;
  /** The Wi-Fi networks the local address is tried on; empty tries it everywhere. */
  setHomeNetworks: (names: string[]) => Promise<void>;
  /** Asks the local address to prove itself again now. */
  checkLocal: () => Promise<LocalCheck | null>;
};

const Context = createContext<SessionContext | null>(null);

/** Where the server's sign-out page sends the browser back to the app. */
const appSignedOutUrl = "dokumentsystem://signed-out";

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
    if (!tryLocalOn(server.homeNetworks ?? [], await currentNetwork())) {
      connect(server.mainUrl, session.token);
      return { using: "main", local: "otherNetwork", checkedAt: Date.now() };
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
    // Uploads belong to the account: they stop, and their copies on the phone go.
    await forgetUploads();
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
      // A different Wi-Fi network counts as a change, for home networks.
      const key = `${info.type}:${info.isConnected}:${networkOf(info).name ?? ""}`;
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
        const endSessionUrl = await drive.signOut().catch(() => null);
        await endSession(false);
        // Then signs out at the IdP in the same browser the app signs in with, so the next sign-in asks for the
        // password again (§5). Closing that browser early leaves the IdP session, but the app is signed out anyway.
        if (endSessionUrl) await WebBrowser.openAuthSessionAsync(endSessionUrl, appSignedOutUrl).catch(() => {});
      },
      sessionEnded: endSession,
      setLocalUrl: async (localUrl) => {
        const current = latest.current;
        if (current.status !== "signedIn") return;
        const server = { ...current.server, localUrl };
        await saveServer(server);
        const checked = await route(server, current.session);
        setState({ ...current, server, route: checked });
      },
      setHomeNetworks: async (homeNetworks) => {
        const current = latest.current;
        if (current.status !== "signedIn") return;
        const server = { ...current.server, homeNetworks };
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
