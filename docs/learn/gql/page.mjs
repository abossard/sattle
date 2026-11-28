const get = id => document.getElementById(id);
const clock = iso => `${iso.slice(11, 19)} UTC`;

function branch(arrow, relation, observation, selected = false) {
  const item = document.createElement("li");
  const edge = document.createElement("p");
  const label = document.createElement("code");
  const node = document.createElement("div");
  const title = document.createElement("strong");
  const times = document.createElement("small");
  const metric = observation.row_type === "ResourceMetricRow";
  edge.className = "edge";
  label.textContent = relation;
  edge.append(`${arrow} `, label);
  node.className = "node";
  node.toggleAttribute("data-selected", selected);
  title.textContent = metric ? `${observation.numeric_value}% CPU${selected ? " · used by GQL" : ""}` : "Azure inventory";
  const received = `Received ${clock(observation.captured_at)}`;
  times.textContent = metric ? `Sampled ${clock(observation.event_at)} · ${received}` : received;
  node.append(title, times);
  item.append(edge, node);
  return item;
}

async function load(path) {
  const response = await fetch(path);
  if (!response.ok) throw new Error(`${path}: HTTP ${response.status}`);
  return response;
}

async function start() {
  const [{ openReplay }, data, binary] = await Promise.all([
    import("./replay.mjs"), load("replay.json"), load("vendor/grafeo_wasm_bg.wasm"),
  ]);
  const seed = await data.json();
  const project = await openReplay(seed, await binary.arrayBuffer());
  const { warning_percent, critical_percent, stale_after_ns } = seed.policy;
  get("policy").textContent = `Warning at ${warning_percent}%, critical at ${critical_percent}%, `
    + `and unknown when the latest reading is more than ${Number(stale_after_ns) / 60e9} minutes old.`;
  get("projection").textContent = seed.projection;

  function show(stop) {
    const { observations, assessments: [assessment] } = project(stop);
    const reading = assessment.point_evidence;
    get("observations").replaceChildren(...observations.map(({ observation, relation }) =>
      branch("↑", relation, observation, observation.id === reading?.id)));
    get("no-readings").hidden = observations.some(row => row.relation === "MEASURES_ENTITY");
    get("master-resource").textContent = observations[0].name;
    get("health-resource").textContent = assessment.subject_name;
    get("state").textContent = assessment.state;
    get("state").dataset.state = assessment.state;
    get("reason").textContent = assessment.reason;
    get("evidence").replaceChildren(...[assessment.resource_evidence, reading].filter(Boolean).map(row =>
      branch("↓", "SUPPORTED_BY", row)));
    get("replay-status").textContent = `${clock(stop.at)} · ${assessment.state}`;
  }

  for (const [index, stop] of seed.checkpoints.entries()) {
    const label = document.createElement("label");
    const input = document.createElement("input");
    const time = document.createElement("time");
    const caption = document.createElement("span");
    input.type = "radio";
    input.name = "checkpoint";
    input.checked = index === 0;
    input.addEventListener("change", () => show(stop));
    time.dateTime = stop.at;
    time.textContent = stop.at.slice(11, 19);
    caption.textContent = stop.label;
    label.append(input, time, caption);
    get("stops").append(label);
  }
  show(seed.checkpoints[0]);
  document.querySelector(".graphs").hidden = false;
  get("timeline").disabled = false;
}

start().catch(error => {
  get("replay-status").textContent = `The replay could not start: ${error.message}`;
});
