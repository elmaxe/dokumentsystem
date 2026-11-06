import AsyncStorage from "@react-native-async-storage/async-storage";
import { createAsyncStoragePersister } from "@tanstack/query-async-storage-persister";
import { QueryClient, type Query } from "@tanstack/react-query";

/** How long what was on screen is kept for showing offline (decision 5 in docs/design.md §15.3). */
export const offlineFor = 7 * 24 * 60 * 60_000;

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      gcTime: offlineFor,
      // Without a connection, show what was cached rather than an error.
      networkMode: "offlineFirst",
      retry: (failures, error) => failures < 2 && !(error instanceof Error && "status" in error),
    },
  },
});

export const persister = createAsyncStoragePersister({ storage: AsyncStorage, key: "dokument.cache" });

/** Only listings and the account are kept: they're what the offline view shows. */
const kept = new Set(["folder", "node", "starred", "recent", "storage", "auth", "server"]);

export const persistOptions = {
  persister,
  maxAge: offlineFor,
  dehydrateOptions: {
    shouldDehydrateQuery: (query: Query) => query.state.status === "success" && kept.has(String(query.queryKey[0])),
  },
};
