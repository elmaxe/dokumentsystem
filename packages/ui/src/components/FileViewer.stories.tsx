import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { FileItem } from "../lib/files";
import { thumbnails } from "../mockup/thumbnails";
import { FileViewer, type PreviewSource } from "./FileViewer";

/** A real, multi-page PDF built on the fly, so the story runs pdf.js exactly as the app does. */
function samplePdf(): string {
  const pages = [
    ["Hyresavtal", "Lägenhet 1203, Storgatan 12", "Hyra: 9 450 kr per månad", "Uppsägningstid: tre månader"],
    ["Bilaga 1: Inventarier", "Diskmaskin, Bosch", "Kyl och frys, Electrolux", "Tvättmaskin i källaren"],
    ["Underskrifter", "Hyresvärd: Bostads AB", "Hyresgäst: Alice Andersson", "Stockholm, 1 oktober 2026"],
  ];
  const objects = [
    "<< /Type /Catalog /Pages 2 0 R >>",
    `<< /Type /Pages /Kids [${pages.map((_, i) => `${4 + i * 2} 0 R`).join(" ")}] /Count ${pages.length} >>`,
    "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
  ];
  for (const lines of pages) {
    const ops = lines.map((line, i) => `BT /F1 ${i === 0 ? 24 : 13} Tf 72 ${760 - i * 40} Td (${line}) Tj ET`).join("\n");
    const stream = `0.39 0.82 0.97 rg 72 780 120 6 re f\n0 0 0 rg\n${ops}`;
    objects.push(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents ${objects.length + 2} 0 R >>`);
    objects.push(`<< /Length ${stream.length} >>\nstream\n${stream}\nendstream`);
  }
  let body = "%PDF-1.4\n";
  const offsets: number[] = [];
  objects.forEach((object, i) => {
    offsets.push(body.length);
    body += `${i + 1} 0 obj\n${object}\nendobj\n`;
  });
  const xref = body.length;
  body += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.map((o) => `${String(o).padStart(10, "0")} 00000 n \n`).join("")}`;
  body += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  // The PDF is Latin-1 (WinAnsi), so each character is one byte.
  const bytes = Uint8Array.from(body, (c) => c.charCodeAt(0));
  return URL.createObjectURL(new Blob([bytes], { type: "application/pdf" }));
}

const text = `# Inköpslista\n\n- Mjölk\n- Kaffe (mörkrost)\n- Knäckebröd\n\n${Array.from({ length: 40 }, (_, i) => `Rad ${i + 1}: ${"lorem ipsum ".repeat(6)}`).join("\n")}`;
const textUrl = () => URL.createObjectURL(new Blob([text], { type: "text/plain" }));

const files: Record<string, { item: FileItem; preview: () => PreviewSource }> = {
  image: {
    item: { id: "1", name: "Abisko-2025.jpg", kind: "image", modified: "2026-10-20T10:00:00Z", owner: "Alice" },
    preview: () => ({ type: "image", src: thumbnails.mountains }),
  },
  pdf: {
    item: { id: "2", name: "Hyresavtal-2026.pdf", kind: "pdf", modified: "2026-10-01T10:00:00Z", owner: "Alice" },
    preview: () => ({ type: "pdf", src: samplePdf() }),
  },
  text: {
    item: { id: "3", name: "inköpslista.md", kind: "text", modified: "2026-10-22T10:00:00Z", owner: "Alice" },
    preview: () => ({ type: "text", src: textUrl() }),
  },
  preparing: {
    item: { id: "4", name: "IMG_4021.HEIC", kind: "image", modified: "2026-10-23T10:00:00Z", owner: "Alice" },
    preview: () => ({ type: "preparing" }),
  },
  none: {
    item: { id: "5", name: "backup-2026.zip", kind: "archive", modified: "2026-09-01T10:00:00Z", owner: "Alice" },
    preview: () => ({ type: "none" }),
  },
  failed: {
    item: { id: "6", name: "skannad.pdf", kind: "pdf", modified: "2026-09-01T10:00:00Z", owner: "Alice" },
    preview: () => ({ type: "pdf", src: URL.createObjectURL(new Blob(["%PDF-1.4 truncated"], { type: "application/pdf" })) }),
  },
};

function Viewer({ file }: { file: keyof typeof files }) {
  const { t } = useTranslation();
  const order = Object.keys(files) as (keyof typeof files)[];
  const [current, setCurrent] = useState(file);
  const [preview] = useState(() => new Map(order.map((key) => [key, files[key]!.preview()])));
  const index = order.indexOf(current);
  const { item } = files[current]!;
  return (
    <FileViewer
      item={item}
      preview={preview.get(current)!}
      onClose={() => {}}
      onDownload={() => {}}
      onShare={() => {}}
      menuActions={[
        { id: "rename", icon: "edit", label: t("actions.rename") },
        { id: "move", icon: "drive_file_move", label: t("actions.move") },
      ]}
      onAction={() => {}}
      onPrevious={index > 0 ? () => setCurrent(order[index - 1]!) : undefined}
      onNext={index < order.length - 1 ? () => setCurrent(order[index + 1]!) : undefined}
    />
  );
}

const meta = {
  title: "Components/FileViewer",
  component: Viewer,
  parameters: {
    layout: "fullscreen",
    docs: {
      description: {
        component:
          "The viewer (§14). Images zoom with the toolbar or a double-click and pan by dragging; PDFs render with pdf.js one page at a time (Page Up/Down); text shows the first 1 MiB. Arrow keys or a swipe move to the next file, Esc closes. The arrows here step through every state.",
      },
    },
  },
} satisfies Meta<typeof Viewer>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Image: Story = { args: { file: "image" } };
export const Pdf: Story = { args: { file: "pdf" } };
export const Text: Story = { args: { file: "text" } };
export const Preparing: Story = { args: { file: "preparing" } };
export const NoPreview: Story = { args: { file: "none" } };
export const Failed: Story = { args: { file: "failed" } };
