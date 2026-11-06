/**
 * A small tus 1.0 client (core and creation, docs/design.md §8.1), enough for the app's uploads. It reads the file a
 * chunk at a time from the phone's storage, which tus-js-client can't do in React Native without loading it whole.
 */

export type TusSource = {
  size: number;
  read: (offset: number, length: number) => Promise<Uint8Array<ArrayBuffer>>;
};

export type TusOptions = {
  /** The uploads endpoint, absolute. */
  endpoint: string;
  /** Sent with every request: the session, and the live client id. Asked for each time, as the address can change. */
  headers: () => Record<string, string>;
  metadata: Record<string, string>;
  /** Where an earlier try of the same upload left its bytes; the upload carries on from the server's offset. */
  location?: string;
  onLocation?: (location: string) => void;
  onProgress?: (sent: number, total: number) => void;
  chunkSize?: number;
  retryDelays?: number[];
  signal?: AbortSignal;
  fetch?: typeof fetch;
  sleep?: (ms: number) => Promise<void>;
};

/** The server refused the upload; `body` is its problem details, when it sent some. */
export class TusError extends Error {
  constructor(
    readonly status: number,
    readonly body: { code?: string; detail?: string; title?: string } = {},
  ) {
    super(body.detail ?? body.title ?? `Upload failed with ${status}`);
  }
}

const version = "1.0.0";

/** tus metadata: each value base64 of its UTF-8, so names like "Kvitto från Åhléns" travel intact. */
export function encodeMetadata(metadata: Record<string, string>): string {
  return Object.entries(metadata)
    .map(([key, value]) => {
      const bytes = new TextEncoder().encode(value);
      let binary = "";
      for (const byte of bytes) binary += String.fromCharCode(byte);
      return `${key} ${btoa(binary)}`;
    })
    .join(",");
}

/** Network failures, the server being busy or restarting, and a lost offset are tried again; refusals are not. */
const retryable = (error: unknown) =>
  !(error instanceof TusError) || error.status >= 500 || error.status === 423 || error.status === 409;

/** Sends the file and answers with the response that completed it, whose headers name the file it made. */
export async function tusUpload(source: TusSource, options: TusOptions): Promise<Headers> {
  const http = options.fetch ?? fetch;
  const sleep = options.sleep ?? ((ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms)));
  const chunkSize = options.chunkSize ?? 8 * 1024 * 1024;
  const delays = options.retryDelays ?? [1_000, 3_000, 10_000];
  let location = options.location;
  let offset: number | null = location ? null : 0;
  let failures = 0;

  const request = async (url: string, init: RequestInit & { headers: Record<string, string> }) => {
    options.signal?.throwIfAborted();
    const response = await http(url, {
      ...init,
      signal: options.signal,
      headers: { "Tus-Resumable": version, ...options.headers(), ...init.headers },
    });
    if (!response.ok) {
      const body = await response.json().catch(() => ({}));
      throw new TusError(response.status, body as TusError["body"]);
    }
    return response;
  };

  for (;;) {
    try {
      if (!location) {
        const created = await request(options.endpoint, {
          method: "POST",
          headers: { "Upload-Length": String(source.size), "Upload-Metadata": encodeMetadata(options.metadata) },
        });
        const header = created.headers.get("Location");
        if (!header) throw new TusError(created.status, { detail: "The server gave no upload address." });
        location = new URL(header, options.endpoint).toString();
        options.onLocation?.(location);
        offset = 0;
        // An empty file is complete as soon as it's made.
        if (source.size === 0) return created.headers;
      }
      if (offset === null) {
        const head = await request(location, { method: "HEAD", headers: {} });
        offset = Number(head.headers.get("Upload-Offset") ?? 0);
      }
      for (;;) {
        const length = Math.min(chunkSize, source.size - offset);
        const chunk = await source.read(offset, length);
        const patched = await request(location, {
          method: "PATCH",
          headers: { "Upload-Offset": String(offset), "Content-Type": "application/offset+octet-stream" },
          body: chunk,
        });
        offset = Number(patched.headers.get("Upload-Offset") ?? offset + length);
        failures = 0;
        options.onProgress?.(offset, source.size);
        if (offset >= source.size) return patched.headers;
      }
    } catch (error) {
      if (options.signal?.aborted || !retryable(error) || failures >= delays.length) throw error;
      await sleep(delays[failures++]!);
      // Ask the server how far it got before sending more.
      if (location) offset = null;
    }
  }
}
