/** Where in a public link the visitor is: a folder inside it, and the file open in the viewer. */
export type LinkSearch = { folder?: string; file?: string };

export const validateLinkSearch = (search: Record<string, unknown>): LinkSearch => ({
  folder: typeof search.folder === "string" && search.folder ? search.folder : undefined,
  file: typeof search.file === "string" && search.file ? search.file : undefined,
});
