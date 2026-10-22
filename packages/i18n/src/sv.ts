import type { Strings } from "./index";

export const sv: Strings = {
  app: {
    name: "Dokumentsystem",
  },
  nav: {
    myFiles: "Mina filer",
    sharedWithMe: "Delade med mig",
    recent: "Senaste",
    starred: "Stjärnmärkta",
    trash: "Papperskorg",
    search: "Sök",
  },
  auth: {
    title: "Logga in på Dokumentsystem",
    body: "Använd kontot du har i hushållets inloggningstjänst.",
    signIn: "Logga in",
    signInWith: "Logga in med {{provider}}",
    notConfigured: "Inloggningen är inte konfigurerad än. Be din administratör att ställa in OIDC för servern.",
    signedInAs: "Inloggad som {{name}}",
    signOut: "Logga ut",
    errors: {
      not_allowed:
        "Ditt konto har inte åtkomst till Dokumentsystem. Be din administratör att lägga till dig i rätt grupp.",
      disabled: "Ditt konto har inaktiverats. Kontakta din administratör om du tror att det är fel.",
      failed: "Inloggningen slutfördes inte. Försök igen och kontakta din administratör om det fortsätter.",
    },
  },
  status: {
    connected: "Ansluten till servern, version {{version}}",
    connecting: "Ansluter till servern",
    unreachable: "Servern svarar inte. Kontrollera att den är igång och försök igen.",
  },
};
