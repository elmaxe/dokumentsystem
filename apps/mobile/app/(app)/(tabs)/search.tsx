import { useTranslation } from "react-i18next";
import { View } from "react-native";
import { EmptyState } from "../../../src/ui/EmptyState";
import { AccountButton } from "../../../src/ui/ListActions";
import { TopBar } from "../../../src/ui/TopBar";

/** Search comes to the app in a later step of M5. */
export default function Search() {
  const { t } = useTranslation();
  return (
    <View className="flex-1 bg-surface">
      <TopBar title={t("nav.search")} actions={<AccountButton />} />
      <EmptyState icon="search" title={t("mobile.searchSoon.title")} text={t("mobile.searchSoon.text")} />
    </View>
  );
}
