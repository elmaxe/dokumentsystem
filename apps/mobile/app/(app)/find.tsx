import { useLocalSearchParams } from "expo-router";
import { SearchScreen } from "../../src/ui/SearchScreen";

/** A search started from a folder's top bar: limited to that folder, which the In chip can take off (§15.3). */
export default function Find() {
  const { folder } = useLocalSearchParams<{ folder?: string }>();
  return <SearchScreen initial={folder ? { folder } : {}} back />;
}
