import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { Checkbox } from "./Checkbox";
import { SearchBar } from "./SearchBar";
import { Switch } from "./Switch";
import { TextField } from "./TextField";

const meta = { title: "Components/Inputs" } satisfies Meta;
export default meta;
type Story = StoryObj<typeof meta>;

export const Fields: Story = {
  render: () => (
    <div className="flex max-w-120 flex-col gap-5">
      <TextField label="ML service URL" defaultValue="http://gpu-box.lan:3003" hint="Leave empty to turn it off." mono />
      <TextField label="Issuer URL" defaultValue="auth.hemma" error="That isn't a URL. Start it with https://." />
      <TextField icon="person_add" placeholder="Add people by name or email" aria-label="Add people" />
    </div>
  ),
};

export const Search: Story = {
  render: function Render() {
    const [value, setValue] = useState("kvitto");
    return <SearchBar value={value} onChange={setValue} className="max-w-180" />;
  },
};

export const Toggles: Story = {
  render: function Render() {
    const [on, setOn] = useState(true);
    const [checked, setChecked] = useState(false);
    return (
      <div className="flex items-center gap-6">
        <Switch checked={on} onCheckedChange={setOn} label="Public link" />
        <Checkbox checked={checked} onCheckedChange={setChecked} label="Select" />
        <Checkbox checked="indeterminate" label="Select all" />
      </div>
    );
  },
};
