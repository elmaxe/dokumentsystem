import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { people } from "../mockup/data";
import { DuplicateDialog } from "./DuplicateDialog";
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
