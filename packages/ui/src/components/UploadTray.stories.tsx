import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { sampleUploads } from "../mockup/data";
import { UploadTray } from "./UploadTray";


const meta = {
  title: "Components/UploadTray",
  component: UploadTray,
  args: { items: sampleUploads },
  render: function Render(args) {
    const [collapsed, setCollapsed] = useState(args.collapsed);
    return <UploadTray {...args} collapsed={collapsed} onCollapsedChange={setCollapsed} />;
  },
} satisfies Meta<typeof UploadTray>;

export default meta;
type Story = StoryObj<typeof meta>;

/** Every state with an icon and words: checking, uploading, paused, done, duplicate and failed. */
export const AllStates: Story = {};

export const Collapsed: Story = { args: { collapsed: true } };

export const Finished: Story = {
  args: { items: sampleUploads.filter((u) => u.state === "done" || u.state === "duplicate"), onClose: () => {} },
};
