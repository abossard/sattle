import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

const enginePort = (globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env.ENGINE_PORT ?? "5080";
const engine = { "/engine": { target: `http://127.0.0.1:${enginePort}`, changeOrigin: true, ws: true, rewrite: (p: string) => p.replace(/^\/engine/, "") } };

export default defineConfig({
  base: "./",
  plugins: [react()],
  server: { fs: { allow: [".."] }, proxy: engine },
  preview: { proxy: engine },
  worker: { format: "es" },
  build: { target: "es2022" },
  // four workers: the jsdom files are heavy, and with every core taken by other suites their axe runs and dialogs starve; the time limits are ceilings for a busy machine (another suite beside this one): a test that is done returns at once; what they wait for is a condition, not a time
  test: { maxWorkers: 4, testTimeout: 30000, hookTimeout: 30000, include: ["tests/**/*.test.{ts,tsx}"], environment: "node", setupFiles: ["tests/setup.ts"] },
} as Parameters<typeof defineConfig>[0]);
