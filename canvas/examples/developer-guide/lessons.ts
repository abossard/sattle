import type { Lesson, Step } from "../../src/tutorial/lessons";
import { kilnLessons } from "./kiln-lessons";

const master: Step = ["Master graph", "master"];
const inventory: Step = ["Inventory", "projection"];
const signals: Step = ["Signals", "projection"];
const tree: Step = ["Tree", "projection"];
const health: Step = ["Health", "projection"];
const workloadHealth: Step = ["Workload health", "projection"];
const report: Step = ["Document", "document"];
const workloads: Step = ["Workloads", "projection"];
const placement: Step = ["Scale-set placement", "projection"];
const colocation: Step = ["SharesVmssWith", "projection"];
const source: Step = ["Azure export", "input"];
const metrics: Step = ["Azure metrics", "input"];
const kubernetes: Step = ["Kubernetes", "input"];
const flows: Step = ["Observed calls", "input"];

const chapters: readonly Lesson[] = [
  {
    id: "core-import", space: "healthMirror", module: "mirror", title: "Open the CORE deployment", goal: "Find the Azure resources behind Run a Task.", action: "Select Run a Task, then follow its links in Inspector.",
    check: "Follow a component’s signal binding to its latest reading.",
    view: "data", graph: "master", projection: "ingest",
    flow: [[["Azure model", "input"], ["Azure Monitor", "input"]], [["C# adapters", "rule"]], [master]],
    files: ["mirror.ingest.gkql.json"],
    commands: [{ label: "Open the local replay", code: "./scripts/health-mirror.sh prepare\n./scripts/health-mirror.sh serve" }, { label: "Capture another model", code: './scripts/health-mirror.sh capture "$MODEL_ID"' }],
    prompt: "In master, trace Run a Task to its web component. Which ID came from Azure? Which links did the ingest rule emit?",
  },
  {
    id: "core-dependencies", space: "healthMirror", module: "mirror", title: "Trace a task through CORE", goal: "See what Run a Task depends on.", action: "Select Run a Task, then choose Inspect storage dependency to read the link.",
    check: "Read the impact on the link. A shared Azure resource can serve distinct roles in the model.",
    view: "data", graph: "structure", projection: "structure",
    flow: [[["CORE", "master"]], [["Run a Task", "projection"]], [["Session and Task storage", "identity"], ["Assistant execution", "identity"]]],
    files: ["mirror.structure.gkql.json"],
    prompt: "Starting at Run a Task, show the declared dependencies in structure. Find the web app’s Tasks and Apply roles in master. Explain why their model entity IDs must stay distinct.",
  },
  {
    id: "core-signals", space: "healthMirror", module: "mirror", title: "Read a configured health signal", goal: "Trace a signal’s health back to the reading Azure supplied.", action: "Pick a signal binding. Read the source, value and UTC window. A Log Analytics binding also shows its Kusto query.",
    check: "The binding identifies the entity. Its definition supplies the threshold. Its observation supplies the measurement.",
    view: "data", graph: "signals", projection: "signals",
    flow: [[["Blob metric", "input"]], [["Bound definition", "identity"], ["Threshold", "rule"]], [["Evaluated signal", "projection"]]],
    files: ["mirror.signals.gkql.json"],
    prompt: "Use the selected storage signal. Show me the configured resource, the metric namespace, the observed value and the threshold that evaluated it. Check how the rule handles a missing reading.",
  },
  {
    id: "core-health", space: "healthMirror", module: "mirror", title: "Roll signals up to CORE", goal: "Explain how a storage reading affects Run a Task.", action: "Select Session and Task storage. Follow its parent and inspect the link’s impact.",
    check: "Compare Sattle with the Azure checkpoint. The verifier lists each differing state or value.",
    view: "data", graph: "health", projection: "health",
    flow: [[["Signals", "projection"], ["Structure", "projection"]], [["Child state + impact", "rule"]], [["Run a Task", "projection"]], [["CORE", "projection"]]],
    files: ["mirror.health.gkql.json"],
    commands: [{ label: "Compare every checkpoint", code: "./scripts/health-mirror.sh verify all" }, { label: "Compare the settled checkpoints", code: "./scripts/health-mirror.sh verify steady" }],
    prompt: "Trace one storage reading to Run a Task and CORE. Read the link impact and ignoreUnknown setting. Check how link impact and ignoreUnknown affect the parent when a child is Unknown. Verify the rule in a draft.",
  },
  {
    id: "core-network", space: "healthMirror", module: "mirror", title: "Follow observed traffic", goal: "Find a call recorded by Application Insights.", action: "Inspect calls to Blob Storage. Follow the host to the storage role in CORE.",
    check: "CalledEndpoint counts requests recorded by Application Insights. EndpointOf matches the hostname to the Azure resource.",
    view: "data", graph: "network", projection: "network",
    flow: [[["Network diagnostics", "input"]], [master], [["Observed connections", "projection"]]],
    files: ["mirror.network.gkql.json"],
    commands: [{ label: "Listen on Event Hubs", code: './scripts/health-mirror.sh listen "$EH_NAMESPACE" "$EH_HUB"' }, { label: "Publish from another terminal", code: './scripts/health-mirror.sh publish "$EH_NAMESPACE" "$EH_HUB"' }],
    prompt: "Show the source of an observed connection. What does that source tell us about the endpoints? Compare it with the model's declared dependency before drawing a conclusion.",
  },
  {
    id: "core-discovery", space: "healthMirror", module: "mirror", title: "Discover every Container App", goal: "Find which apps run in the same Azure environment.", action: "Select an app. Follow its environment link and inspect its ARM resource ID.",
    check: "On the next inventory refresh, a new app appears under the environment Azure declares.",
    view: "data", graph: "containerApps", projection: "containerApps",
    flow: [[["Azure inventory", "input"]], [["Full ARM ID", "identity"], ["Environment ID", "identity"]], [["Container Apps", "projection"]]],
    files: ["mirror.containerApps.gkql.json"],
    commands: [{ label: "Refresh this resource group", code: './scripts/health-mirror.sh inventory "$RESOURCE_GROUP_ID"' },
      { label: "Keep the running graph current", code: './scripts/health-mirror.sh watch "$RESOURCE_GROUP_ID" "$EH_NAMESPACE" "$EH_HUB"' },
      { label: "Verify deployed apps and flow links", code: './scripts/health-mirror.sh verify-rules' }],
    prompt: "Find the ARM IDs of the app and its environment. How does GKQL join them? Check whether both deployed apps have a declared environment.",
  },
  {
    id: "core-portable-flows", space: "healthMirror", module: "mirror", title: "Model user flows in the ontology", goal: "Find the deployed apps that match each step’s declared requirements.", action: "Pick a flow. Select a step or its app to inspect it in the graph.",
    check: "The ontology names the journey. GKQL links steps to matching apps. A class match alone leaves the caller and receiver unproven.",
    view: "data", graph: "workloadFlows", projection: "workloadFlows",
    flow: [[["User flow", "identity"]], [["Declared step", "identity"]], [["Required app class", "rule"], ["Classified apps", "projection"]], [["Matching deployed apps", "projection"]]],
    files: ["mirror.workloadFlows.gkql.json"],
    commands: [{ label: "Apply the project ontology to local data", code: "./scripts/health-mirror.sh curate-flows --offline\n./scripts/health-mirror.sh prepare-project" }, { label: "Compare flows with the Azure model held out", code: "./scripts/health-mirror.sh compare-discovery" }],
    prompt: "Trace Run a Task from flows.json to each deployed app. Which links express project intent? Which facts came from Azure? Does the captured telemetry show that the whole task finished?",
  },
  {
    id: "core-app-telemetry", space: "healthMirror", module: "mirror", title: "Read Azure metrics and Kusto results", goal: "Find what Azure measured while an app was serving requests.", action: "Select a metric to see its UTC window. Open a step’s operation to see what Kusto counted.",
    check: "The Event Hub receipt identifies the app and metric window. Kusto counted matching operations across the window. An individual request needs its own trace.",
    view: "data", graph: "appTelemetry", projection: "appTelemetry",
    flow: [[["Azure Monitor metric", "input"], ["Kusto result", "input"]], [["Deployed app", "identity"], ["Measured operation", "identity"]], [["Step evidence", "projection"]]],
    files: ["mirror.appTelemetry.gkql.json"],
    commands: [{ label: "Trace a metric to its Event Hub receipt", code: "./scripts/health-mirror.sh verify-diagnostics" }, { label: "Listen on the captured metric hub", code: "./scripts/health-mirror.sh listen-diagnostics" }, { label: "Refresh Kusto results", code: "./scripts/health-mirror.sh curate-flows --hours 72\n./scripts/health-mirror.sh prepare-project" }],
    prompt: "Read one metric and one operation from this app. Compare their UTC windows, units and identities. Which part of the flow does each reading cover?",
  },
  {
    id: "core-project-health", space: "healthMirror", module: "mirror", source: "demo-project", title: "Set a health rule in Sattle", goal: "See how an app’s memory reading affects its steps and flows.", action: "Inspect a memory reading, then open the projection editor and change memoryWarningPercent in a draft.",
    check: "The projection rolls measured and missing evidence into operational health. Each flow also records whether completion was observed.",
    view: "data", graph: "projectHealth", projection: "projectHealth",
    flow: [[["Diagnostic metric", "input"], ["Observed request", "input"]], [["App and step", "projection"]], [["Flow", "projection"]], [["Resource group", "projection"]]],
    files: ["mirror.projectHealth.gkql.json"],
    commands: [{ label: "Build from deployment evidence alone", code: "./scripts/health-mirror.sh prepare-project" }],
    prompt: "Trace a memory metric through a step and flow. Predict what changes when you lower the warning threshold below its measured value. Which flow outcomes are still unobserved?",
  },
  {
    id: "core-ontology-gaps", space: "healthMirror", module: "mirror", title: "Find apps the ontology missed", goal: "See which app names need a rule or match conflicting classes.", action: "Inspect the two app names and the rules that classified them.",
    check: "Only Unmapped and Ambiguous apps appear in the gaps graph.",
    view: "data", graph: "ontologyGaps", projection: "ontologyGaps",
    flow: [[["Container App names", "input"], ["Naming rules", "identity"]], [["Matched classes", "projection"]], [["Unmapped", "rule"], ["Ambiguous", "rule"]]],
    files: ["mirror.ontologyGaps.gkql.json"],
    commands: [{ label: "Preview rule gaps on the two deployed apps", code: "./scripts/health-mirror.sh verify-rules --draft" }],
    prompt: "Which rule classifies each app? Compare a name ending in -web with one ending in -webhook. Show why a naming match alone cannot prove that the app serves a user flow.",
  },
  {
    id: "trace-submission", space: "flowReview", source: "flow-review", module: "sattle.flow", title: "Follow one submission", goal: "Find which calls belong to one request before writing any ontology rules.",
    action: "Select a handoff from the local sample. Inspect the parent and child for submit-42.",
    check: "web.submit called queue.enqueue and audit.write. Both children name the same parent. A separate report counts four web-to-queue calls.",
    view: "data", graph: "flows", projection: "discover",
    flow: [[["Three spans", "input"]], [["Parent references", "rule"]], [["Two handoffs", "projection"]]],
    files: ["sattle.flow.discover.gkql.json"],
    commands: [{ label: "Start the local submission replay", code: "python3 scripts/review-flow.py serve" }],
    prompt: "Use submit-42 to explain the two handoffs. Cite the parent and child references and distinguish them from the counted pattern.",
  },
  {
    id: "inspect-coverage", space: "flowReview", source: "flow-review", module: "sattle.coverage", title: "Find what the rules missed", goal: "See which observations lack a match and which declarations lack evidence.",
    action: "Inspect the receiver scope, then compare the observed receiver with the declared downstream participant.",
    check: "One sampled request has a receiver receipt. The worker’s downstream role stays unverified until a linked call arrives.",
    view: "data", graph: "coverage", projection: "coverage",
    flow: [[["Ingress receipt", "input"], ["Project rules", "rule"]], [["Coverage", "projection"]], [["Observed item", "identity"], ["Declared role", "identity"]]],
    files: ["sattle.coverage.coverage.gkql.json"],
    commands: [{ label: "Replay with an empty ontology", code: "python3 scripts/review-flow.py empty" }],
    prompt: "For submit-42, show me the observed receiver, the receiver named by the rule, and the downstream declaration. Which has a receipt? How does sampling limit the claim?",
  },
  {
    id: "review-receiver", space: "flowReview", source: "flow-review", module: "sattle.coverage", title: "Correct the receiver", goal: "Make the submission rule agree with the ingress receipt.",
    action: "Inspect the receiver conflict. Choose Review rule, set Target to web and choose Preview change.",
    check: "Preview shows conflict → mapped. Publish rule applies the change. Discard draft keeps the original. Either way, the downstream worker call still needs evidence.",
    view: "data", graph: "conflicts", projection: "rules",
    flow: [[["Ingress receipt", "input"], ["Receiver rule", "rule"]], [["Conflict", "projection"]], [["Draft comparison", "projection"]]],
    commands: [{ label: "Paste into Use an assistant suggestion", code: '{"role":"receiver","target":"web","sourceRef":"trace/42/ingress"}' }],
    prompt: "Read the receiver finding for submit-42. Suggest a role and target as JSON, and copy the exact sourceRef from the evidence. I will review and publish the change.",
  },
  ...kilnLessons,
  {
    id: "start", title: "Start the engine and master graph", goal: "Replay captured Azure and Kubernetes data into one graph.", action: "Run the engine and Canvas in separate terminals.",
    check: "The master graph shows the captured objects and their relationships.",
    view: "data", graph: "master", flow: [[source, metrics, kubernetes, flows], [master]],
    commands: [
      { label: "Prepare and serve", code: "./scripts/tutorial.sh prepare\n./scripts/tutorial.sh serve" },
      { label: "Start Canvas", code: "npm --prefix canvas install\nnpm --prefix canvas run dev" },
    ],
    prompt: "Read scripts/tutorial.sh. Help me prepare my capture and start the engine, then Canvas. Ask for the output after each command.",
  },
  {
    id: "adapter", title: "Build an adapter", goal: "Read source data into typed rows that GKQL can use.", action: "Open Adapters.cs and find the Azure resource ID in the emitted row.",
    check: "In master, a Resource node has the same armId as its scrubbed Azure export row.",
    view: "editor", graph: "master", projection: "ingest", flow: [[source], [["C# adapter", "input"]], [["GKQL ingest", "rule"]], [master]],
    files: ["Adapters.cs", "tutorial.module.gkql.json", "tutorial.ingest.gkql.json"],
    commands: [{ label: "Rebuild and check the adapter", code: "./scripts/tutorial.sh verify" }],
    prompt: "Use AzureResourceGraphAdapter as a template. Trace one ID from Azure JSON through the C# row contract, registration, and GKQL ingest mapping. Give me one small mapping edit and a command to verify it.",
  },
  {
    id: "explore", title: "Explore the master graph", goal: "Follow a relationship between two captured objects.", action: "Click Private link and read both endpoints in Inspector.",
    check: "Each endpoint is a node. The edge type tells you what connects them.",
    view: "data", graph: "master", flow: [[source, kubernetes, flows], [master], [["Resource nodes", "master"], ["Network links", "identity"]]],
    prompt: "Help me trace a private endpoint to its service and network interface. Ask for the scrubbed relationship types and endpoints, then follow the recorded IP and DNS links.",
  },
  {
    id: "first-projection", title: "Map Kubernetes workloads", goal: "Find which Deployments and Services call each other.", action: "Write a projection whose rules turn captured pod calls into workload calls.",
    check: "Look for DeploymentCalls and ServiceCalls in the draft. Trace a matching example to its source edges.",
    view: "editor", graph: "workloads", projection: "workloads", flow: [[master], [["Pod calls", "identity"], ["Pod owners", "rule"], ["EndpointSlices", "rule"]], [workloads]],
    files: ["tutorial.workloads.gkql.json"],
    build: { name: "firstWorkloads", inputs: ["master"], source: "tutorial.workloads.gkql.json" },
    example: { type: "ServiceCalls", label: "Trace a real service call", explanation: "EndpointSlices link the calling and receiving pods to their Services. Inspector shows that evidence under Derived from." },
    prompt: "Read tutorial.workloads.gkql.json. Trace a captured pod call through pod owners to DeploymentCalls and through EndpointSlice membership to ServiceCalls. Ask me to find that evidence in Inspector.",
  },
  {
    id: "second-projection", title: "Connect Deployments to Azure scale sets", goal: "Trace a Deployment to its Azure virtual machine scale set.", action: "Match the Kubernetes node in workloads to its Azure host in master.",
    check: "RunsOnVmss links Deployments to scale sets. Its evidence includes ScheduledOn and HostedByAzure.",
    view: "editor", graph: "placement", projection: "placement", flow: [[workloads, master], [["ScheduledOn", "rule"], ["Node → Azure host", "identity"]], [placement]],
    files: ["tutorial.placement.gkql.json"],
    build: { name: "firstPlacement", inputs: ["workloads", "master"], source: "tutorial.placement.gkql.json" },
    example: { type: "RunsOnVmss", label: "Trace a real scale-set link", explanation: "The node shared by the scheduling and hosting records joins the two inputs." },
    prompt: "Read tutorial.placement.gkql.json. Start at a RunsOnVmss edge. Trace it through ScheduledOn to a Kubernetes node and then to the Azure scale set. Ask me to verify the links in Inspector.",
  },
  {
    id: "branch", title: "Derive another connection", goal: "Find which Deployments share an Azure scale set.", action: "Read placement and link Deployments that run on the same scale set.",
    check: "Trace a SharesVmssWith edge to two RunsOnVmss edges that end at one scale set.",
    view: "editor", graph: "colocation", projection: "colocation", flowLabel: "Workload projection chain", flow: [[master], [workloads, master], [placement], [colocation]],
    companionFlow: { label: "Other master branches", levels: [[master], [inventory, signals]] },
    files: ["tutorial.colocation.gkql.json"],
    build: { name: "firstColocation", inputs: ["placement"], source: "tutorial.colocation.gkql.json" },
    example: { type: "SharesVmssWith", label: "Trace shared placement", explanation: "Two RunsOnVmss edges to the same scale set produce one directed SharesVmssWith edge." },
    prompt: "Read tutorial.colocation.gkql.json. Follow a SharesVmssWith edge back to both placement edges and their Azure scale set. Ask me which projections update if a Deployment moves.",
  },
  {
    id: "document", title: "Generate a document", goal: "Turn graph data into a Markdown report.", action: "Switch between Document and Graph in the report.",
    check: "A GKQL block appears as a node in Graph and as a row or heading in Document.",
    view: "data", graph: "report", projection: "report", flow: [[workloadHealth, health], [["GKQL blocks", "rule"]], [["Markdown renderer", "rule"]], [report]],
    files: ["tutorial.report.gkql.json", "sattle.doc.module.gkql.json"],
    commands: [{ label: "Fetch Markdown", code: "curl -fsS http://127.0.0.1:5080/graphs/report/document" }],
    prompt: "Read tutorial.report.gkql.json and the sattle.doc ontology. Help me edit one block and check the resulting Markdown at the document endpoint.",
  },
  {
    id: "tree", title: "Make a tree from a graph", goal: "Read resource containment as parents and children.", action: "Expand a Group in Tree, then inspect Contains in Graph.",
    check: "A Cluster below its Group in Tree is the target of Contains in Graph.",
    view: "data", graph: "tree", projection: "tree", flow: [[["Group", "projection"]], [["Contains · hierarchy", "rule"]], [["Cluster", "projection"]]],
    files: ["tutorial.tree.gkql.json", "tutorial.module.gkql.json"],
    prompt: "Read the tree projection and ontology. Show which field marks Contains as a hierarchy. Ask me to find the same parent and child in Tree and Graph.",
  },
  {
    id: "ontology", title: "Curate the ontology", goal: "Decide which node types and relationships the model allows.", action: "Open the projection editor and move the Group label from name to policy in Module GKQL.",
    check: "Check & store module accepts the label change. The Group identity still uses its Azure resource ID.",
    view: "ontology", graph: "inventory", module: "tutorial", projection: "inventory", flow: [[["Group", "identity"]], [["InventoryContains", "rule"]], [["Cluster", "identity"]]],
    files: ["tutorial.module.gkql.json"],
    prompt: "Read tutorial.module.gkql.json. Show the Group type, the InventoryContains endpoints, and the identity key. Help me move label: true from Group.name to Group.policy and check the module.",
  },
  {
    id: "rollup", title: "Roll up and pass policy down", goal: "Pass policy down to children and roll their state up.", action: "Compare a resource group’s policy with the policy its cluster inherits.",
    check: "The cluster copies g.policy. In the opposite direction, the group takes the worst cluster state.",
    view: "editor", graph: "health", projection: "health", flowLabel: "Policy flows down",
    flow: [[["Group policy", "rule"]], [["g.policy", "rule"]], [["Cluster policy", "projection"]]],
    companionFlow: { label: "State rolls up", levels: [[["Cluster state", "projection"]], [["summarize max", "rule"]], [["Group state", "projection"]]] },
    files: ["tutorial.policy.gkql.json", "tutorial.health.gkql.json"],
    prompt: "Read tutorial.policy.gkql.json and tutorial.health.gkql.json. Follow one policy to its child and one state to its parent. Ask me which values Azure measures, which values the policy copies, and which states the projection calculates.",
  },
  {
    id: "azure-health", title: "Build a workload health model", goal: "Trace changes in Azure and Kubernetes checks to the flows they affect.", action: "Compare the replay checkpoints. Select the affected flow and follow its evidence.",
    check: "Deploy to AKS uses API CPU capacity. Serve traffic to Services uses Pod readiness and the Azure probe.",
    view: "data", graph: "workloadHealth", projection: "signals", flowLabel: "Evidence rolls up to user flows",
    flow: [[["Pod readiness", "input"], ["Azure probe", "input"], ["Azure API CPU", "input"]], [["Service checks", "rule"], ["Capacity check", "rule"]], [["Serve traffic to Services", "projection"], ["Deploy to AKS", "projection"]], [workloadHealth]],
    files: ["tutorial.workloadHealth.gkql.json", "tutorial.signals.gkql.json"],
    commands: [{ label: "Compare two and four readings at one checkpoint", code: "./scripts/tutorial.sh compare-health 4" }, { label: "Verify the replay", code: "./scripts/tutorial.sh verify" }, { label: "Ingest live Azure metrics", code: "npm --prefix lab/capture ci\n./scripts/tutorial.sh live" }],
    prompt: "Read the workload health and signals GKQL. Follow a user flow to its checks and dependencies. Ask me to predict what changes at each CPU replay checkpoint, what Unknown means, and which observations would measure request success.",
  },
];


export const LESSONS = chapters.map(lesson => ({
  ...lesson, module: lesson.module ?? "tutorial",
  source: lesson.source ?? (["workloadFlows", "appTelemetry", "containerApps", "ontologyGaps"].includes(lesson.graph) && lesson.space
    ? "demo-project" : lesson.space ? "health-mirror" : "engine"),
}));
