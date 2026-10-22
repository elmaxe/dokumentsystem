export const en = {
  app: {
    name: "Dokumentsystem",
  },
  nav: {
    myFiles: "My files",
    sharedWithMe: "Shared with me",
    recent: "Recent",
    starred: "Starred",
    trash: "Trash",
    search: "Search",
  },
  auth: {
    title: "Sign in to Dokumentsystem",
    body: "Use the account you have with your household's sign-in service.",
    signIn: "Sign in",
    signInWith: "Sign in with {{provider}}",
    notConfigured: "Sign-in isn't set up yet. Ask your admin to configure OIDC for the server.",
    signedInAs: "Signed in as {{name}}",
    signOut: "Sign out",
    errors: {
      not_allowed:
        "Your account doesn't have access to Dokumentsystem. Ask your admin to add you to the right group.",
      disabled: "Your account has been disabled. Contact your admin if you think this is a mistake.",
      failed: "Sign-in didn't finish. Try again, and contact your admin if it keeps failing.",
    },
  },
  status: {
    connected: "Connected to server version {{version}}",
    connecting: "Connecting to the server",
    unreachable: "Can't reach the server. Check that it's running and try again.",
  },
};
