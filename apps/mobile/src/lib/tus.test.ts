import { describe, expect, it } from "vitest";
import { encodeMetadata, TusError, tusUpload, type TusSource } from "./tus";

const bytes = Uint8Array.from({ length: 25 }, (_, i) => i);
const source: TusSource = { size: bytes.length, read: async (offset, length) => bytes.slice(offset, offset + length) };

/** A tus server in memory, which can drop the next PATCH after storing part of it. */
function fakeServer() {
  const stored: number[] = [];
  const calls: string[] = [];
  let dropNext = false;
  const server = {
    stored,
    calls,
    dropNextPatch: () => (dropNext = true),
    fetch: (async (url: string | URL, init?: RequestInit) => {
      const headers = new Headers(init?.headers);
      calls.push(`${init?.method} ${String(url)}`);
      expect(headers.get("Tus-Resumable")).toBe("1.0.0");
      expect(headers.get("Authorization")).toBe("Bearer token");
      switch (init?.method) {
        case "POST":
          return new Response(null, { status: 201, headers: { Location: "/api/v1/uploads/abc" } });
        case "HEAD":
          return new Response(null, { status: 200, headers: { "Upload-Offset": String(stored.length) } });
        case "PATCH": {
          expect(Number(headers.get("Upload-Offset"))).toBe(stored.length);
          const chunk = [...(init.body as Uint8Array)];
          if (dropNext) {
            dropNext = false;
            stored.push(...chunk.slice(0, 3));
            throw new TypeError("Network request failed");
          }
          stored.push(...chunk);
          const done = stored.length === bytes.length;
          return new Response(null, {
            status: 204,
            headers: { "Upload-Offset": String(stored.length), ...(done ? { "Dokument-Node-Id": "node-1" } : {}) },
          });
        }
      }
      throw new Error(`Unexpected ${init?.method}`);
    }) as typeof fetch,
  };
  return server;
}

const options = (server: ReturnType<typeof fakeServer>) => ({
  endpoint: "https://drive.example/api/v1/uploads",
  headers: () => ({ Authorization: "Bearer token" }),
  metadata: { filename: "Kvitto från Åhléns.pdf" },
  chunkSize: 10,
  fetch: server.fetch,
  sleep: async () => {},
});

describe("tusUpload", () => {
  it("creates the upload and sends it in chunks", async () => {
    const server = fakeServer();
    const progress: number[] = [];
    const headers = await tusUpload(source, { ...options(server), onProgress: (sent) => progress.push(sent) });
    expect(server.stored).toEqual([...bytes]);
    expect(progress).toEqual([10, 20, 25]);
    expect(headers.get("Dokument-Node-Id")).toBe("node-1");
    expect(server.calls[1]).toBe("PATCH https://drive.example/api/v1/uploads/abc");
  });

  it("asks the server for its offset after a dropped connection and carries on from there", async () => {
    const server = fakeServer();
    server.dropNextPatch();
    await tusUpload(source, options(server));
    expect(server.stored).toEqual([...bytes]);
    expect(server.calls).toContain("HEAD https://drive.example/api/v1/uploads/abc");
  });

  it("resumes an upload from an earlier try", async () => {
    const server = fakeServer();
    server.stored.push(...bytes.slice(0, 12));
    await tusUpload(source, { ...options(server), location: "https://drive.example/api/v1/uploads/abc" });
    expect(server.calls[0]).toBe("HEAD https://drive.example/api/v1/uploads/abc");
    expect(server.stored).toEqual([...bytes]);
  });

  it("doesn't try a refusal again", async () => {
    let posts = 0;
    const refuse = (async () => {
      posts++;
      return new Response(JSON.stringify({ code: "quotaExceeded" }), { status: 413 });
    }) as unknown as typeof fetch;
    const error = await tusUpload(source, { ...options(fakeServer()), fetch: refuse }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(TusError);
    expect((error as TusError).body.code).toBe("quotaExceeded");
    expect(posts).toBe(1);
  });
});

describe("encodeMetadata", () => {
  it("encodes values as base64 of their UTF-8", () => {
    expect(encodeMetadata({ filename: "Åhléns.pdf", onConflict: "keepBoth" })).toBe(
      `filename ${Buffer.from("Åhléns.pdf").toString("base64")},onConflict ${Buffer.from("keepBoth").toString("base64")}`,
    );
  });
});
