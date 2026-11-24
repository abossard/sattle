import { createHash } from "node:crypto";
import { readdir, readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

async function routesFor(root) {
  const examples = resolve(root, "examples");
  const routes = new Map();
  for (const entry of await readdir(examples, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const declaration = resolve(examples, entry.name, "local-evidence.json");
    try {
      const manifest = JSON.parse(await readFile(declaration, "utf8"));
      for (const spec of Array.isArray(manifest) ? manifest : [manifest]) {
        const evidenceRoot = resolve(root, spec.root);
        const reader = spec.reader
          ? (await import(pathToFileURL(resolve(examples, entry.name, spec.reader)).href)).default
          : () => currentReport({ ...spec, root: evidenceRoot });
        routes.set(spec.route, () => reader(evidenceRoot));
      }
    } catch (error) { if (error.code !== "ENOENT") throw error; }
  }
  return routes;
}

async function currentReport(spec) {
  const read = async path => JSON.parse(await readFile(resolve(spec.root, path), "utf8"));
  const [report, inputs] = await Promise.all([read(spec.report), read(spec.inputs)]);
  const paths = Object.entries(inputs).flatMap(([base, names]) => names.map(name => base + "/" + name));
  if (!report.inputs || paths.length !== Object.keys(report.inputs).length) return null;
  const checked = await Promise.all(paths.map(async path => {
    let hash = null;
    try { hash = createHash("sha256").update(await readFile(resolve(spec.root, path))).digest("hex"); }
    catch (error) { if (error.code !== "ENOENT") throw error; }
    return Object.hasOwn(report.inputs, path) && hash === report.inputs[path];
  }));
  if (!checked.every(Boolean)) return null;
  delete report.inputs;
  return report;
}

function loopback(address) {
  return address === "::1" || address === "localhost" || address === "[::1]" ||
    /^(?:::ffff:)?127\.\d+\.\d+\.\d+$/.test(address ?? "");
}

export function localEvidence() {
  async function mount(server) {
    const routes = await routesFor(server.config.root);
    server.middlewares.use(async (req, res, next) => {
      const read = routes.get(req.url?.split("?")[0]);
      if (!read) return next();
      res.setHeader("Cache-Control", "no-store");
      res.setHeader("Content-Type", "application/json; charset=utf-8");
      res.setHeader("X-Content-Type-Options", "nosniff");
      const reply = (status, body) => { res.statusCode = status; res.end(JSON.stringify(body)); };
      // Local evidence is available only to a browser opened on loopback, even if Vite binds to the LAN.
      let host;
      try { host = new URL("http://" + req.headers.host).hostname; } catch { /* reject */ }
      if (!loopback(req.socket.remoteAddress) || !loopback(host) || req.headers["sec-fetch-site"] === "cross-site")
        return reply(403, { status: "local-only" });
      if (req.method !== "GET") return reply(405, { status: "read-only" });
      try {
        const report = await read();
        return report ? reply(200, report) : reply(409, { status: "stale" });
      } catch { return reply(404, { status: "unavailable" }); }
    });
  }
  return { name: "local-example-evidence", configureServer: mount, configurePreviewServer: mount };
}
