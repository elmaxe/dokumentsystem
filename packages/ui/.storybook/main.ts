import tailwindcss from "@tailwindcss/vite";
import type { StorybookConfig } from "@storybook/react-vite";

const config: StorybookConfig = {
  framework: "@storybook/react-vite",
  stories: ["../src/**/*.stories.tsx"],
  addons: ["@storybook/addon-a11y"],
  core: { disableTelemetry: true },
  viteFinal(config) {
    config.plugins = [...(config.plugins ?? []), tailwindcss()];
    // Radix ships "use client" directives, which only matter to server components.
    const onwarn = config.build?.rollupOptions?.onwarn;
    config.build = {
      ...config.build,
      chunkSizeWarningLimit: 2000,
      rollupOptions: {
        ...config.build?.rollupOptions,
        onwarn(warning, warn) {
          if (warning.code === "MODULE_LEVEL_DIRECTIVE") return;
          if (onwarn) onwarn(warning, warn);
          else warn(warning);
        },
      },
    };
    return config;
  },
};

export default config;
