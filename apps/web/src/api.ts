import { type CurrentSession, createApiClient } from "@dokument/api";
import { queryOptions } from "@tanstack/react-query";

let csrfToken: string | null = null;

export const api = createApiClient({ csrfToken: () => csrfToken });

export const serverInfoQuery = queryOptions({
  queryKey: ["server", "info"],
  queryFn: async () => {
    const { data, error } = await api.GET("/api/v1/server/info");
    if (error || !data) throw new Error("Server info unavailable");
    return data;
  },
});

export const authConfigQuery = queryOptions({
  queryKey: ["auth", "config"],
  queryFn: async () => {
    const { data, error } = await api.GET("/api/v1/auth/config");
    if (error || !data) throw new Error("Auth config unavailable");
    return data;
  },
  staleTime: Infinity,
});

/** The signed-in session, or null when nobody is signed in. Other failures throw. */
export const sessionQuery = queryOptions({
  queryKey: ["auth", "session"],
  queryFn: async (): Promise<CurrentSession | null> => {
    const { data, response } = await api.GET("/api/v1/auth/session");
    if (response.status === 401) {
      csrfToken = null;
      return null;
    }
    if (!data) throw new Error("Session unavailable");
    csrfToken = data.csrfToken ?? null;
    return data;
  },
  staleTime: 5 * 60_000,
});

export async function logout(): Promise<void> {
  await api.POST("/api/v1/auth/logout");
  csrfToken = null;
}
