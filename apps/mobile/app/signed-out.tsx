import { Redirect } from "expo-router";

/**
 * Where the server's sign-out bounce sends the browser back to the app, after the IdP signed the person out (docs/
 * design.md §5). The app has already signed out, so there's nothing to do here but go home.
 */
export default function SignedOut() {
  return <Redirect href="/" />;
}
