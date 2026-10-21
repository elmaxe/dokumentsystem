import { describe, expect, it } from "vitest";
import { createI18n, pickLanguage } from "./index";

describe("pickLanguage", () => {
  it("prefers Swedish for sv locales", () => {
    expect(pickLanguage(["sv-SE", "en-US"])).toBe("sv");
  });

  it("falls back to English", () => {
    expect(pickLanguage(["de-DE"])).toBe("en");
  });
});

describe("createI18n", () => {
  it("interpolates Swedish strings", async () => {
    const i18n = await createI18n("sv");
    expect(i18n.t("status.connected", { version: "1.0" })).toBe("Ansluten till servern, version 1.0");
  });
});
