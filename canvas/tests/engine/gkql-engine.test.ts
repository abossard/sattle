// @ts-expect-error the Canvas test project has no Node typings
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
// @ts-expect-error as above
import { resolve, join } from "node:path";
// @ts-expect-error as above
import { tmpdir } from "node:os";
// @ts-expect-error as above
import { fileURLToPath } from "node:url";
import { expect, it } from "vitest";
import { printGkql, readGkql } from "../../src/editor/gkql";
import { EditorService } from "../../src/editor/service";
import { EditorSession } from "../../src/editor/session";
import { newProjection } from "../../src/editor/structures";
import { serveEngine } from "./global-setup";

const engineDir = fileURLToPath(new URL("../../../engine/", import.meta.url));
const file = "tutorial.inventory.gkql.json";
const graph = "inventory";

it("checks, previews, commits and reloads an edited GKQL projection after restart", async () => {
  const directory = await mkdtemp(join(tmpdir(), "sattle-gkql-editor-"));
  const storage = ["--store", `sqlite=${join(directory, "journal.sqlite")}`];
  const setup = ["--defs", resolve(engineDir, "tutorial"), "--space", "azureTutorial", ...storage];
  const capture = join(directory, "capture");
  const subscriptionId = "00000000-0000-0000-0000-000000000001";
  const resourceGroup = "test-group";
  const group = `/subscriptions/${subscriptionId}/resourcegroups/${resourceGroup}`;
  const resources = [
    { id: group, name: resourceGroup, type: "microsoft.resources/resourcegroups" },
    { id: `${group}/providers/microsoft.containerservice/managedclusters/test-cluster`, name: "test-cluster", type: "microsoft.containerservice/managedclusters" },
  ].map((resource, seq) => JSON.stringify({
    v: 1, source: "arg", capturedAt: "2026-01-01T10:00:00Z", seq, key: resource.id,
    meta: { query: "resources" }, payload: { ...resource, subscriptionId, resourceGroup },
  }));
  let host: Awaited<ReturnType<typeof serveEngine>> | undefined;
  try {
    await mkdir(join(capture, "arg"), { recursive: true });
    await writeFile(join(capture, "arg", "resources.ndjson"), resources.join("\n") + "\n");
    host = await serveEngine([capture, "--cluster", "synth", "--lateness", "0s", ...setup]);
    let service = new EditorService(host.url);
    const starter = await EditorSession.open(service);
    const name = "newInventory";
    const created = newProjection(starter.graph, {
      language: "gkql", module: "tutorial", name, graphName: name, description: "", outputModule: "d:tutorial", params: [],
      inputs: [{ port: "master", graph: "master", ontology: "d:tutorial" }],
    }, starter.texts.tutorial);
    if (!created.ok || !created.value.module) throw new Error(created.ok ? "missing module binding" : created.reason);
    const native = { file: { ...created.value.file, text: created.value.text }, module: created.value.module, edits: [], label: "Create inventory" };
    const reverted = await starter.change(() => ({
      ...native, module: { ...native.module, text: native.module.text.replace('"master": "master"', '"master": "unknownGraph"') },
    }));
    expect(reverted?.ok).toBe(false);
    expect(starter.texts[native.file.file]).toBeUndefined();
    expect(starter.ok).toBe(true);
    expect((await starter.change(() => native))?.ok).toBe(true);
    expect(starter.fileOf(`tutorial.${name}`)?.language).toBe("gkql");
    const blank = await starter.preview({ graphs: [name], include: ["counts"] });
    expect(blank.graphs[0]?.counts.entities).toBe("0");
    await starter.discard();

    const session = await EditorSession.open(service);
    expect(session.fileOf("tutorial.inventory")).toEqual({ projection: "tutorial.inventory", file, language: "gkql" });
    const program = readGkql(session.texts[file]);
    const initial = await session.preview({ graphs: [graph], include: ["counts", "snapshots"] });
    expect(Number(initial.graphs[0]?.counts.entities)).toBeGreaterThan(0);

    const rewrite = (condition: unknown) => printGkql({
      ...program,
      verbs: program.verbs.map((verb) => verb.id === "onlyAks" ? { ...verb, condition } : verb),
    });
    const invalid = await session.storeText(file, rewrite({ fn: "unknown", args: [] }));
    expect(invalid.ok && !invalid.applied.ok).toBe(true);
    expect(session.diagnostics.some((diagnostic) => diagnostic.file === file)).toBe(true);
    await expect(service.commit(session.draft.draft, session.draft.baseRevision)).rejects.toThrow();

    const text = rewrite({ fn: "==", args: [{ ref: "r.resourceType" }, { lit: "microsoft.invalid" }] });
    const stored = await session.storeText(file, text);
    expect(stored.ok && stored.applied.ok).toBe(true);
    const changed = await session.preview({ graphs: [graph], include: ["counts", "diff", "snapshots"] });
    expect(changed.graphs[0]?.counts.entities).toBe("0");
    expect(changed.graphs[0]?.diff?.edits.some((edit) => edit.kind === "entity" && !edit.after)).toBe(true);
    const committed = await service.commit(session.draft.draft, session.draft.baseRevision);
    expect(committed.catalogRevision).not.toBe(session.draft.baseRevision);
    await host.stop();

    host = await serveEngine(setup, false);
    service = new EditorService(host.url);
    const reopened = await EditorSession.open(service);
    expect(reopened.texts[file]).toBe(text);
    const after = await reopened.preview({ graphs: [graph], include: ["counts"] });
    expect(after.graphs[0]?.counts.entities).toBe("0");
    await reopened.discard();
  } finally {
    await host?.stop();
    await rm(directory, { recursive: true, force: true });
  }
}, 120_000);
