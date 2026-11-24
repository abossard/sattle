import { execFile } from "node:child_process";
import { join } from "node:path";
import { promisify } from "node:util";

const run = promisify(execFile);

export default async function capturedKusto(root) {
  const fixture = process.env.MIRROR_FIXTURE || join(root, "fixture");
  const { stdout } = await run("python3", [
    "-B", join(root, "curate-flows.py"),
    "--fixture", fixture, "--template", join(root, "projects/ahm-model-demo/flows.json"),
    "--print-captured-kql", "both",
  ], { timeout: 5000 });
  return JSON.parse(stdout);
}
