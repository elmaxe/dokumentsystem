import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { people } from "../mockup/data";
import { DuplicateDialog } from "./DuplicateDialog";
import { TextDialog } from "./FileInfo";
import { ShareDialog, type ShareLink } from "./ShareDialog";

const meta = {
  title: "Components/Dialogs",
  parameters: { layout: "fullscreen" },
} satisfies Meta;

export default meta;
type Story = StoryObj<typeof meta>;

const noop = () => {};

const batch = [
  { name: "ikea-kvitto-2025-03.pdf", kind: "pdf" as const, path: "Mina filer / Kvitton / 2025" },
  { name: "Abisko-midnattssol.heic", kind: "image" as const, path: "Mina filer" },
  { name: "IMG_4410.jpg", kind: "image" as const, path: "Mina filer", trashed: true },
  { name: "Budget 2026.xlsx", kind: "sheet" as const, path: "Mina filer" },
];

/** A batch with duplicates offers Skip duplicates / Upload all / Review (§8.3). */
export const DuplicatesBatch: Story = {
  render: () => <DuplicateDialog open onOpenChange={noop} total={12} duplicates={batch} onSkip={noop} onUploadAll={noop} onSecondary={noop} />,
};

/** One file offers Skip / Upload anyway / Open existing. */
export const DuplicateSingle: Story = {
  render: () => (
    <DuplicateDialog open onOpenChange={noop} total={1} duplicates={batch.slice(0, 1)} onSkip={noop} onUploadAll={noop} onSecondary={noop} />
  ),
};

/** Under 600px dialogs become bottom sheets with full-width buttons. */
export const DuplicateSheet: Story = {
  ...DuplicatesBatch,
  globals: { viewport: { value: "phone", isRotated: false } },
};

function Share() {
  const [list, setList] = useState(people);
  const [link, setLink] = useState<ShareLink>({ enabled: true, expiresInDays: 7, password: false, allowDownload: true });
  return (
    <ShareDialog
      open
      onOpenChange={noop}
      name="Familjen – delat"
      people={list}
      onPeopleChange={setList}
      link={link}
      onLinkChange={setLink}
      onCopyLink={noop}
    />
  );
}

/** Inherited access is labelled with its folder and can't be lowered here. */
export const ShareDialogDefault: Story = { name: "Share dialog", render: () => <Share /> };

export const ShareSheet: Story = {
  name: "Share sheet",
  render: () => <Share />,
  globals: { viewport: { value: "phone", isRotated: false } },
};

const longText = {
  state: "ready" as const,
  ocrSkipped: false,
  pageCount: 42,
  totalPages: 40,
  maxPage: 42,
  words: 18_250,
  editedPages: 1,
  pages: [],
};

function LongText() {
  const [page, setPage] = useState(3);
  return (
    <TextDialog
      open
      onOpenChange={noop}
      name="Årsredovisning 2025.pdf"
      text={longText}
      pages={
        page === 7
          ? []
          : [
              {
                pageNo: page,
                source: page % 2 ? ("ocr" as const) : ("text" as const),
                text: `Sida ${page}\n\nFörvaltningsberättelse. Föreningen har under året fortsatt arbetet med stambytet och ny ventilation i trapphus A och B.`,
                edited: page === 3,
                editedBy: page === 3 ? "Axel" : undefined,
              },
            ]
      }
      paging="pages"
      pager={{ page, total: 42, onPage: setPage }}
      onCopy={noop}
      onSavePage={async () => {}}
      onRevertPage={async () => {}}
    />
  );
}

/** Files over 10 pages show their text one page at a time (§13.5). Page 7 has no text. */
export const TextOnePageAtATime: Story = { name: "Text, one page at a time", render: () => <LongText /> };

export const TextOnePageAtATimeSheet: Story = {
  ...TextOnePageAtATime,
  name: "Text, one page at a time (phone)",
  globals: { viewport: { value: "phone", isRotated: false } },
};
