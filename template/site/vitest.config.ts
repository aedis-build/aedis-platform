import { fileURLToPath } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react()],
  resolve: { alias: { "@": fileURLToPath(new URL(".", import.meta.url)) } },
  test: {
    environment: "jsdom",
    setupFiles: ["./test/setup.ts"],
    include: ["test/**/*.test.{ts,tsx}"],
    coverage: {
      provider: "v8",
      include: ["app/**", "lib/**", "proxy.ts"],
      // cobertura: o relatório que o coverage-js.yml do Aedis Platform lê; limiares do nível L3 (85/70).
      reporter: ["text-summary", "cobertura"],
      thresholds: { lines: 85, branches: 70 },
    },
  },
});
