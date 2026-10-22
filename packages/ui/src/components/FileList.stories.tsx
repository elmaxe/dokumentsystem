import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { sortItems, type Sort } from "../lib/files";
import { fileMenu } from "../mockup/actions";
import { initialNodes } from "../mockup/data";
import { FileList } from "./FileList";

function Interactive({ width, initial = [] as string[] }: { width?: number; initial?: string[] }) {
  const { t, i18n } = useTranslation();
  const [selected, setSelected] = useState(initial);
  const [sort, setSort] = useState<Sort>({ key: "name", dir: "asc" });
  const items = sortItems(
    initialNodes.filter((n) => n.parentId === null && !n.sharedWithMe && n.trashedDaysLeft === undefined),
    sort,
    i18n.language,
  );
  return (
    <div style={{ maxWidth: width }}>
      <FileList
        items={items}
        selected={selected}
        onSelectedChange={setSelected}
        sort={sort}
        onSortChange={setSort}
        menuActions={(item) => fileMenu(t, item)}
        onAction={() => {}}
      />
    </div>
  );
}

const meta = {
  title: "Components/FileList",
  component: Interactive,
} satisfies Meta<typeof Interactive>;

export default meta;
type Story = StoryObj<typeof meta>;

/** Click a column heading to sort. Folders always stay first. */
export const Default: Story = { args: { initial: ["ikea"] } };

/** Under 600px of its own width, the columns fold into a second line. */
export const Narrow: Story = { args: { width: 420 } };
