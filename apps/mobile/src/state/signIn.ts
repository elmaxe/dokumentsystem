import { createApiClient, type AuthConfig, type CurrentSession, type ServerInfo } from "@dokument/api";
import * as Crypto from "expo-crypto";
import * as Device from "expo-device";
import * as WebBrowser from "expo-web-browser";
import { authorizeUrl, discoveryUrl, readCallback } from "../lib/oidc";
import { createPkce } from "../lib/pkce";

/** Where the server's sign-in page sends the IdP's code back to the app (docs/design.md §5). */
export const appReturnUrl = "dokumentsystem://signed-in";

export type FoundServer = { url: string; info: ServerInfo; auth: AuthConfig };

export class SignInError extends Error {
  constructor(
    readonly reason: "unreachable" | "notDokument" | "notConfigured" | "cancelled" | "failed" | "not_allowed" | "disabled",
    message?: string,
  ) {
    super(message ?? reason);
  }
}

/** Checks that an address is a Dokumentsystem server and reads how it signs people in. */
export async function findServer(url: string): Promise<FoundServer> {
  const api = createApiClient({ baseUrl: url });
  let info: ServerInfo | undefined;
  let auth: AuthConfig | undefined;
  try {
    [info, auth] = await Promise.all([
      api.GET("/api/v1/server/info").then((r) => r.data),
      api.GET("/api/v1/auth/config").then((r) => r.data),
    ]);
  } catch {
    throw new SignInError("unreachable");
  }
  if (info?.name !== "Dokumentsystem" || !auth) throw new SignInError("notDokument");
  return { url, info, auth };
}

/**
 * Signs in at the server's OIDC provider in the system browser and redeems the code for one of our bearer sessions
 * through `POST /api/v1/auth/token` (§5). The server holds the client secret, so the app only needs PKCE.
 */
export async function signIn(server: FoundServer): Promise<{ token: string; session: CurrentSession }> {
  const { auth } = server;
  if (!auth.enabled || !auth.issuer || !auth.clientId || !auth.appRedirectUri) throw new SignInError("notConfigured");

  let authorizationEndpoint: string;
  try {
    const discovery = (await (await fetch(discoveryUrl(auth.issuer))).json()) as { authorization_endpoint?: string };
    if (!discovery.authorization_endpoint) throw new Error("No authorization_endpoint");
    authorizationEndpoint = discovery.authorization_endpoint;
  } catch {
    throw new SignInError("unreachable");
  }

  const pkce = createPkce(Crypto.getRandomBytes(64));
  const result = await WebBrowser.openAuthSessionAsync(
    authorizeUrl({
      authorizationEndpoint,
      clientId: auth.clientId,
      scope: auth.scope ?? "openid profile email",
      redirectUri: auth.appRedirectUri,
      challenge: pkce.challenge,
      state: pkce.state,
    }),
    appReturnUrl,
  );
  if (result.type !== "success") throw new SignInError("cancelled");
  const callback = readCallback(result.url, pkce.state);
  if ("error" in callback) {
    throw new SignInError(callback.error === "access_denied" ? "not_allowed" : "failed", callback.description);
  }

  const response = await fetch(`${server.url}/api/v1/auth/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      grant_type: "authorization_code",
      code: callback.code,
      code_verifier: pkce.verifier,
      redirect_uri: auth.appRedirectUri,
      device_name: Device.modelName ?? Device.deviceName ?? "",
    }).toString(),
  }).catch(() => {
    throw new SignInError("unreachable");
  });
  const body = (await response.json().catch(() => ({}))) as { access_token?: string; error?: string; error_description?: string };
  if (!response.ok || !body.access_token) {
    throw new SignInError(body.error === "access_denied" ? "not_allowed" : "failed", body.error_description);
  }

  const { data: session } = await createApiClient({ baseUrl: server.url, token: body.access_token }).GET(
    "/api/v1/auth/session",
  );
  if (!session) throw new SignInError("failed");
  return { token: body.access_token, session };
}
