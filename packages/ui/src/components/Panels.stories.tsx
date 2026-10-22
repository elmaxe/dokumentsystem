import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { details, initialNodes, versions as sampleVersions } from "../mockup/data";
import { DetailsPanel, type DetailsTab } from "./DetailsPanel";
import { StorageMeter } from "./StorageMeter";
import { VersionHistory } from "./VersionHistory";

const meta = { title: "Components/Panels" } satisfies Meta;
export default meta;
type Story = StoryObj<typeof meta>;

const item = initialNodes.find((n) => n.id === "ikea")!;

function Details({ initialTab }: { initialTab: DetailsTab }) {
  const { t } = useTranslation();
  const [tab, setTab] = useState(initialTab);
  const [versions, setVersions] = useState(sampleVersions);
  return (
    <DetailsPanel
      item={{ ...item, shared: true }}
      details={details(t, { ...item, shared: true }, "Mina filer")}
      tab={tab}
      onTabChange={setTab}
      onClose={() => {}}
      versions={{
        versions,
        onAction: (action, version) =>
          (action === "keep" || action === "unkeep") &&
          setVersions(versions.map((v) => (v.number === version.number ? { ...v, keep: action === "keep" } : v))),
      }}
      className="h-180 rounded-lg"
    />
  );
}

export const DetailsInfo: Story = { name: "Details: info", render: () => <Details initialTab="info" /> };

/** Current and kept versions are marked; others warn before retention removes them (§8.2). */
export const DetailsVersions: Story = { name: "Details: versions", render: () => <Details initialTab="versions" /> };

export const VersionHistoryOnly: Story = {
  name: "Version history",
  render: () => (
    <div className="w-90">
      <VersionHistory versions={sampleVersions} onAction={() => {}} />
    </div>
  ),
};

/** Warns at 80 % and is critical at 95 %, in words as well as colour. */
export const StorageMeters: Story = {
  render: () => (
    <div className="flex w-64 flex-col gap-6">
      <StorageMeter used={212_000_000_000} quota={500_000_000_000} />
      <StorageMeter used={412_000_000_000} quota={500_000_000_000} />
      <StorageMeter used={489_000_000_000} quota={500_000_000_000} />
    </div>
  ),
};
