import * as bindings from "./vendor/grafeo_wasm_bg.mjs";

const master = `
MATCH (observation:Observation)-[link]->(subject:MasterEntity)
WHERE observation.captured_ns <= $as_of_ns AND observation.event_ns <= $as_of_ns
RETURN observation, type(link) AS relation, subject.display_name AS name
ORDER BY observation.event_ns, observation.ordinal`;

function properties(element) {
  return Object.fromEntries(Object.entries(element)
    .filter(([key]) => !key.startsWith("_"))
    .map(([key, value]) => [key, key.endsWith("_ns") ? BigInt(value) : value]));
}

export async function openReplay(seed, wasm) {
  // The upstream wrapper imports .wasm as a module, which browsers do not all support.
  const { instance } = await WebAssembly.instantiate(wasm, { "./grafeo_wasm_bg.js": bindings });
  bindings.__wbg_set_wasm(instance.exports);
  instance.exports.__wbindgen_start();

  const db = new bindings.Database();
  for (const statement of seed.ontology.split(";").map(text => text.trim()).filter(Boolean)) {
    db.execute(statement);
  }

  const ids = new Map(seed.nodes.map(node => {
    const [id] = db.batchCreateNodesWithProps(node._labels, [properties(node)]);
    return [node.id, id];
  }));
  db.batchCreateEdges(seed.edges.map(({ source, edge, target }) => ({
    src: ids.get(source), dst: ids.get(target), type: edge._type, properties: properties(edge),
  })));

  const policy = { ...seed.policy, stale_after_ns: BigInt(seed.policy.stale_after_ns) };
  return checkpoint => {
    const as_of_ns = BigInt(checkpoint.ns);
    const observations = db.executeWithParams(master, { as_of_ns });
    const assessments = db.executeWithParams(seed.projection, { ...policy, as_of_ns });
    return { observations, assessments };
  };
}
