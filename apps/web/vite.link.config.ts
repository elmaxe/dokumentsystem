import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// The link page alone, for the links gateway (docs/design.md §9.1): what the public side serves holds none of the
// rest of the app's code.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    outDir: "dist-links",
    emptyOutDir: true,
    rollupOptions: { input: "link.html" },
  },
});
