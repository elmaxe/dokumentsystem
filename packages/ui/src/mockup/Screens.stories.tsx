import type { Meta, StoryObj } from "@storybook/react-vite";
import { sampleUploads } from "./data";
import { MockupApp } from "./MockupApp";

/**
 * The clickable mockup (docs/design.md §16.5). Every screen is the same app on fake data, so you can
 * click from one to the next: open folders and files, select, share, upload (drop real files or use
 * New), search, and open Admin settings from the account menu. Use the toolbar to switch theme,
 * language and width.
 */
const meta = {
  title: "Screens",
  component: MockupApp,
  parameters: { layout: "fullscreen" },
} satisfies Meta<typeof MockupApp>;

export default meta;
type Story = StoryObj<typeof meta>;

export const FileBrowser: Story = { name: "File browser" };

export const FileBrowserPhone: Story = {
  name: "File browser (phone)",
  globals: { viewport: { value: "phone", isRotated: false } },
};

/** List view with one file selected and the details panel open on its versions. */
export const ListWithDetails: Story = {
  name: "List and details",
  args: {
    initialView: "list",
    initialSelected: ["ikea"],
    initialDetails: { open: true, tab: "versions" },
  },
};

export const Folder: Story = {
  name: "Inside a folder",
  args: { initialRoute: { screen: "files", place: "files", folderId: "kvitton" } },
};

/** The upload tray with every state. Pause, resume, retry and remove the duplicate copy. */
export const Uploading: Story = { args: { initialUploads: sampleUploads } };

/** Picking files that are already in the library shows the duplicate prompt first (§8.3). */
export const DuplicatePrompt: Story = {
  name: "Duplicate prompt",
  args: {
    initialPick: [
      { name: "ikea-kvitto-2025-03.pdf", size: 182_000 },
      { name: "Abisko-midnattssol.heic", size: 4_800_000 },
      { name: "kvitto-apotek.pdf", size: 90_000 },
      { name: "scan-2026-10-22.pdf", size: 640_000 },
    ],
  },
};

export const Sharing: Story = { args: { initialShare: "familjen" } };

export const Viewer: Story = {
  args: {
    initialRoute: {
      screen: "viewer",
      fileId: "ikea",
      back: { screen: "files", place: "files", folderId: null },
    },
  },
};

export const ViewerPhone: Story = {
  name: "Viewer (phone)",
  args: Viewer.args,
  globals: { viewport: { value: "phone", isRotated: false } },
};

export const SearchResults: Story = {
  name: "Search results",
  args: { initialRoute: { screen: "search", query: "kvitto" } },
};

export const SearchResultsPhone: Story = {
  name: "Search results (phone)",
  args: SearchResults.args,
  globals: { viewport: { value: "phone", isRotated: false } },
};

export const SharedWithMe: Story = {
  name: "Shared with me",
  args: { initialRoute: { screen: "files", place: "shared", folderId: null } },
};

export const Trash: Story = { args: { initialRoute: { screen: "files", place: "trash", folderId: null } } };

export const AdminSettings: Story = {
  name: "Admin settings",
  args: { initialRoute: { screen: "admin" } },
};

export const AdminSettingsPhone: Story = {
  name: "Admin settings (phone)",
  args: { initialRoute: { screen: "admin" } },
  globals: { viewport: { value: "phone", isRotated: false } },
};
