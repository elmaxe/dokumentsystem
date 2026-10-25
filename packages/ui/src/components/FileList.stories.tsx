import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { DisplaySettingsProvider } from "../lib/display";
import { sortItems, type Sort } from "../lib/files";
import { fileMenu } from "../mockup/actions";
import { initialNodes, withFolderContents } from "../mockup/data";
import { FileList } from "./FileList";

function Interactive({
  width,
  initial = [] as string[],
  showSeconds = false,
}: {
  width?: number;
  initial?: string[];
  showSeconds?: boolean;
}) {
  const { t, i18n } = useTranslation();
  const [selected, setSelected] = useState(initial);
  const [sort, setSort] = useState<Sort>({ key: "name", dir: "asc" });
  const live = initialNodes.filter((n) => n.trashedDaysLeft === undefined);
  const items = sortItems(
    withFolderContents(
      live.filter((n) => n.parentId === null && !n.sharedWithMe),
      live,
    ),
    sort,
    i18n.language,
  );
  return (
    <DisplaySettingsProvider value={{ showSeconds }}>
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
    </DisplaySettingsProvider>
  );
}

const meta = {
  title: "Components/FileList",
  component: Interactive,
} satisfies Meta<typeof Interactive>;

export default meta;
type Story = StoryObj<typeof meta>;

/**
 * Click a column heading to sort. Folders always stay first and show the size of what they hold; files with a
 * thumbnail show it instead of the type icon. Hover a date for the full timestamp.
 */
export const Default: Story = { args: { initial: ["ikea"] } };

/** Under 600px of its own width, the columns fold into a second line. */
export const Narrow: Story = { args: { width: 420 } };

/** With "Show seconds in times" on in the account menu. */
export const WithSeconds: Story = { name: "With seconds", args: { showSeconds: true } };
