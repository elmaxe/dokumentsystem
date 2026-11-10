import { useLocalSearchParams } from "expo-router";
import { SearchScreen } from "../../src/ui/SearchScreen";

/**
 * A search started from a folder's top bar, limited to that folder, which the In chip can take off, or from a tag in
 * a file's info, for the files with that tag (§15.3).
 */
export default function Find() {
  const { folder, tag } = useLocalSearchParams<{ folder?: string; tag?: string }>();
  return <SearchScreen initial={{ ...(folder ? { folder } : {}), ...(tag ? { tag } : {}) }} back />;
}
