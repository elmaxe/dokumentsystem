import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { Chip } from "./Chip";

const meta = {
  title: "Components/Chip",
  component: Chip,
  args: { children: "Type: PDF", selected: true },
} satisfies Meta<typeof Chip>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Selected: Story = {};

/** Search filters mirror the query operators: "Type: PDF" is type:pdf (§16.4). */
export const SearchFilters: Story = {
  render: function Render() {
    const [mine, setMine] = useState(false);
    const [pdf, setPdf] = useState(true);
    return (
      <div className="flex flex-wrap gap-2">
        {pdf ? (
          <Chip icon="filter_list" selected onRemove={() => setPdf(false)}>
            Type: PDF
          </Chip>
        ) : (
          <Chip icon="filter_list" onClick={() => setPdf(true)}>
            Type
          </Chip>
        )}
        <Chip selected={mine} onClick={() => setMine(!mine)}>
          {mine ? "Owner: me" : "Owner"}
        </Chip>
        <Chip icon="event">Modified</Chip>
      </div>
    );
  },
};
