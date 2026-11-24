// The Inspector edits a real draft. Replaying the written definitions with the CLI must give the published graph.
import { expect, it } from "vitest";
// @ts-expect-error Node test utilities, as in global-setup.ts
import { mkdtemp, readFile, rm } from "node:fs/promises";
// @ts-expect-error Node test utility
import { execFileSync } from "node:child_process";
// @ts-expect-error Node test utility
import { join } from "node:path";
// @ts-expect-error Node test utility
import { tmpdir } from "node:os";
// @ts-expect-error Node test utility
import { fileURLToPath } from "node:url";
import { EditorService } from "../../src/editor/service";
import { field, proposedRule, previewRule } from "../../src/editor/coverage-review";
import { parseSnapshot } from "../../src/source/snapshot-file";
import { serveEngine } from "./global-setup";

it("reviews the captured ingress, publishes it, and agrees with CLI replay while the prior conflict remains readable", async () => {
  const root = fileURLToPath(new URL("../../../engine/", import.meta.url));
  const tmp = await mkdtemp(join(tmpdir(), "sattle-coverage-"));
  const defs = join(tmp, "definitions"), capture = join(tmp, "capture");
  let host: Awaited<ReturnType<typeof serveEngine>> | undefined;
  try {
    execFileSync("python3", [join(root, "../scripts/review-flow.py"), "prepare", tmp]);
    host = await serveEngine([capture, "--defs", defs, "--space", "flowReview", "--write-definitions"]);
    const service = new EditorService(host.url);
    const parse = (raw: unknown) => parseSnapshot(raw, "coverage", r => { throw new Error(r.errors.join(", ")); })!.snapshot;
    const snapshot = async (graph: string, revision?: string) => {
      const response = await fetch(host!.url + "/graphs/" + graph + "/snapshot" + (revision ? "?revision=" + revision : ""));
      expect(response.ok).toBe(true);
      return parse(await response.json());
    };
    const before = await snapshot("coverage"), conflict = (await snapshot("conflicts")).entities[0];
    expect(field(conflict, "status")).toBe("conflict");
    expect(field(conflict, "observedTarget")).toBe("web");
    const rule = { ...proposedRule(conflict), target: "web" };
    const space = await (await fetch(host.url + "/space")).json();
    const coverage = space.graphs.find((g: { id: string }) => g.id === "coverage");
    const rules = space.graphs.find((g: { id: string }) => g.id === coverage.inputPorts.rules);
    const comparison = await previewRule(service, coverage.id, rules.projectionFile, conflict, rule);
    expect(comparison.changes).toContainEqual(expect.objectContaining({ id: conflict.id, from: "conflict", to: "mapped" }));
    expect((await snapshot("coverage")).version).toEqual(before.version);
    await service.commit(comparison.draft.draft, comparison.draft.baseRevision);

    const after = await snapshot("coverage");
    const corrected = after.entities.find(e => e.id === conflict.id)!;
    expect(field(corrected, "status")).toBe("mapped");
    expect(field(corrected, "revision")).toBe("2");
    expect(field(corrected, "sourceRef")).toBe(field(conflict, "sourceRef"));
    expect((await snapshot("conflicts")).entities).toHaveLength(0);
    expect((await snapshot("coverage", before.version.revision)).entities).toEqual(before.entities);

    const out = join(tmp, "replay");
    execFileSync("dotnet", [join(root, "src/Sattle.Cli/bin/Release/net11.0/sattle.dll"), "replay", capture,
      "--defs", defs, "--space", "flowReview", "--out", out], { cwd: root });
    const replayed = parse(JSON.parse(await readFile(join(out, "coverage.final.snapshot.json"), "utf8")));
    const sorted = <T extends { id: string }>(xs: T[]) => [...xs].sort((a, b) => a.id.localeCompare(b.id));
    expect(sorted(replayed.entities)).toEqual(sorted(after.entities));
    expect(sorted(replayed.relationships)).toEqual(sorted(after.relationships));
  } finally {
    await host?.stop();
    await rm(tmp, { recursive: true, force: true });
  }
}, 90_000);
