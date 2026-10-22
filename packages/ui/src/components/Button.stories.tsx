import type { Meta, StoryObj } from "@storybook/react-vite";
import { Button, IconButton } from "./Button";

const meta = {
  title: "Components/Button",
  component: Button,
  args: { children: "Upload files", variant: "filled" },
} satisfies Meta<typeof Button>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Filled: Story = {};

/** Filled is the one main action per view; tonal, outlined and text step down from it (§16.4). */
export const Variants: Story = {
  render: () => (
    <div className="flex flex-wrap items-center gap-3">
      <Button icon="upload">Upload files</Button>
      <Button variant="tonal" icon="add">
        New folder
      </Button>
      <Button variant="outlined" icon="link">
        Copy link
      </Button>
      <Button variant="text">Cancel</Button>
      <Button variant="danger" icon="delete_forever">
        Delete forever
      </Button>
      <Button disabled>Save changes</Button>
    </div>
  ),
};

/** Every icon button has a label, which is also its tooltip. */
export const IconButtons: Story = {
  render: () => (
    <div className="flex items-center gap-2">
      <IconButton icon="grid_view" label="Grid view" selected />
      <IconButton icon="view_list" label="List view" />
      <IconButton icon="info" label="Show details" />
      <IconButton icon="more_vert" label="More actions" size="sm" />
      <IconButton icon="delete" label="Move to trash" disabled />
    </div>
  ),
};
