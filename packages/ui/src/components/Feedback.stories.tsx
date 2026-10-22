import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { ActionBar } from "./ActionBar";
import { Banner } from "./Banner";
import { Button } from "./Button";
import { EmptyState } from "./EmptyState";
import { Progress } from "./Progress";
import { useSnackbar } from "../lib/snackbar";

const meta = { title: "Components/Feedback" } satisfies Meta;
export default meta;
type Story = StoryObj<typeof meta>;

/** Status pairs a colour with an icon and a sentence (§16.1). */
export const Banners: Story = {
  render: () => (
    <div className="flex max-w-160 flex-col gap-3">
      <Banner tone="info">Items in the trash are deleted forever after 30 days.</Banner>
      <Banner tone="success">Connected · version 1.4.0</Banner>
      <Banner tone="warning">Scanning is off. Uploaded files aren't checked for viruses.</Banner>
      <Banner tone="error" action={<Button variant="text">Retry</Button>}>
        Can't reach the ML service. Check the URL and that the container is running.
      </Banner>
    </div>
  ),
};

export const ProgressBars: Story = {
  render: () => (
    <div className="flex w-80 flex-col gap-4">
      <Progress value={62} label="Uploading" />
      <Progress value={28} tone="paused" label="Paused" />
      <Progress label="Checking for duplicates" />
    </div>
  ),
};

/** Shows the empty folder, and the drop target that names the destination. */
export const EmptyStates: Story = {
  render: () => (
    <div className="grid gap-6 md:grid-cols-2">
      <EmptyState
        title="This folder is empty"
        text="Drop files here, or choose New to upload or make a folder."
        action={<Button icon="upload">Upload files</Button>}
      />
      <EmptyState dropping title="Drop to upload to Kvitton" text="Files are checked for duplicates before they upload." />
    </div>
  ),
};

export const SelectionBar: Story = {
  name: "Action bar",
  render: function Render() {
    const [count, setCount] = useState(3);
    return (
      <div className="max-w-180">
        <ActionBar
          count={count}
          onClear={() => setCount(0)}
          onAction={() => {}}
          actions={[
            { id: "share", icon: "person_add", label: "Share" },
            { id: "download", icon: "download", label: "Download" },
            { id: "move", icon: "drive_file_move", label: "Move" },
            { id: "trash", icon: "delete", label: "Move to trash" },
          ]}
          moreActions={[{ id: "star", icon: "star", label: "Add star" }]}
        />
      </div>
    );
  },
};

/** Toasts confirm in the past tense. */
export const Snackbar: Story = {
  render: function Render() {
    const show = useSnackbar();
    return (
      <div className="flex gap-3">
        <Button variant="outlined" onClick={() => show("Link copied")}>
          Copy link
        </Button>
        <Button variant="outlined" onClick={() => show("Moved 2 items to the trash", { label: "Undo", onClick: () => {} })}>
          Move to trash
        </Button>
      </div>
    );
  },
};
