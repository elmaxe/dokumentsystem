import { useState } from "react";
import { useTranslation } from "react-i18next";
import { View } from "react-native";
import { Button } from "./Button";
import { Sheet } from "./Sheet";
import { TextField } from "./TextField";

/**
 * Asks for a name: New folder and Rename (docs/design.md §15.3). The name is selected up to its extension, so typing
 * keeps ".pdf". Give it a key that changes per use, so it starts from `initial` each time.
 */
export function NameSheet({
  open,
  onClose,
  title,
  label,
  submitLabel,
  initial,
  onSubmit,
}: {
  open: boolean;
  onClose: () => void;
  title: string;
  label: string;
  submitLabel: string;
  initial: string;
  /** Resolves to an error message to show, or null when done. */
  onSubmit: (value: string) => Promise<string | null>;
}) {
  const { t } = useTranslation();
  const [value, setValue] = useState(initial);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const dot = initial.lastIndexOf(".");
  const [selection, setSelection] = useState<{ start: number; end: number } | undefined>({
    start: 0,
    end: dot > 0 ? dot : initial.length,
  });

  const submit = async () => {
    const name = value.trim();
    if (!name || busy) return;
    setBusy(true);
    const problem = await onSubmit(name);
    setBusy(false);
    if (problem) setError(problem);
    else onClose();
  };

  return (
    <Sheet open={open} onClose={onClose} title={title}>
      <View className="mx-5 gap-4">
        <TextField
          label={label}
          value={value}
          error={error}
          maxLength={255}
          autoFocus
          autoCorrect={false}
          // Selects the name once, then lets the cursor go where the person puts it.
          selection={selection}
          onSelectionChange={({ nativeEvent }) => {
            if (selection && (nativeEvent.selection.start !== selection.start || nativeEvent.selection.end !== selection.end))
              setSelection(undefined);
          }}
          onChangeText={(text) => {
            setValue(text);
            setError(null);
          }}
          returnKeyType="done"
          onSubmitEditing={() => void submit()}
        />
        <View className="flex-row justify-end gap-2">
          <Button variant="text" label={t("common.cancel")} onPress={onClose} />
          <Button label={submitLabel} busy={busy} disabled={!value.trim()} onPress={() => void submit()} />
        </View>
      </View>
    </Sheet>
  );
}
