import { useLocalSearchParams } from "expo-router";
import { FolderView } from "../../../src/ui/FolderView";

export default function Folder() {
  const { id } = useLocalSearchParams<{ id: string }>();
  return <FolderView folderId={id} />;
}
