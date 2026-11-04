import { sha256 } from "@noble/hashes/sha2.js";

/** RFC 4648 base64url without padding, as PKCE and our tokens use it. */
export function base64Url(bytes: Uint8Array): string {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
  let out = "";
  let i = 0;
  for (; i + 2 < bytes.length; i += 3) {
    const n = (bytes[i]! << 16) | (bytes[i + 1]! << 8) | bytes[i + 2]!;
    out += alphabet[n >> 18]! + alphabet[(n >> 12) & 63]! + alphabet[(n >> 6) & 63]! + alphabet[n & 63]!;
  }
  if (i < bytes.length) {
    const n = (bytes[i]! << 16) | ((bytes[i + 1] ?? 0) << 8);
    out += alphabet[n >> 18]! + alphabet[(n >> 12) & 63]!;
    if (i + 1 < bytes.length) out += alphabet[(n >> 6) & 63]!;
  }
  return out;
}

export type Pkce = { verifier: string; challenge: string; state: string };

/** A PKCE pair (S256) and a `state`, from 64 random bytes: 32 for the verifier, 32 for the state. */
export function createPkce(random: Uint8Array): Pkce {
  if (random.length !== 64) throw new RangeError("createPkce needs 64 random bytes");
  const verifier = base64Url(random.subarray(0, 32));
  const challenge = base64Url(sha256(new TextEncoder().encode(verifier)));
  return { verifier, challenge, state: base64Url(random.subarray(32)) };
}
