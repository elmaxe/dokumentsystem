/**
 * Whether this build of the web app includes Scriptor, the Word viewer's engine (docs/design.md §14). Without Rust and
 * wasm-pack, packages/scriptor builds a stub instead. Imports only the view's JavaScript, not the 16 MB engine.
 */
export async function wordViewerBuilt(): Promise<boolean> {
  return (await import("@dokument/scriptor")).available;
}
