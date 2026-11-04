import { Redirect } from "expo-router";

/**
 * Where the server's sign-in bounce sends the browser back to the app (docs/design.md §5). The sign-in screen reads
 * the code from the auth session; if the link opens the app another way, there's nothing to do here but go home.
 */
export default function SignedIn() {
  return <Redirect href="/" />;
}
