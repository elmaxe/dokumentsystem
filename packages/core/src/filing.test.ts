import { describe, expect, it } from "vitest";
import { exampleFolder, folderProblem, pickedTemplate, templateLabel } from "./filing";

describe("folderProblem", () => {
  it("accepts year and month in any case", () => {
    expect(folderProblem("Kvitton/{year}/{MONTH}")).toBeNull();
  });

  it("refuses an empty path and unknown placeholders", () => {
    expect(folderProblem(" / ")).toBe("empty");
    expect(folderProblem("Kvitton/{day}")).toBe("placeholder");
    expect(folderProblem("Kvitton/{year")).toBe("placeholder");
  });
});

describe("exampleFolder", () => {
  it("fills in the year and a two-digit month", () => {
    expect(exampleFolder("Räkningar/ {year} /{month}", new Date(2026, 2, 5))).toBe("Räkningar / 2026 / 03");
  });
});

describe("templateLabel", () => {
  it("names the placeholders in words and drops empty folders", () => {
    expect(templateLabel("Kvitton//{year}/", { year: "År", month: "Månad" })).toBe("Kvitton / År");
  });
});

describe("pickedTemplate", () => {
  it("keeps the placeholders that ended the old template", () => {
    expect(pickedTemplate(["Kvitton", "Hem"], "Papper/{year}/{month}")).toBe("Kvitton/Hem/{year}/{month}");
  });

  it("drops the old folders and placeholders between them", () => {
    expect(pickedTemplate(["Kvitton"], "{year}/Papper")).toBe("Kvitton");
    expect(pickedTemplate(["Kvitton"], "")).toBe("Kvitton");
  });
});
