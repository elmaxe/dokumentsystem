import { describe, expect, it, vi } from "vitest";
import { probeLocalAddress, tryLocalOn } from "./localAddress";
import { sessionProof } from "./proof";

const token = "q6Rk0n3o0Hc7wq1y2Nn9b0m0B0p3hN0u5tY0xQk1vXo";
const base = { localUrl: "http://192.168.1.20:8080", sessionId: "s-1", token, random: new Uint8Array(32).fill(7) };

describe("probeLocalAddress", () => {
  it("trusts a server that proves it holds the session, and never sends the token", async () => {
    const fetch = vi.fn(async (_url: string, init: RequestInit) => {
      const { nonce } = JSON.parse(init.body as string) as { nonce: string };
      return Response.json({ proof: sessionProof(token, nonce) });
    });
    await expect(probeLocalAddress({ ...base, fetch: fetch as unknown as typeof globalThis.fetch })).resolves.toBe("verified");
    const [url, init] = fetch.mock.calls[0]!;
    expect(url).toBe("http://192.168.1.20:8080/api/v1/auth/proof");
    expect(JSON.stringify(init)).not.toContain(token);
  });

  it("refuses a server that can't prove it", async () => {
    const fetch = async () => Response.json({ proof: "made-up" });
    await expect(probeLocalAddress({ ...base, fetch: fetch as typeof globalThis.fetch })).resolves.toBe("wrong-server");
  });

  it("refuses a server that doesn't know the session", async () => {
    const fetch = async () => new Response(null, { status: 404 });
    await expect(probeLocalAddress({ ...base, fetch: fetch as typeof globalThis.fetch })).resolves.toBe("wrong-server");
  });

  it("gives up when nothing answers", async () => {
    const fetch = (_url: string, init: RequestInit) =>
      new Promise<Response>((_, reject) => init.signal!.addEventListener("abort", () => reject(new Error("aborted"))));
    await expect(
      probeLocalAddress({ ...base, fetch: fetch as typeof globalThis.fetch, timeoutMs: 10 }),
    ).resolves.toBe("unreachable");
  });
});

describe("tryLocalOn", () => {
  it("tries everywhere without home Wi-Fi names", () => {
    expect(tryLocalOn([], { wifi: false, name: null })).toBe(true);
  });

  it("tries only on a home Wi-Fi network once there are names", () => {
    expect(tryLocalOn(["Home"], { wifi: true, name: "Home" })).toBe(true);
    expect(tryLocalOn(["Home"], { wifi: true, name: "Café" })).toBe(false);
    expect(tryLocalOn(["Home"], { wifi: false, name: null })).toBe(false);
  });

  it("tries when the name can't be read", () => {
    expect(tryLocalOn(["Home"], { wifi: true, name: null })).toBe(true);
  });
});
