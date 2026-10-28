import { hmac } from "@noble/hashes/hmac.js";
import { sha256 } from "@noble/hashes/sha2.js";
import { base64Url } from "./pkce";

const utf8 = (text: string) => new TextEncoder().encode(text);

/**
 * What a server must answer to prove it's the one that holds this session (docs/design.md §15.3): an HMAC-SHA256 of
 * the nonce, keyed by the SHA-256 of the session token, which is exactly what the server stores. Another device at
 * the same local address doesn't have it, so the app never sends its token there.
 */
export const sessionProof = (token: string, nonce: string) => base64Url(hmac(sha256, sha256(utf8(token)), utf8(nonce)));

/** Compares two proofs without stopping at the first difference. */
export function proofsMatch(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}
