import AsyncStorage from "@react-native-async-storage/async-storage";
import * as SecureStore from "expo-secure-store";
import { Platform } from "react-native";

/** The server this app talks to (docs/design.md §15.3): one at a time. */
export type SavedServer = {
  /** The main address, https outside development. */
  mainUrl: string;
  /** An optional local address, used whenever the server proves itself there. */
  localUrl: string | null;
};

/** The bearer session, kept in the Keychain or Android Keystore (§5). */
export type SavedSession = { token: string; sessionId: string };

const serverKey = "dokument.server";
const sessionKey = "dokument.session";

// The web build is only for development and screenshots; it has no secure store.
const secure = {
  get: (key: string) => (Platform.OS === "web" ? AsyncStorage.getItem(key) : SecureStore.getItemAsync(key)),
  set: (key: string, value: string) =>
    Platform.OS === "web" ? AsyncStorage.setItem(key, value) : SecureStore.setItemAsync(key, value),
  remove: (key: string) => (Platform.OS === "web" ? AsyncStorage.removeItem(key) : SecureStore.deleteItemAsync(key)),
};

async function readJson<T>(read: Promise<string | null>): Promise<T | null> {
  try {
    const text = await read;
    return text ? (JSON.parse(text) as T) : null;
  } catch {
    return null;
  }
}

export const loadServer = () => readJson<SavedServer>(AsyncStorage.getItem(serverKey));
export const saveServer = (server: SavedServer) => AsyncStorage.setItem(serverKey, JSON.stringify(server));
export const forgetServer = () => AsyncStorage.removeItem(serverKey);

export const loadSession = () => readJson<SavedSession>(secure.get(sessionKey));
export const saveSession = (session: SavedSession) => secure.set(sessionKey, JSON.stringify(session));
export const forgetSession = () => secure.remove(sessionKey);
