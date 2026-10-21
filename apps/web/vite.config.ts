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
    proxy: {
      "/api": apiTarget,
      "/health": apiTarget,
    },
  },
});
