// The part of Scriptor's headless view (@truespar/scriptor-core) that the viewer uses. Scriptor's own TypeScript is
// compiled by Vite but not type-checked here, so these declarations are the contract; check them when the pinned
// commit moves.

export interface ScriptorViewOptions {
  mode?: "read" | "edit";
  /** Whether text can be selected and copied in read mode. */
  selectable?: boolean;
  /**
   * Ctrl+S, in read mode too. Without it Scriptor downloads its own re-serialization of the document, so the viewer
   * always passes one.
   */
  onSave?: (bytes: Uint8Array) => void;
}

export declare class ScriptorView {
  static create(container: HTMLElement, options?: ScriptorViewOptions): Promise<ScriptorView>;
  /** Parses and lays out a .docx. Throws when the file can't be read. */
  loadDocx(bytes: Uint8Array): void;
  /** Clamped to 25 %–400 %. */
  setZoom(factor: number): void;
  readonly zoomLevel: number;
  pageCount(): number;
  /** Page size in twips (1/1440 inch). */
  pageGeometry(): { width: number; height: number };
  destroy(): void;
}

/** False when the engine wasn't built (no Rust toolchain); the viewer then shows the PDF instead. */
export declare const available: boolean;
