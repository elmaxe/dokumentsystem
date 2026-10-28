/** Where the app opens the IdP's sign-in, from its discovery document (docs/design.md §5). */
export function discoveryUrl(issuer: string): string {
  return `${issuer.replace(/\/+$/, "")}/.well-known/openid-configuration`;
}

export function authorizeUrl(options: {
  authorizationEndpoint: string;
  clientId: string;
  scope: string;
  redirectUri: string;
  challenge: string;
  state: string;
}): string {
  const url = new URL(options.authorizationEndpoint);
  url.searchParams.set("response_type", "code");
  url.searchParams.set("client_id", options.clientId);
  url.searchParams.set("scope", options.scope);
  url.searchParams.set("redirect_uri", options.redirectUri);
  url.searchParams.set("code_challenge", options.challenge);
  url.searchParams.set("code_challenge_method", "S256");
  url.searchParams.set("state", options.state);
  return url.toString();
}

export type Callback = { code: string } | { error: string; description?: string };

/**
 * Reads the app link the server's sign-in page sent back (`dokumentsystem://signed-in?code=…&state=…`). A missing or
 * different `state` means the answer belongs to some other sign-in, so it's refused.
 */
export function readCallback(link: string, expectedState: string): Callback {
  const query = link.includes("?") ? link.slice(link.indexOf("?") + 1).split("#")[0]! : "";
  const params = new URLSearchParams(query);
  if (params.get("state") !== expectedState) return { error: "state_mismatch" };
  const error = params.get("error");
  if (error) return { error, description: params.get("error_description") ?? undefined };
  const code = params.get("code");
  return code ? { code } : { error: "invalid_request" };
}
