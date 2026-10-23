type Reply = { id: number; hash?: string; error?: string };

let worker: Worker | undefined;
let nextId = 0;
const pending = new Map<number, { resolve: (hash: string) => void; reject: (error: Error) => void }>();
let queue: Promise<unknown> = Promise.resolve();

function getWorker(): Worker {
  if (!worker) {
    worker = new Worker(new URL("./hash.worker.ts", import.meta.url), { type: "module" });
    worker.onmessage = ({ data }: MessageEvent<Reply>) => {
      const waiting = pending.get(data.id);
      pending.delete(data.id);
      if (data.hash) waiting?.resolve(data.hash);
      else waiting?.reject(new Error(data.error ?? "Hashing failed"));
    };
  }
  return worker;
}

/** The file's SHA-256 in lowercase hex. Files are hashed one at a time, in a Web Worker. */
export function sha256(file: File): Promise<string> {
  const run = () =>
    new Promise<string>((resolve, reject) => {
      const id = nextId++;
      pending.set(id, { resolve, reject });
      getWorker().postMessage({ id, file });
    });
  const result = queue.then(run, run);
  queue = result.catch(() => undefined);
  return result;
}
