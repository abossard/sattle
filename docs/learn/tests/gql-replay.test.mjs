import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const dist = new URL("../../../brainstorm/dist/docs/learn/gql/", import.meta.url);
const read = (file, encoding) => readFile(new URL(file, dist), encoding);

test("the Pages replay projects the normalized capture with the lab's GQL", async () => {
  const { openReplay } = await import(new URL("replay.mjs", dist));
  const seed = JSON.parse(await read("replay.json", "utf8"));
  const query = await readFile(new URL("../../../lab/gql/projection.gql", import.meta.url), "utf8");
  assert.equal(seed.projection, query.trim().replace(/;$/, ""));
  const project = await openReplay(seed, await read("vendor/grafeo_wasm_bg.wasm"));

  function inspect(stop, state, values, evidence) {
    const { observations, assessments } = project(stop);
    const points = observations.map(row => row.observation).filter(row => row.row_type === "ResourceMetricRow");
    assert.deepEqual(points.map(point => point.numeric_value), values);
    assert.deepEqual(observations.map(row => row.relation), ["DESCRIBES_ENTITY", ...values.map(() => "MEASURES_ENTITY")]);
    assert.equal(assessments.length, 1);
    const [assessment] = assessments;
    assert.equal(assessment.state, state);
    assert.equal(assessment.subject_name, "shop-aks");
    assert.equal(assessment.resource_evidence.id, observations[0].observation.id);
    if (evidence == null) {
      assert.equal(assessment.point_evidence, null);
    } else {
      assert.equal(assessment.point_evidence.id, points.find(point => point.numeric_value === evidence).id);
    }
  }

  const [inventory, first, before, arrival, recovery, stale] = seed.checkpoints;
  inspect(inventory, "UNKNOWN_MISSING", [], null);
  inspect(first, "HEALTHY", [24], 24);
  inspect(before, "HEALTHY", [24], 24);
  inspect({ ns: BigInt(arrival.ns) - 1n }, "HEALTHY", [24], 24);
  inspect(arrival, "CRITICAL", [24, 97], 97);
  inspect(recovery, "HEALTHY", [24, 97, 42], 42);
  const expires = BigInt(Date.parse("2026-01-01T10:09:00Z")) * 1_000_000n;
  inspect({ ns: expires }, "HEALTHY", [24, 97, 42], 42);
  inspect({ ns: expires + 1n }, "UNKNOWN_STALE", [24, 97, 42], 42);
  inspect(stale, "UNKNOWN_STALE", [24, 97, 42], 42);
  inspect(first, "HEALTHY", [24], 24);
});
