import type { Meta, StoryObj } from "@storybook/react-vite";
import type { FileKind } from "../lib/files";
import { FileTypeIcon } from "./FileTypeIcon";

const kinds: FileKind[] = ["folder", "pdf", "doc", "sheet", "slides", "image", "video", "audio", "archive", "text", "other"];

const meta = {
  title: "Components/FileTypeIcon",
  component: FileTypeIcon,
  args: { kind: "pdf" },
} satisfies Meta<typeof FileTypeIcon>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Single: Story = {};

/** One colour per MIME group. Folders stay neutral; the colours meet 3:1 on every surface. */
export const AllKinds: Story = {
  render: () => (
    <div className="grid grid-cols-[repeat(auto-fill,minmax(120px,1fr))] gap-4">
      {[...kinds.map((kind) => ({ kind, shared: false })), { kind: "folder" as const, shared: true }].map(
        ({ kind, shared }) => (
          <div key={kind + shared} className="flex flex-col items-center gap-2 rounded-md bg-surface-container-lowest p-4">
            <FileTypeIcon kind={kind} shared={shared} size={40} />
            <span className="text-body-sm text-on-surface-variant">{shared ? "folder (shared)" : kind}</span>
          </div>
        ),
      )}
    </div>
  ),
};
