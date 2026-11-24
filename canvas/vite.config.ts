import connections from "./connections.json" with { type: "json" };
import { localEvidence } from "./scripts/local-evidence.mjs";
import react from "@vitejs/plugin-react";
import { defineConfig, loadEnv, type Plugin } from "vite";

const runtime = (globalThis as unknown as { process: { env: Record<string, string | undefined>; cwd(): string } }).process;
const env = runtime.env;
const proxy = (prefix: string, port: string) => ({ target: `http://127.0.0.1:${port}`, changeOrigin: true, ws: true, rewrite: (path: string) => path.slice(prefix.length) });
const registry = "virtual:canvas-examples";
function exampleRegistry(enabled: boolean): Plugin {
  return {
    name: "canvas-example-registry",
    resolveId(id) { if (id === registry) return "\0" + registry; },
    load(id) {
      if (id !== "\0" + registry) return;
      if (!enabled) return "export const guidePacks = [], sourceProviders = [], connections = [];";
      return `
        export const guidePacks = Object.values(import.meta.glob("/examples/*/guide-pack.ts", { eager: true, import: "default" }));
        export const sourceProviders = Object.values(import.meta.glob("/examples/*/sources.ts", { eager: true, import: "default" })).flat();
        export { default as connections } from "/connections.json";
      `;
    },
  };
}

export default defineConfig(({ mode }) => {
  const enabled = loadEnv(mode, runtime.cwd(), "VITE_").VITE_CANVAS_EXAMPLES !== "off";
  const engine = Object.fromEntries([["/engine", proxy("/engine", env.ENGINE_PORT ?? "5080")],
    ...(enabled ? connections : []).map(c => [c.url, proxy(c.url, env[c.portEnv] ?? String(c.port))])]);
  return {
    base: "./",
    plugins: [react(), exampleRegistry(enabled), ...(enabled ? [localEvidence()] : [])],
    publicDir: enabled ? "public" : false,
    server: { fs: { allow: [".."] }, proxy: engine },
    preview: { proxy: engine },
    worker: { format: "es" as const },
    // React in its own chunk: it changes less often than the app, so a release does not invalidate it in the browser's cache. Fluent stays with the
    // code that uses it, so the components only the editor needs load with the editor.
    build: { target: "es2022", rolldownOptions: { output: { codeSplitting: { groups: [{ name: "react", test: /node_modules[\\/](react|react-dom|scheduler)[\\/]/ }] } } } },
    // four workers: the jsdom files are heavy, and with every core taken by other suites their axe runs and dialogs starve; the time limits are ceilings for a busy machine (another suite beside this one): a test that is done returns at once; what they wait for is a condition, not a time
    // two projects: "pure" needs no engine (`vitest run --project pure` runs without dotnet); "engine" is the folder tests/engine, whose global setup builds and serves a real engine. Its files share that one engine and commit to it, so they run one after the other
    test: {
      maxWorkers: 4, testTimeout: 30000, hookTimeout: 30000, environment: "node", setupFiles: ["tests/setup.ts"],
      projects: [
        { extends: true, test: { name: "pure", include: ["tests/**/*.test.{ts,tsx}"], exclude: ["tests/engine/**"] } },
        { extends: true, test: { name: "engine", include: ["tests/engine/**/*.test.{ts,tsx}"], globalSetup: ["tests/engine/global-setup.ts"], fileParallelism: false } },
      ],
    },
  };
});
