import { useTranslation } from "react-i18next";
import { View } from "react-native";
import { EmptyState } from "../../../src/ui/EmptyState";
import { AccountButton } from "../../../src/ui/ListActions";
import { TopBar } from "../../../src/ui/TopBar";

/** Shared with me arrives with sharing (M4); until then the tab says so. */
export default function Shared() {
  const { t } = useTranslation();
  return (
    <View className="flex-1 bg-surface">
      <TopBar title={t("nav.sharedWithMe")} actions={<AccountButton />} />
      <EmptyState
        icon="folder_shared"
        title={t("mobile.sharingSoon.title")}
        text={t("mobile.sharingSoon.text")}
      />
    </View>
  );
}
