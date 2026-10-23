import { createSHA256 } from "hash-wasm";

// Hashes files off the main thread, a slice at a time so large files never sit in memory (docs/design.md §8.3).
const sliceSize = 8 * 1024 * 1024;

onmessage = async (event: MessageEvent<{ id: number; file: File }>) => {
  const { id, file } = event.data;
  try {
    const hasher = await createSHA256();
    for (let offset = 0; offset < file.size; offset += sliceSize) {
      hasher.update(new Uint8Array(await file.slice(offset, offset + sliceSize).arrayBuffer()));
    }
    postMessage({ id, hash: hasher.digest("hex") });
  } catch (error) {
    postMessage({ id, error: String(error) });
  }
};
