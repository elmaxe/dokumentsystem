import { base64Url } from "./pkce";
import { proofsMatch, sessionProof } from "./proof";

export type ProbeResult = "verified" | "unreachable" | "wrong-server";

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
