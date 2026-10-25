import type { TFunction } from "i18next";
import type { ActivityEntry, FileDetails } from "../components/DetailsPanel";
import type { SharePerson } from "../components/ShareDialog";
import type { UploadItem } from "../components/UploadTray";
import type { FileVersion } from "../components/VersionHistory";
import { isFolder, type FileItem } from "../lib/files";
import { pages, thumbnails } from "./thumbnails";

// A household's library, in Swedish like the real one would be. Dates are relative to now so
// "Today" and "Yesterday" show up.

const hour = 60 * 60 * 1000;
const day = 24 * hour;
const ago = (ms: number) => new Date(Date.now() - ms).toISOString();

export type Node = FileItem & {
  parentId: string | null;
  /** For trashed items: days until they are deleted forever. */
  trashedDaysLeft?: number;
  sharedWithMe?: boolean;
  openedAt?: string;
  /** Text the search matched, with the hit in [[double brackets]]. */
  text?: string;
  ocr?: boolean;
  page?: string;
  pages?: number;
};

export const root = { id: "root", name: "" };

const n = (node: Omit<Node, "owner"> & { owner?: string }): Node => ({ owner: "Axel", ...node });

export const initialNodes: Node[] = [
  n({ id: "kvitton", parentId: null, name: "Kvitton", kind: "folder", modified: ago(2 * hour) }),
  n({ id: "forsakringar", parentId: null, name: "Försäkringar", kind: "folder", modified: ago(40 * day) }),
  n({ id: "familjen", parentId: null, name: "Familjen – delat", kind: "folder", modified: ago(1 * day), shared: true }),
  n({ id: "bostad", parentId: null, name: "Bostadsrätten", kind: "folder", modified: ago(90 * day) }),
  n({
    id: "ikea",
    parentId: null,
    name: "ikea-kvitto-2025-03.pdf",
    kind: "pdf",
    size: 182_000,
    modified: ago(3 * hour),
    thumbnail: thumbnails.receipt,
    starred: true,
    openedAt: ago(1 * hour),
    page: pages.receipt,
    pages: 1,
    text: "IKEA Kungens Kurva … [[kvitto]] nr 4471 … BILLY bokhylla 3 st",
    ocr: true,
  }),
  n({
    id: "abisko",
    parentId: null,
    name: "Abisko-midnattssol.heic",
    kind: "image",
    size: 4_800_000,
    modified: ago(220 * day),
    thumbnail: thumbnails.mountains,
    shared: true,
    openedAt: ago(1 * day + 2 * hour),
  }),
  n({
    id: "hyresavtal",
    parentId: null,
    name: "Hyresavtal Odengatan.docx",
    kind: "doc",
    size: 64_000,
    modified: ago(262 * day),
    thumbnail: thumbnails.document,
    page: pages.contract,
    pages: 4,
    text: "… hyresgästen ska betala [[hyra]] i förskott senast den sista …",
  }),
  n({
    id: "midsommar",
    parentId: null,
    name: "Midsommar-2025.mp4",
    kind: "video",
    size: 812_000_000,
    modified: ago(489 * day),
    thumbnail: thumbnails.video,
    duration: "3:42",
  }),
  n({ id: "budget", parentId: null, name: "Budget 2026.xlsx", kind: "sheet", size: 38_000, modified: ago(5 * 60 * 1000), thumbnailPending: true, openedAt: ago(5 * 60 * 1000) }),
  n({ id: "deklaration", parentId: null, name: "Deklaration-2025.zip", kind: "archive", size: 12_400_000, modified: ago(540 * day) }),

  n({ id: "k1", parentId: "kvitton", name: "Elgiganten-diskmaskin.pdf", kind: "pdf", size: 240_000, modified: ago(2 * hour), thumbnail: thumbnails.receipt, page: pages.receipt, pages: 1, text: "Elgiganten … [[kvitto]] … Bosch diskmaskin, 2 års garanti", ocr: true }),
  n({ id: "k2", parentId: "kvitton", name: "Clas Ohlson 2026-09.pdf", kind: "pdf", size: 96_000, modified: ago(33 * day), thumbnail: thumbnails.receipt, page: pages.receipt, pages: 1 }),
  n({ id: "k3", parentId: "kvitton", name: "Biltema-vinterdäck.jpg", kind: "image", size: 2_100_000, modified: ago(48 * day), thumbnail: thumbnails.receipt, text: "Biltema … [[kvitto]] … vinterdäck 4 st", ocr: true }),
  n({ id: "k-2025", parentId: "kvitton", name: "2025", kind: "folder", modified: ago(300 * day) }),
  n({ id: "k-2025-1", parentId: "k-2025", name: "ikea-kvitto-2025-03.pdf", kind: "pdf", size: 182_000, modified: ago(220 * day), thumbnail: thumbnails.receipt, page: pages.receipt, pages: 1 }),

  n({ id: "f1", parentId: "forsakringar", name: "Hemförsäkring Folksam 2026.pdf", kind: "pdf", size: 410_000, modified: ago(40 * day), thumbnail: thumbnails.document, page: pages.contract, pages: 6, text: "Folksam … [[hemförsäkring]] … självrisk 1 500 kr" }),
  n({ id: "f2", parentId: "forsakringar", name: "Bilförsäkring.pdf", kind: "pdf", size: 290_000, modified: ago(120 * day), thumbnail: thumbnails.document, page: pages.contract, pages: 3 }),

  n({ id: "fam1", parentId: "familjen", name: "Stugan i Dalarna.jpg", kind: "image", size: 3_900_000, modified: ago(1 * day), thumbnail: thumbnails.cabin, owner: "Karin" }),
  n({ id: "fam2", parentId: "familjen", name: "Sommarschema.xlsx", kind: "sheet", size: 22_000, modified: ago(6 * day), thumbnail: thumbnails.sheet, owner: "Karin" }),
  n({ id: "fam3", parentId: "familjen", name: "Bröllopstal.pptx", kind: "slides", size: 5_600_000, modified: ago(70 * day), thumbnail: thumbnails.slides }),

  n({ id: "b1", parentId: "bostad", name: "Lägenhetsritning.pdf", kind: "pdf", size: 1_200_000, modified: ago(90 * day), thumbnail: thumbnails.floorPlan, page: pages.contract, pages: 1 }),
  n({ id: "b2", parentId: "bostad", name: "Årsredovisning BRF 2025.pdf", kind: "pdf", size: 2_800_000, modified: ago(160 * day), thumbnail: thumbnails.document, page: pages.contract, pages: 18, text: "Föreningen har höjt [[avgiften]] med 5 % från 1 januari" }),

  n({ id: "s1", parentId: null, name: "Torneträsk-solnedgång.jpg", kind: "image", size: 5_200_000, modified: ago(3 * day), thumbnail: thumbnails.lake, owner: "Karin", sharedWithMe: true }),
  n({ id: "s2", parentId: null, name: "Elsas skolfoton", kind: "folder", modified: ago(12 * day), owner: "Karin", sharedWithMe: true, shared: true }),
  n({ id: "s3", parentId: null, name: "Pappas recept.docx", kind: "doc", size: 48_000, modified: ago(400 * day), thumbnail: thumbnails.document, owner: "Lennart", sharedWithMe: true, page: pages.contract, pages: 2 }),

  n({ id: "t1", parentId: null, name: "gammal-budget-2024.xlsx", kind: "sheet", size: 31_000, modified: ago(20 * day), trashedDaysLeft: 10 }),
  n({ id: "t2", parentId: null, name: "IMG_4410.jpg", kind: "image", size: 3_100_000, modified: ago(27 * day), thumbnail: thumbnails.lake, trashedDaysLeft: 3 }),
];

export const people: SharePerson[] = [
  { name: "Axel", email: "axel@hemma.se", role: "owner", you: true, tone: 1 },
  { name: "Karin Lindqvist", email: "karin@hemma.se", role: "editor", tone: 2, inheritedFrom: "Familjen – delat" },
  { name: "Lennart Eriksson", email: "lennart@hemma.se", role: "viewer", tone: 3 },
];

const iso = (ms: number) => ago(ms);

export const versions: FileVersion[] = [
  { number: 4, current: true, by: "Axel", tone: 1, at: iso(3 * hour), size: 182_000, comment: "Rätt moms" },
  { number: 3, keep: true, by: "Karin Lindqvist", tone: 2, at: iso(12 * day), size: 179_000, comment: "Original från butiken" },
  { number: 2, by: "Axel", tone: 1, at: iso(27 * day), size: 176_000, removedInDays: 3 },
  { number: 1, by: "Axel", tone: 1, at: iso(29 * day), size: 160_000, removedInDays: 1 },
];

const activity = (t: TFunction): ActivityEntry[] => [
  { id: "a1", who: "Axel", tone: 1, what: t("activity.uploadedVersion", { who: "Axel", number: 4 }), at: iso(3 * hour) },
  { id: "a2", who: "Karin Lindqvist", tone: 2, what: t("activity.downloaded", { who: "Karin Lindqvist" }), at: iso(2 * day) },
  { id: "a3", who: "Axel", tone: 1, what: t("activity.shared", { who: "Axel", with: "Lennart Eriksson" }), at: iso(12 * day) },
];

export const details = (t: TFunction, item: FileItem, location: string): FileDetails => ({
  location,
  created: iso(29 * day),
  checksum: item.kind === "folder" ? undefined : "9f2c 41aa 07be 5d13 e8c0 77f4 2b19 a6d0 3c55 8e21 f0b4 6a97 1d3e c802 554b e7a9",
  people: item.shared ? people : people.slice(0, 1),
  publicLink: item.shared,
  activity: activity(t),
});

export const quota = { used: 412_000_000_000, quota: 500_000_000_000 };

export const sampleUploads: UploadItem[] = [
  { id: "1", name: "scan-2026-10-21-001.pdf", kind: "pdf", state: "done", size: 820_000, folder: "Kvitton" },
  { id: "2", name: "Midsommar-2025-hela.mov", kind: "video", state: "uploading", size: 2_300_000_000, uploaded: 1_400_000_000, secondsLeft: 240, folder: "Mina filer" },
  { id: "3", name: "Abisko-panorama.dng", kind: "image", state: "paused", size: 120_000_000, uploaded: 34_000_000, folder: "Mina filer" },
  { id: "4", name: "ikea-kvitto-2025-03.pdf", kind: "pdf", state: "duplicate", size: 182_000, folder: "Kvitton", duplicateOf: "Mina filer / Kvitton / 2025" },
  { id: "5", name: "Lägenhetsritning.pdf", kind: "pdf", state: "failed", size: 1_200_000, folder: "Bostadsrätten" },
  { id: "6", name: "Bröllopsfoton.zip", kind: "archive", state: "hashing", size: 3_400_000_000, folder: "Mina filer" },
];

/** Gives each folder its size and counts from everything below it, as the server does (§16.4). */
export function withFolderContents(items: Node[], nodes: Node[]): Node[] {
  const contents = (id: string) => {
    let size = 0;
    let files = 0;
    let folders = 0;
    const walk = (parentId: string) => {
      for (const child of nodes.filter((n) => n.parentId === parentId)) {
        if (isFolder(child)) {
          folders++;
          walk(child.id);
        } else {
          files++;
          size += child.size ?? 0;
        }
      }
    };
    walk(id);
    return { size, contents: { files, folders } };
  };
  return items.map((item) => (isFolder(item) ? { ...item, ...contents(item.id) } : item));
}
