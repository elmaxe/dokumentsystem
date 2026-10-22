import { createContext, use } from "react";

export type SnackbarMessage = { text: string; action?: { label: string; onClick: () => void } };
type Show = (text: string, action?: SnackbarMessage["action"]) => void;

export const SnackbarContext = createContext<Show | null>(null);

/** Shows a snackbar. Needs a SnackbarProvider above it. */
export function useSnackbar(): Show {
  const show = use(SnackbarContext);
  if (!show) throw new Error("useSnackbar needs a SnackbarProvider");
  return show;
}
