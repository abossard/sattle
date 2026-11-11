import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

const env = (globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env;
const enginePort = env.ENGINE_PORT ?? "5080";
const engine = { "/engine": { target: `http://127.0.0.1:${enginePort}`, changeOrigin: true, ws: true, rewrite: (p: string) => p.replace(/^\/engine/, "") } };

export default defineConfig({
  base: "./",
  plugins: [react()],
  server: { fs: { allow: [".."] }, proxy: engine },
  preview: { proxy: engine },
  worker: { format: "es" },
  build: { target: "es2022" },
  // tests/review holds one test per open defect of the code review: they fail until the defect is fixed, so `npm test` leaves them out and `npm run test:review` runs them
  test: { include: ["tests/**/*.test.{ts,tsx}"], exclude: env.SATTLE_REVIEW ? ["**/node_modules/**"] : ["**/node_modules/**", "tests/review/**"], environment: "node", setupFiles: ["tests/setup.ts"] },
} as Parameters<typeof defineConfig>[0]);
