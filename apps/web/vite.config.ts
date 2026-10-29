import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// In development the API runs separately (Aspire or `dotnet run`); production serves this build from the server.
// Aspire passes the server's address as services__server__http__0.
const apiTarget =
  process.env.services__server__http__0 ?? process.env.API_URL ?? "http://localhost:5080";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: Number(process.env.PORT ?? 5173),
    // Objects rather than plain targets, which would rewrite the Host header: the server builds its redirect URIs from
    // the address the browser or phone used.
    proxy: {
      // WebSockets too, for live updates (docs/design.md §15.4).
      "/api": { target: apiTarget, ws: true },
      "/health": { target: apiTarget },
      // The API reference, so it signs in and calls the API at the same public URL as the app.
      "/scalar": { target: apiTarget },
      "/openapi": { target: apiTarget },
    },
  },
});
