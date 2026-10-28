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

const markdown = `# Stugan på Utö

Allt man behöver veta inför en helg i stugan. Se [färjetiderna](https://example.com/farjor).

![Utsikten från bryggan](https://example.com/brygga.jpg)

## Att göra när vi kommer

- [x] Slå på vattnet (kranen under diskbänken)
- [ ] Tänd i kaminen

## Wifi

| Nät | Lösenord |
| --- | --- |
| Stugan | \`[lösenordet]\` |

\`\`\`yaml
# Färjan går från Årsta brygga
avgång: "17:45"
retur: 16
\`\`\`

> Glöm inte att stänga av vattnet när vi åker. <b>Raw HTML</b> stays text.
`;

const code = `# Copies new photos from the camera card into the drive's import folder.
import json, pathlib, subprocess


def read_exif(path):
    out = subprocess.run(["exiftool", "-json", str(path)], capture_output=True, check=True)
    return json.loads(out.stdout)[0]


def destination(meta, root):
    taken = meta.get("DateTimeOriginal", "1970:01:01 00:00:00")
    year, month = taken[:4], taken[5:7]
    return pathlib.Path(root) / year / month / meta["FileName"]


def main(card, root):
    for photo in pathlib.Path(card).rglob("*.JPG"):
        meta = read_exif(photo)
        target = destination(meta, root)
        if target.exists():
            continue  # already copied
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(photo.read_bytes())
${Array.from({ length: 30 }, (_, i) => `# line ${i + 25}: ${"a long comment that runs past the edge of the window ".repeat(3)}`).join("\n")}
`;
const textUrl = (body: string) => URL.createObjectURL(new Blob([body], { type: "text/plain" }));

const files: Record<string, { item: FileItem; preview: () => PreviewSource }> = {
  image: {
    item: { id: "1", name: "Abisko-2025.jpg", kind: "image", modified: "2026-10-20T10:00:00Z", owner: "Alice" },
    preview: () => ({ type: "image", src: thumbnails.mountains }),
  },
  pdf: {
    item: { id: "2", name: "Hyresavtal-2026.pdf", kind: "pdf", modified: "2026-10-01T10:00:00Z", owner: "Alice" },
    preview: () => ({ type: "pdf", src: samplePdf() }),
  },
  markdown: {
    item: { id: "3", name: "README.md", kind: "text", modified: "2026-10-22T10:00:00Z", owner: "Alice" },
    preview: () => ({ type: "markdown", src: textUrl(markdown) }),
  },
  code: {
    item: { id: "7", name: "sync_photos.py", kind: "text", modified: "2026-10-22T10:00:00Z", owner: "Alice" },
    preview: () => ({ type: "text", src: textUrl(code) }),
  },
  preparing: {
    item: { id: "4", name: "IMG_4021.HEIC", kind: "image", modified: "2026-10-23T10:00:00Z", owner: "Alice" },
    preview: () => ({ type: "preparing" }),
  },
  converting: {
    item: { id: "8", name: "Midsommar-2026.mkv", kind: "video", modified: "2026-06-21T10:00:00Z", owner: "Alice" },
    preview: () => ({ type: "converting", waiting: false, progress: 42 }),
  },
  waiting: {
    item: { id: "9", name: "MVI_0412.AVI", kind: "video", modified: "2026-06-21T10:00:00Z", owner: "Alice" },
    preview: () => ({ type: "converting", waiting: true }),
  },
  unplayable: {
    item: { id: "10", name: "intervju.wma", kind: "audio", modified: "2026-03-02T10:00:00Z", owner: "Alice" },
    preview: () => ({ type: "unplayable" }),
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
          "The viewer (§14). Images zoom with the toolbar or a double-click and pan by dragging; PDFs render with pdf.js one page at a time (Page Up/Down); text shows the first 1 MiB in a read-only CodeMirror with highlighting, folding and find, and Markdown is formatted with a Source toggle. Video and audio browsers can't play wait for the worker's playable copy, with its progress. Arrow keys or a swipe move to the next file, Esc closes. The arrows here step through every state.",
      },
    },
  },
} satisfies Meta<typeof Viewer>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Image: Story = { args: { file: "image" } };
export const Pdf: Story = { args: { file: "pdf" } };
export const Markdown: Story = { args: { file: "markdown" } };
export const Code: Story = { args: { file: "code" } };
export const Preparing: Story = { args: { file: "preparing" } };
export const Converting: Story = { args: { file: "converting" } };
export const WaitingForCopy: Story = { args: { file: "waiting" } };
export const Unplayable: Story = { args: { file: "unplayable" } };
export const NoPreview: Story = { args: { file: "none" } };
export const Failed: Story = { args: { file: "failed" } };
