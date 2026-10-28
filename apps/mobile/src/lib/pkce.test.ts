import { createHash } from "node:crypto";
import { describe, expect, it } from "vitest";
import { base64Url, createPkce } from "./pkce";

describe("base64Url", () => {
  it.each([[0], [1], [2], [3], [31], [32], [33]])("matches Node for %d bytes", (length) => {
    const bytes = Uint8Array.from({ length }, (_, i) => (i * 37 + 11) & 255);
    expect(base64Url(bytes)).toBe(Buffer.from(bytes).toString("base64url"));
  });
});

describe("createPkce", () => {
  it("derives the S256 challenge from the verifier", () => {
    const random = Uint8Array.from({ length: 64 }, (_, i) => i);
    const pkce = createPkce(random);
    expect(pkce.verifier).toHaveLength(43);
    expect(pkce.state).toHaveLength(43);
    expect(pkce.challenge).toBe(createHash("sha256").update(pkce.verifier).digest("base64url"));
  });

  it("wants exactly 64 bytes", () => {
    expect(() => createPkce(new Uint8Array(32))).toThrow(RangeError);
  });
});
