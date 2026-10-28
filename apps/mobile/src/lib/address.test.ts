import { describe, expect, it } from "vitest";
import { displayAddress, parseAddress } from "./address";

describe("parseAddress", () => {
  it.each([
    ["files.example.com", "https://files.example.com"],
    ["  https://files.example.com/ ", "https://files.example.com"],
    ["https://example.com/dokument/", "https://example.com/dokument"],
    ["HTTPS://Files.Example.com:8443", "https://files.example.com:8443"],
  ])("reads %j as %s", (input, url) => {
    expect(parseAddress(input, { allowHttp: false })).toEqual({ ok: true, url });
  });

  it("allows http only when asked", () => {
    expect(parseAddress("http://192.168.1.20:8080", { allowHttp: false })).toEqual({ ok: false, error: "insecure" });
    expect(parseAddress("http://192.168.1.20:8080", { allowHttp: true })).toEqual({
      ok: true,
      url: "http://192.168.1.20:8080",
    });
  });

  it.each([
    ["", "empty"],
    ["   ", "empty"],
    ["ftp://files.example.com", "invalid"],
    ["https://files.example.com/?a=1", "invalid"],
    ["https://user:pw@files.example.com", "invalid"],
    ["https://", "invalid"],
  ])("refuses %j", (input, error) => {
    expect(parseAddress(input, { allowHttp: true })).toEqual({ ok: false, error });
  });
});

describe("displayAddress", () => {
  it("drops the scheme", () => {
    expect(displayAddress("https://files.example.com/dokument")).toBe("files.example.com/dokument");
  });
});
