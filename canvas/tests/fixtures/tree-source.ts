// The hand-made tree graph as a source of its own: one graph `tree` with the ontology marks of a hierarchy, a snapshot at revision 1 and one patch
// (revision 2) in which db degrades, so the timeline has something to move and the rows something to mark as changed.
import type { Snapshot } from "../../src/core/model";
import { NOT_AVAILABLE, type EvaluateResult, type GraphSource, type SpaceInfo, type StreamEvent } from "../../src/source/types";
import { RejectionBus } from "../../src/source/base";
import { curated, curatedSpec, treeGraph, treeHints, treeSpec } from "./tree-graph";
import { patch, setN, text } from "../helpers";

const ID = (v: string) => ({ value: v });
const ontology = {
  reference: { id: "tree.ont", version: "1.0.0" },
  entityTypes: [{ id: ID("Component"), properties: ["name", "own", "state", "severity"].map(ID) }, { id: ID("Evidence"), properties: [ID("name")] }],
  relationshipTypes: ["Contains", "EvidenceFor", "DependsOn"].map((t) => ({ id: ID(t), sourceTypes: [], targetTypes: [], properties: [] })),
  properties: [],
} as unknown as SpaceInfo["graphs"][number]["ontology"];

export class TreeSource implements GraphSource {
  readonly name = "Tree test";
  constructor(private readonly curatedData = false) {}
  private graph() { return this.curatedData ? curated(treeGraph()) : treeGraph(); }
  readonly bus = new RejectionBus();
  private info: SpaceInfo | null = null;
  async open(): Promise<SpaceInfo> {
    return (this.info ??= {
      id: "tree-space",
      graphs: [{ id: "tree", role: "projection", descriptor: { version: { tenant: ID("t"), graph: ID("tree"), revision: ID("1") }, projection: ID("p"), ontology: { id: "tree.ont", version: "1.0.0" }, virtualRegions: [] } as never, ontology, presentation: treeHints, tree: this.curatedData ? curatedSpec : treeSpec, inputs: [] }],
    });
  }
  async snapshot(): Promise<Snapshot> { return this.graph().toSnapshot(); }
  subscribe(_graph: string, sink: (e: StreamEvent) => void): () => void {
    const g = this.graph();
    const db = g.entity("db")!;
    const degraded = setN(db, { ...db, props: { ...db.props, own: text("Degraded"), state: text("Degraded"), severity: { kind: "integer", value: "1" } } });
    queueMicrotask(() => { sink({ type: "patch", patch: patch("1", "2", [degraded]) }); sink({ type: "end" }); });
    return () => {};
  }
  evaluate(): Promise<EvaluateResult> { return Promise.resolve(NOT_AVAILABLE); }
  onRejected(cb: Parameters<RejectionBus["on"]>[0]): () => void { return this.bus.on(cb); }
}
