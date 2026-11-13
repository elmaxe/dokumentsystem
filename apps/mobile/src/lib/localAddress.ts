import { base64Url } from "./pkce";
import { proofsMatch, sessionProof } from "./proof";

export type ProbeResult = "verified" | "unreachable" | "wrong-server";

/** The network the phone is on: whether it's Wi-Fi, and its name when the system tells the app. */
export type Network = { wifi: boolean; name: string | null };

/**
 * Whether to try the local address on this network (docs/design.md §15.3). With no home Wi-Fi names, everywhere;
 * otherwise only on those networks. When the name can't be read (no location permission), it's tried anyway, since
 * the session proof keeps a wrong server from getting the token.
 */
export function tryLocalOn(homeNetworks: string[], network: Network): boolean {
  if (homeNetworks.length === 0) return true;
  if (!network.wifi) return false;
  return network.name === null || homeNetworks.includes(network.name);
}

/**
 * Asks the local address to prove it's this server (docs/design.md §15.3), without sending the session token: the
 * server answers with an HMAC of a fresh nonce keyed by the session's stored hash, and the app checks it.
 */
export async function probeLocalAddress(options: {
  localUrl: string;
  sessionId: string;
  token: string;
  random: Uint8Array;
  fetch: typeof fetch;
  timeoutMs?: number;
}): Promise<ProbeResult> {
  const nonce = base64Url(options.random);
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), options.timeoutMs ?? 1_500);
  try {
    const response = await options.fetch(`${options.localUrl}/api/v1/auth/proof`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sessionId: options.sessionId, nonce }),
      signal: controller.signal,
    });
    if (!response.ok) return "wrong-server";
    const body = (await response.json()) as { proof?: unknown };
    return typeof body.proof === "string" && proofsMatch(body.proof, sessionProof(options.token, nonce))
      ? "verified"
      : "wrong-server";
  } catch {
    return "unreachable";
  } finally {
    clearTimeout(timer);
  }
}
