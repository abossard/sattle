// TypeScript mirrors of the wire shapes in brainstorm/contracts/schemas/*.json.
// Definitions (projection, ontology, descriptor) are used in wire shape everywhere.
// Hot data (entities, relationships, patches) is converted to the flat model in model.ts.

export interface Id { value: string }
export type GraphValue =
  | { kind: "text"; value: string }
  | { kind: "integer"; value: string }
  | { kind: "decimal"; value: string }
  | { kind: "boolean"; value: boolean }
  | { kind: "instant"; value: string }
  | { kind: "null" }
  | { kind: "entity"; value: Id }
  | { kind: "list"; items: GraphValue[] };

export interface PropertyValue { property: Id; value: GraphValue }
export interface EntityState { id: Id; type: Id; properties: PropertyValue[] }
export interface RelationshipState { id: Id; source: Id; target: Id; type: Id; properties: PropertyValue[] }
export interface GraphVersionWire { tenant: Id; graph: Id; revision: Id }
export interface OntologyRef { id: string; version: string; digest: Id }

export type GraphEditWire =
  | { kind: "entity"; id: Id; before: EntityState | null; after: EntityState | null }
  | { kind: "relationship"; id: Id; before: RelationshipState | null; after: RelationshipState | null }
  | { kind: "virtualRegion"; id: string; before: unknown; after: unknown }
  | { kind: "ontology"; before: OntologyRef; after: OntologyRef };

export interface GraphPatchWire {
  schemaVersion: "1.0";
  from: GraphVersionWire;
  to: GraphVersionWire;
  projection: Id;
  profile: "extensionalFinite" | "symbolic";
  edits: GraphEditWire[];
  cause: { inputs: Id[]; configurationVersion: string };
}

export interface GraphDescriptorWire {
  version: GraphVersionWire;
  projection: Id;
  ontology: OntologyRef;
  virtualRegions: unknown[];
}

export interface GraphExtentWire { entities: EntityState[]; relationships: RelationshipState[] }

export type ValueKind = "text" | "integer" | "decimal" | "boolean" | "instant" | "entity" | "list";
export interface PropertyDefinition {
  id: Id; valueKind: ValueKind; nullable: boolean; itemKind: ValueKind | null; unit: string | null; role: "graphData" | "telemetry";
}
export interface OntologyDefinition {
  schemaVersion: "1.0";
  reference: OntologyRef;
  noEntityType: Id;
  noRelationshipType: Id;
  properties: PropertyDefinition[];
  entityTypes: { id: Id; properties: Id[] }[];
  relationshipTypes: { id: Id; sourceTypes: Id[]; targetTypes: Id[]; properties: Id[] }[];
}

// ---- projection definition ----
export type FieldType = "text" | "integer" | "decimal" | "boolean" | "instant" | "entity" | "relationship";
export interface Field { name: string; type: FieldType; optional: boolean }
export interface RowType { fields: Field[] }
export interface Literal { kind: "literal"; value: GraphValue }
export interface FieldExpression { kind: "field"; field: string }
export type CallFunction =
  | "equal" | "notEqual" | "and" | "or" | "not" | "add" | "subtract" | "multiply" | "divide"
  | "lessThan" | "greaterThan" | "concat" | "coalesce" | "lowerInvariant";
export interface CallExpression { kind: "call"; function: CallFunction; arguments: Expression[] }
export type Expression = Literal | FieldExpression | CallExpression;
export interface Assignment { field: string; value: Expression }
export interface PropertyAssignment { property: Id; value: Expression }
export interface NodePattern { variable: string; type: Id }
export interface EdgePattern { variable: string; source: string; target: string; type: Id }
export interface GraphPattern { nodes: NodePattern[]; edges: EdgePattern[] }
export type ConflictPolicy = "requireEqual" | "preferFirstPort" | "preferLastPort" | "preserveEvidence";
export interface AggregateField {
  name: string; function: "countDistinct" | "sum" | "min" | "max" | "any" | "all"; value: Expression;
}
export interface IdentityExpression { domain: Id; canonicalizationVersion: string; components: Assignment[] }

interface OpBase { id: Id }
export type Operator =
  | (OpBase & { kind: "input"; binding: string; row: RowType })
  | (OpBase & { kind: "match"; binding: string; pattern: GraphPattern })
  | (OpBase & { kind: "filter"; predicate: Expression })
  | (OpBase & { kind: "map"; fields: Assignment[] })
  | (OpBase & { kind: "join"; on: Expression })
  | (OpBase & { kind: "union"; conflict: ConflictPolicy })
  | (OpBase & { kind: "distinct" })
  | (OpBase & { kind: "group"; keys: Assignment[]; aggregates: AggregateField[] })
  | (OpBase & { kind: "traverse"; relationship: Id; direction: "outgoing" | "incoming" | "either"; minimumDepth: number; maximumDepth: number })
  | (OpBase & { kind: "emitEntity"; identity: IdentityExpression; type: Id; properties: PropertyAssignment[] })
  | (OpBase & {
      kind: "emitRelationship"; identityRule: string; source: Expression; target: Expression; type: Id;
      properties: PropertyAssignment[]; retypeCollision: ConflictPolicy;
    })
  | (OpBase & {
      kind: "fixpoint"; relation: string; row: RowType; body: Operator[]; wires: Wire[]; result: Id;
      finiteDomainBinding: string; maximumFacts: number; maximumIterations: number;
    })
  | (OpBase & { kind: "window"; timestampField: string; widthNanoseconds: number; latePolicy: "ignore" | "revise" | "reject" });
export type OperatorKind = Operator["kind"];

export interface Wire { from: Id; output: string; to: Id; input: string }
export interface RuleProgram { operators: Operator[]; wires: Wire[]; outputs: Id[] }
export interface BidirectionalRule {
  id: string; sourcePattern: GraphPattern; targetPattern: GraphPattern;
  correspondences: { name: string; sourceVariables: string[]; targetVariables: string[]; complement: Assignment[] }[];
  forward: RuleProgram; backward: RuleProgram; requiredLaws: ("getPut" | "putGet")[];
}
export interface ProjectionDefinition {
  schemaVersion: "1.0";
  id: Id;
  version: string;
  ontology: OntologyRef;
  inputs: { name: string; contract: string; version: string }[];
  scaffold: GraphExtentWire;
  operators: Operator[];
  wires: Wire[];
  outputs: Id[];
  retainedInputsPerElement: number;
  bidirectionalRules: BidirectionalRule[];
}

// ---- graph space ----
export interface GraphSpaceDefinition {
  schemaVersion: "1.0";
  id: string;
  master: { graph: Id; ingestionProjection: ProjectionDefinition };
  projections: {
    outputGraph: Id;
    projection: ProjectionDefinition;
    graphInputs: { port: string; source: { kind: "master" } | { kind: "projection"; projection: Id }; expectedOntology: OntologyRef }[];
  }[];
}
