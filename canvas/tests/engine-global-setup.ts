// The engine the engine-gated tests (kql-engine, kql-references) talk to: serve the estate fixture on a free port, hand its address to the test
// workers as ENGINE, stop it afterwards. The Release build is brought up to date first (a no-op when it is). `ENGINE=http://host:port` uses an engine that already runs instead.
// @ts-expect-error the project has no typings for node; the tests run in it
import { execFileSync, spawn } from "node:child_process";
// @ts-expect-error as above
import { fileURLToPath } from "node:url";

type Proc = { stdout: { on(e: "data", f: (b: { toString(): string }) => void): void }; stderr: { on(e: "data", f: (b: { toString(): string }) => void): void }; once(e: "exit", f: (code: number | null) => void): void; kill(): void };
const env = (globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env;
const engineDir = fileURLToPath(new URL("../../engine/", import.meta.url));
const cli = `${engineDir}src/Sattle.Cli`, dll = `${cli}/bin/Release/net11.0/sattle.dll`, fixture = `${engineDir}tests/Sattle.Tests/fixtures/estate`;

/** Serves the fixture; resolves with the address once the replay has finished, so a draft sees the whole space. */
const serve = (): Promise<{ url: string; stop: () => void }> => new Promise((resolve, reject) => {
  const child: Proc = spawn("dotnet", [dll, "serve", fixture, "--cluster", "synth", "--port", "0"], { cwd: engineDir, stdio: ["ignore", "pipe", "pipe"] });
  let out = "", url = "", settled = false;
  const stop = () => child.kill();
  // the first outcome counts: ready, a failed input, an exit or the time limit; a failure before ready stops the engine at once
  const settle = (f: () => void) => { if (settled) return; settled = true; clearTimeout(timer); f(); };
  const fail = (why: string) => settle(() => { stop(); reject(new Error(`${why}; its output:\n${out}`)); });
  const timer = setTimeout(() => fail("the engine did not finish its replay in 180 s"), 180_000);
  const read = (b: { toString(): string }) => {
    out += b.toString();
    if (out.includes("input failed")) return fail("the engine's input failed");
    url ||= /serving (http:\/\/\S+)/.exec(out)?.[1] ?? "";
    if (url && out.includes("replay finished")) settle(() => resolve({ url: url.replace("[::]", "127.0.0.1").replace("0.0.0.0", "127.0.0.1"), stop }));
  };
  child.stdout.on("data", read);
  child.stderr.on("data", read);
  child.once("exit", (code) => fail(`the engine exited with ${code} before it was ready`));
});

export default async function setup(): Promise<() => void> {
  if (env.ENGINE) return () => {};
  execFileSync("dotnet", ["build", "-c", "Release", cli, "--nologo", "-v", "q"], { stdio: "inherit", timeout: 600_000 });
  const { url, stop } = await serve();
  env.ENGINE = url;
  return stop;
}
