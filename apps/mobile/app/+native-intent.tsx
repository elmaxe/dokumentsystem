/**
 * The iOS share extension opens the app at "dokumentsystem://dataUrl=…", which isn't a screen: it goes to the share
 * screen, which reads the files from expo-share-intent (docs/design.md §15.3). Every other link is left as it is.
 */
export function redirectSystemPath({ path }: { path: string; initial: boolean }) {
  return path.includes("dataUrl=") ? "/share" : path;
}
