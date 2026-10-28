import { createHash, createHmac } from "node:crypto";
import { describe, expect, it } from "vitest";
import { proofsMatch, sessionProof } from "./proof";

describe("sessionProof", () => {
  it("is an HMAC of the nonce keyed by the token's SHA-256", () => {
    const token = "q6Rk0n3o0Hc7wq1y2Nn9b0m0B0p3hN0u5tY0xQk1vXo";
    const nonce = "nonce-123";
    const key = createHash("sha256").update(token, "utf8").digest();
    expect(sessionProof(token, nonce)).toBe(createHmac("sha256", key).update(nonce, "utf8").digest("base64url"));
  });
});

describe("proofsMatch", () => {
  it("needs the same text", () => {
    expect(proofsMatch("abc", "abc")).toBe(true);
    expect(proofsMatch("abc", "abd")).toBe(false);
    expect(proofsMatch("abc", "abcd")).toBe(false);
  });
});
