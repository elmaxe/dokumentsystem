import { describe, expect, it } from "vitest";
import { authorizeUrl, discoveryUrl, readCallback } from "./oidc";

describe("discoveryUrl", () => {
  it.each([
    ["https://auth.example.com/realms/dokument", "https://auth.example.com/realms/dokument/.well-known/openid-configuration"],
    ["https://auth.example.com/application/o/dokument/", "https://auth.example.com/application/o/dokument/.well-known/openid-configuration"],
  ])("for %s", (issuer, url) => expect(discoveryUrl(issuer)).toBe(url));
});

describe("authorizeUrl", () => {
  it("asks for a code with PKCE", () => {
    const url = new URL(
      authorizeUrl({
        authorizationEndpoint: "https://auth.example.com/auth?kc_idp_hint=x",
        clientId: "dokument",
        scope: "openid profile email",
        redirectUri: "https://files.example.com/api/v1/auth/app-callback",
        challenge: "abc",
        state: "xyz",
      }),
    );
    expect(Object.fromEntries(url.searchParams)).toEqual({
      kc_idp_hint: "x",
      response_type: "code",
      client_id: "dokument",
      scope: "openid profile email",
      redirect_uri: "https://files.example.com/api/v1/auth/app-callback",
      code_challenge: "abc",
      code_challenge_method: "S256",
      state: "xyz",
    });
  });
});

describe("readCallback", () => {
  it("returns the code when the state matches", () => {
    expect(readCallback("dokumentsystem://signed-in?code=c1&state=s1", "s1")).toEqual({ code: "c1" });
  });

  it("refuses another sign-in's answer", () => {
    expect(readCallback("dokumentsystem://signed-in?code=c1&state=other", "s1")).toEqual({ error: "state_mismatch" });
  });

  it("passes the IdP's error on", () => {
    expect(readCallback("dokumentsystem://signed-in?error=access_denied&error_description=No&state=s1", "s1")).toEqual({
      error: "access_denied",
      description: "No",
    });
  });
});
