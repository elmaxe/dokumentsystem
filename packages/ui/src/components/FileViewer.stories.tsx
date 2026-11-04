import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { FileItem } from "../lib/files";
import { thumbnails } from "../mockup/thumbnails";
import { FileViewer, type FileViewerProps, type PreviewSource } from "./FileViewer";

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
/** A quiet tone, `seconds` long, as an 8 kHz WAV, so the player has something to play and seek in. */
function sampleWav(seconds: number): string {
  const rate = 8000;
  const samples = rate * seconds;
  const bytes = new Uint8Array(44 + samples);
  const view = new DataView(bytes.buffer);
  const ascii = (offset: number, text: string) => [...text].forEach((c, i) => view.setUint8(offset + i, c.charCodeAt(0)));
  ascii(0, "RIFF");
  view.setUint32(4, 36 + samples, true);
  ascii(8, "WAVEfmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, rate, true);
  view.setUint32(28, rate, true);
  view.setUint16(32, 1, true);
  view.setUint16(34, 8, true);
  ascii(36, "data");
  view.setUint32(40, samples, true);
  for (let i = 0; i < samples; i++) bytes[44 + i] = 128 + Math.round(4 * Math.sin((i / rate) * 2 * Math.PI * 220));
  return URL.createObjectURL(new Blob([bytes], { type: "audio/wav" }));
}

/**
 * A made-up waveform in the worker's format (§14): speech in bursts, quieter at times, with the given stretches of
 * silence, in 20 ms slices.
 */
function sampleWaveform(seconds: number, silences: readonly (readonly [number, number])[] = []) {
  const count = seconds * 50;
  const bytes = new Uint8Array(16 + count * 3);
  const view = new DataView(bytes.buffer);
  [..."DKWF"].forEach((c, i) => view.setUint8(i, c.charCodeAt(0)));
  view.setUint8(4, 1);
  view.setFloat32(8, 0.02, true);
  view.setUint32(12, count, true);
  const noise = (i: number) => {
    const x = Math.sin(i * 12.9898 + 78.233) * 43758.5453;
    return x - Math.floor(x);
  };
  for (let i = 0; i < count; i++) {
    const t = i / 50;
    const quiet = silences.some(([from, to]) => t >= from && t < to);
    const gate = Math.sin(t * 1.25 + 0.9 * Math.sin(t * 0.37)) < -0.82 ? 0.05 : 1;
    const syllable = Math.abs(Math.sin(t * Math.PI * 4.1 + 1.6 * Math.sin(t * 0.83))) ** 0.9;
    const level = 0.58 + 0.3 * Math.sin(t * 0.23 + 1.1);
    const amp = quiet ? 0.012 + 0.01 * noise(i) : Math.min(1, gate * syllable * level * (0.7 + 0.3 * noise(i)) + 0.02);
    bytes[16 + i * 3] = Math.round(255 * amp * (0.8 + 0.2 * noise(i + 7)));
    bytes[17 + i * 3] = Math.round(255 * amp);
    bytes[18 + i * 3] = Math.round(255 * amp * (0.36 + 0.14 * noise(i + 3)));
  }
  return URL.createObjectURL(new Blob([bytes], { type: "application/octet-stream" }));
}

const meetingLines = [
  [0, "Hej, tack för att du kunde ses. Jag tänkte vi kunde gå igenom det som hänt med fönstren först."],
  [9_000, "Ja, och sen var det frågan om balkongen, som de ska renovera nästa år."],
  [18_000, "Men det påverkar inte hyran, sa han, i alla fall inte i år."],
  [27_000, "Och då sa han att vi måste skriva om hyresavtalet innan juni, annars gäller det gamla hyresavtal fortfarande,"],
  [36_500, "med samma uppsägningstid som förut. Tre månader."],
  [44_000, "Okej. Kan du maila det till mig så jag har det skriftligt?"],
  [52_000, "Absolut, jag skickar ett utkast i veckan."],
  [120_000, "Sådär, då är vi tillbaka. Var var vi? Just det, hyresavtal för förrådet också."],
  [131_000, "Det tar vi nästa gång."],
] as const;

const meeting: FileViewerProps["transcript"] = {
  state: "ready",
  language: "sv",
  model: "kb-whisper-small",
  durationMs: 150_000,
  lines: meetingLines.map(([startMs, text], i) => ({
    startMs,
    endMs: Math.min(meetingLines[i + 1]?.[0] ?? 150_000, startMs + 8_500),
    text,
  })),
};

const textUrl = (body: string) => URL.createObjectURL(new Blob([body], { type: "text/plain" }));

const files: Record<string, { item: FileItem; preview: () => PreviewSource; extra?: Partial<FileViewerProps> }> = {
  transcript: {
    item: { id: "11", name: "Möte med hyresvärden.m4a", kind: "audio", modified: "2026-03-12T17:40:00Z", owner: "Alice" },
    preview: () => ({ type: "audio", src: sampleWav(150), waveform: sampleWaveform(150, [[60, 120]]) }),
    extra: {
      transcript: meeting,
      onTranscriptRedo: () => {},
      startAtMs: 19_400,
      marks: {
        query: "hyresavtal",
        terms: ["hyresavtal"],
        pages: [],
        times: [29_400, 33_900, 126_800],
        onClear: () => {},
      },
    },
  },
  listening: {
    item: { id: "12", name: "Röstmemo 2026-09-02.m4a", kind: "audio", modified: "2026-09-02T08:12:00Z", owner: "Alice" },
    preview: () => ({ type: "audio", src: sampleWav(60), waveform: sampleWaveform(60) }),
    extra: { transcript: { ...meeting, state: "running", doneMs: 36_000, lines: meeting.lines.slice(0, 4) } },
  },
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
  video: {
    item: { id: "13", name: "Årsstämma 2026.mp4", kind: "video", modified: "2026-05-12T19:00:00Z", owner: "Alice" },
    preview: () => ({
      type: "video",
      src: "sample.webm",
      seekPreviews: { src: "sample-seek.webp", intervalMs: 2000, count: 15, columns: 10, frameWidth: 256, frameHeight: 144 },
    }),
    extra: {
      transcript: {
        state: "ready",
        language: "sv",
        model: "large-v3-turbo",
        durationMs: 30_000,
        lines: [
          { startMs: 1_000, endMs: 5_000, text: "Nästa punkt på dagordningen är motion nummer fyra." },
          { startMs: 6_000, endMs: 9_500, text: "Den gäller frågan om andrahandsuthyrning," },
          { startMs: 10_000, endMs: 16_000, text: "där styrelsen föreslår att hyresavtal för andra hand alltid ska godkännas skriftligt." },
          { startMs: 17_000, endMs: 21_000, text: "Finns det några frågor om det? Ja, där bak." },
          { startMs: 22_000, endMs: 27_000, text: "Gäller det även korttidsuthyrning, alltså några veckor?" },
        ],
      },
      captions: "data:text/vtt,WEBVTT",
      marks: { query: "hyresavtal", terms: ["hyresavtal"], pages: [], times: [10_400], onClear: () => {} },
    },
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
  const { item, extra } = files[current]!;
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
      {...extra}
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
/** Opened from a search hit: plays from 10 s before it, with the transcript beside the player and ticks for each hit. */
export const Transcript: Story = { args: { file: "transcript" } };
export const StillListening: Story = { args: { file: "listening" } };
/**
 * Our own controls over the picture: ticks for the hits, captions from the transcript (CC), speed, volume, full screen.
 * Pointing at the timeline shows the frame from that moment, cut from the seek-preview sheet.
 */
export const Video: Story = { args: { file: "video" } };
export const Pdf: Story = { args: { file: "pdf" } };
export const Markdown: Story = { args: { file: "markdown" } };
export const Code: Story = { args: { file: "code" } };
export const Preparing: Story = { args: { file: "preparing" } };
export const Converting: Story = { args: { file: "converting" } };
export const WaitingForCopy: Story = { args: { file: "waiting" } };
export const Unplayable: Story = { args: { file: "unplayable" } };
export const NoPreview: Story = { args: { file: "none" } };
export const Failed: Story = { args: { file: "failed" } };
