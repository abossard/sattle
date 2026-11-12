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
  test: { include: ["tests/**/*.test.{ts,tsx}"], environment: "node", setupFiles: ["tests/setup.ts"] },
} as Parameters<typeof defineConfig>[0]);
