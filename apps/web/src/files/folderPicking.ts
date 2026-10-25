/** A folder someone picked or dropped (docs/design.md §8.1): every file in it, with the folder each one is in. */
export type PickedFolder = {
  name: string;
  /** `dir` is the folder's path from the top, names separated by "/": "Kvitton/2025". */
  files: { file: File; dir: string }[];
  /** Every folder in it, the top one and empty ones included. */
  folders: string[];
};

/** Files operating systems leave behind that nobody means to upload. Skipped without asking. */
const junk = new Set([".ds_store", "thumbs.db", "desktop.ini"]);
export const isJunk = (name: string) => junk.has(name.toLowerCase()) || name.startsWith("._");

/** Whether this browser lets people pick a whole folder. Phones mostly can't, so the menu leaves the item out. */
export const canPickFolders = () =>
  typeof document !== "undefined" && "webkitdirectory" in document.createElement("input");

/** Names the server accepts (NodeName, §6): not blank, "." or "..", no slashes or control characters, 255 bytes. */
export function validName(name: string): boolean {
  const normalized = name.trim().normalize("NFC");
  return (
    normalized !== "" &&
    normalized !== "." &&
    normalized !== ".." &&
    ![...normalized].some((c) => c === "/" || c === "\\" || isControl(c.codePointAt(0)!)) &&
    new TextEncoder().encode(normalized).length <= 255
  );
}

/** Like .NET's char.IsControl, which the server uses: C0 controls, DEL and the C1 controls. */
const isControl = (code: number) => code <= 0x1f || (code >= 0x7f && code <= 0x9f);

/** From `<input webkitdirectory>`: one folder. Browsers don't list empty folders this way. */
export function fromFolderInput(files: FileList | File[]): PickedFolder[] {
  const byTop = new Map<string, PickedFolder>();
  for (const file of Array.from(files)) {
    if (isJunk(file.name)) continue;
    const parts = file.webkitRelativePath.split("/");
    const top = parts[0] ?? file.name;
    const folder = byTop.get(top) ?? { name: top, files: [], folders: [top] };
    byTop.set(top, folder);
    const dir = parts.length > 1 ? parts.slice(0, -1).join("/") : top;
    folder.files.push({ file, dir });
    if (!folder.folders.includes(dir)) folder.folders.push(dir);
  }
  return [...byTop.values()];
}

/**
 * From a drop: folders are read through the entries API, which also finds empty folders; files dropped beside them
 * upload as usual. The entries have to be taken before the drop event ends, so call this from the handler itself.
 */
export async function fromDrop(data: DataTransfer): Promise<{ folders: PickedFolder[]; files: File[] }> {
  const entries = Array.from(data.items)
    .filter((item) => item.kind === "file")
    .map((item) => item.webkitGetAsEntry?.() ?? null);
  if (entries.length === 0 || entries.some((entry) => entry === null)) {
    return { folders: [], files: Array.from(data.files).filter((file) => !isJunk(file.name)) };
  }

  const folders: PickedFolder[] = [];
  const files: File[] = [];
  for (const entry of entries) {
    if (entry?.isDirectory) {
      const folder: PickedFolder = { name: entry.name, files: [], folders: [] };
      await walk(entry as FileSystemDirectoryEntry, entry.name, folder);
      folders.push(folder);
    } else if (entry?.isFile && !isJunk(entry.name)) {
      files.push(await fileOf(entry as FileSystemFileEntry));
    }
  }
  return { folders, files };
}

async function walk(directory: FileSystemDirectoryEntry, path: string, into: PickedFolder) {
  into.folders.push(path);
  const reader = directory.createReader();
  // readEntries hands out a folder's entries in batches (100 in Chrome) until it returns none.
  for (;;) {
    const batch = await new Promise<FileSystemEntry[]>((resolve, reject) => reader.readEntries(resolve, reject));
    if (batch.length === 0) return;
    for (const entry of batch) {
      if (entry.isDirectory) await walk(entry as FileSystemDirectoryEntry, `${path}/${entry.name}`, into);
      else if (!isJunk(entry.name)) into.files.push({ file: await fileOf(entry as FileSystemFileEntry), dir: path });
    }
  }
}

const fileOf = (entry: FileSystemFileEntry) =>
  new Promise<File>((resolve, reject) => entry.file(resolve, reject));
