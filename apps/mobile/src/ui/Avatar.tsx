import { avatarUrl } from "@dokument/api";
import { initials } from "@dokument/core";
import { Image } from "expo-image";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { View } from "react-native";
import { authHeaders, baseUrl } from "../state/api";
import { Text } from "./Text";

const tones = {
  0: { fill: "bg-secondary-container", text: "text-on-secondary-container" },
  1: { fill: "bg-primary-container", text: "text-on-primary-container" },
} as const;

/**
 * A person's profile picture, or their initials in a tonal circle (§5.1), as on the web. Pass the person's id and
 * `avatar` from their DTO; initials show under the picture until it loads and instead of it when it can't.
 * Decorative: the name is always written next to it.
 */
export function Avatar({
  name,
  id,
  avatar,
  size = 32,
  tone = 0,
}: {
  name: string;
  id?: string;
  avatar?: string | null;
  size?: number;
  /** 1 for the person themself (the account button and sheet), 0 for everyone else. */
  tone?: 0 | 1;
}) {
  const { i18n } = useTranslation();
  const [failed, setFailed] = useState<string | null>(null);
  const uri = id ? avatarUrl(id, avatar, baseUrl()) : null;
  const colors = tones[tone];
  return (
    <View
      style={{ width: size, height: size }}
      className={`items-center justify-center overflow-hidden rounded-full ${colors.fill}`}
      accessible={false}
      importantForAccessibility="no-hide-descendants"
    >
      <Text variant={size > 40 ? "title-lg" : "label-lg"} className={colors.text}>
        {initials(name, i18n.language) || "?"}
      </Text>
      {uri && failed !== uri ? (
        <Image
          source={{ uri, headers: authHeaders(), cacheKey: `avatar/${id}/${avatar}` }}
          onError={() => setFailed(uri)}
          contentFit="cover"
          transition={120}
          style={{ position: "absolute", top: 0, left: 0, width: size, height: size }}
        />
      ) : null}
    </View>
  );
}
