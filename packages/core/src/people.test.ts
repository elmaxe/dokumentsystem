import { describe, expect, it } from "vitest";
import { initials } from "./people";

describe("initials", () => {
  it.each([
    ["Axel Elmarsson", "AE"],
    ["Anna Maria Svensson", "AS"],
    ["Åsa Öberg", "ÅÖ"],
    ["alice", "A"],
    ["axel@example.com", "A"],
    ["  bob   lind  ", "BL"],
    ["(Bob) Lind", "BL"],
    ["", ""],
  ])("%s → %s", (name, expected) => {
    expect(initials(name)).toBe(expected);
  });
});
