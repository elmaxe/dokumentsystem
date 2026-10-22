import { createApiClient } from "@dokument/api";
import { queryOptions } from "@tanstack/react-query";

export const api = createApiClient();

export const serverInfoQuery = queryOptions({
  queryKey: ["server", "info"],
  queryFn: async () => {
    const { data, error } = await api.GET("/api/v1/server/info");
    if (error || !data) throw new Error("Server info unavailable");
    return data;
  },
});
