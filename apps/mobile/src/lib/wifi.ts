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
 * The Wi-Fi network's name for adding it as a home network. The system only tells it to apps that may see the
 * location, so this asks for that first. "denied" when the person said no, "notWifi" off Wi-Fi.
 */
export async function readWifiName(): Promise<
  { name: string } | { error: "denied" | "notWifi" | "unknown" }
> {
  if (Platform.OS !== "web") {
    const permission = await Location.requestForegroundPermissionsAsync();
    if (!permission.granted) return { error: "denied" };
  }
  const network = await currentNetwork();
  if (!network.wifi) return { error: "notWifi" };
  return network.name ? { name: network.name } : { error: "unknown" };
}
