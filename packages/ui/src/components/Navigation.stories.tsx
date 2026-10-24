import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { AccountMenu, type LanguageChoice } from "./AccountMenu";
import { AdminBottomNav, AdminRail } from "./AdminNav";
import type { AdminPlace } from "../lib/places";
import { Avatar } from "./Avatar";
import { BottomNav, type BottomPlace } from "./BottomNav";
import { Breadcrumbs } from "./Breadcrumbs";
import type { Place } from "../lib/places";
import { NavRail } from "./NavRail";

const meta = { title: "Components/Navigation", parameters: { layout: "fullscreen" } } satisfies Meta;
export default meta;
type Story = StoryObj<typeof meta>;

const newActions = [
  { id: "folder", icon: "create_new_folder", label: "New folder" },
  { id: "upload", icon: "upload_file", label: "Upload files", separatorBefore: true },
];

function Rail({ mode }: { mode: "expanded" | "collapsed" }) {
  const [active, setActive] = useState<Place>("files");
  return (
    <div className="flex h-160">
      <NavRail mode={mode} active={active} onNavigate={setActive} newActions={newActions} used={412_000_000_000} quota={500_000_000_000} />
    </div>
  );
}

/** 1200px and up: 256px with labels beside the icons and the storage meter. */
export const RailExpanded: Story = { name: "Nav rail, expanded", render: () => <Rail mode="expanded" /> };

/** 840–1199px: 88px with labels under the icons. */
export const RailCollapsed: Story = { name: "Nav rail, collapsed", render: () => <Rail mode="collapsed" /> };

/** On admin pages the admin rail takes the nav rail's place. */
export const Admin: Story = {
  name: "Admin rail",
  render: function Render() {
    const [active, setActive] = useState<AdminPlace>("people");
    return (
      <div className="flex h-160">
        <AdminRail mode="expanded" active={active} onNavigate={setActive} onExit={() => {}} />
      </div>
    );
  },
};

/** The admin pages under 840px. */
export const AdminBottom: Story = {
  name: "Admin bottom nav",
  render: function Render() {
    const [active, setActive] = useState<AdminPlace>("people");
    return (
      <div className="flex h-dvh flex-col justify-end">
        <AdminBottomNav active={active} onNavigate={setActive} />
      </div>
    );
  },
  globals: { viewport: { value: "phone", isRotated: false } },
};

/** Under 840px and in the app. */
export const Bottom: Story = {
  name: "Bottom nav",
  render: function Render() {
    const [active, setActive] = useState<BottomPlace>("files");
    return (
      <div className="flex h-dvh flex-col justify-end">
        <BottomNav active={active} onNavigate={setActive} />
      </div>
    );
  },
  globals: { viewport: { value: "phone", isRotated: false } },
};

/** The last crumb is the page title. On phones only the parent and the title show. */
export const Crumbs: Story = {
  name: "Breadcrumbs",
  render: () => (
    <div className="p-6">
      <Breadcrumbs
        onNavigate={() => {}}
        path={[
          { id: "root", name: "My files" },
          { id: "a", name: "Kvitton" },
          { id: "b", name: "2025" },
          { id: "c", name: "Elektronik och vitvaror" },
        ]}
      />
    </div>
  ),
};

/** The avatar menu: language and a link to settings for this person, admin settings for admins, and sign-out. */
export const Account: Story = {
  name: "Account menu",
  render: function Render() {
    const [language, setLanguage] = useState<LanguageChoice>("browser");
    return (
      <div className="flex justify-end p-6">
        <AccountMenu
          name="Axel"
          email="axel@hemma.se"
          language={language}
          currentLanguage="en"
          onLanguageChange={setLanguage}
          onSettings={() => {}}
          onAdmin={() => {}}
          onSignOut={() => {}}
          trigger={
            <button type="button" aria-label="Account" className="cursor-pointer rounded-full">
              <Avatar name="Axel" tone={1} />
            </button>
          }
        />
      </div>
    );
  },
};
