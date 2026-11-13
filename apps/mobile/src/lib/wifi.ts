import NetInfo, { type NetInfoState } from "@react-native-community/netinfo";
import * as Location from "expo-location";
import { Platform } from "react-native";
import type { Network } from "./localAddress";

// iOS only tells the name with the Wi-Fi information entitlement and location permission.
NetInfo.configure({ shouldFetchWiFiSSID: true });

/** The network in NetInfo's terms, as the local address check needs it (docs/design.md §15.3). */
export function networkOf(info: NetInfoState): Network {
  if (info.type !== "wifi") return { wifi: false, name: null };
  const ssid = info.details?.ssid;
  // Android hides the name as "<unknown ssid>" without location permission.
  return { wifi: true, name: ssid && ssid !== "<unknown ssid>" ? ssid : null };
}

export const currentNetwork = async () => networkOf(await NetInfo.fetch());

/**
 * The Wi-Fi network's name for adding it as a home network. Android only tells it to apps with precise location
 * while location is on, so this asks for that first. NetInfo keeps the state from before the permission, so it's
 * read again rather than taken from the cache.
 */
export async function readWifiName(): Promise<{ name: string } | { error: WifiProblem }> {
  if (Platform.OS !== "web") {
    const permission = await Location.requestForegroundPermissionsAsync();
    if (!permission.granted) return { error: "denied" };
    if (Platform.OS === "android" && permission.android?.accuracy !== "fine") return { error: "approximate" };
    if (!(await Location.hasServicesEnabledAsync())) return { error: "locationOff" };
  }
  const network = networkOf(await NetInfo.refresh());
  if (!network.wifi) return { error: "notWifi" };
  return network.name ? { name: network.name } : { error: "unknown" };
}

export type WifiProblem = "denied" | "approximate" | "locationOff" | "notWifi" | "unknown";
