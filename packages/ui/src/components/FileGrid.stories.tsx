import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { sortItems } from "../lib/files";
import { fileMenu } from "../mockup/actions";
import { initialNodes } from "../mockup/data";
import { FileGrid } from "./FileGrid";

const items = sortItems(
  initialNodes.filter((n) => n.parentId === null && !n.sharedWithMe && n.trashedDaysLeft === undefined),
  { key: "name", dir: "asc" },
  "sv",
);

function Interactive({ initial = [] as string[] }) {
  const { t } = useTranslation();
  const [selected, setSelected] = useState(initial);
  return (
    <FileGrid
      items={items}
      selected={selected}
      onSelectedChange={setSelected}
      menuActions={(item) => fileMenu(t, item)}
      onAction={() => {}}
    />
  );
}

const meta = {
  title: "Components/FileGrid",
  component: Interactive,
  parameters: {
    docs: {
      description: {
        component:
          "Folders first, then 4:3 thumbnails. Click selects, Ctrl/Cmd-click toggles, Shift-click selects a range, arrows move, Space selects. On touch, long-press selects. The spreadsheet is still waiting for its thumbnail; the video shows its duration.",
      },
    },
  },
} satisfies Meta<typeof Interactive>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Default: Story = {};

export const WithSelection: Story = { args: { initial: ["abisko", "hyresavtal"] } };

/** Two columns on phones. */
export const Phone: Story = {
  globals: { viewport: { value: "phone", isRotated: false } },
};
