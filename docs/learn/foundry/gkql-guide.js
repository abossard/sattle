"use strict";

function exploreGkql() {
  const {verbs, wires, frames, pods} = ShopGkqlExample;
  // The IDs belong to the checked-in program. Several blocks use the same verb.
  const descriptions = {
    deployments:"Find every Deployment, including one whose pods have all stopped being ready.",
    readyPods:"Match a Pod with ready set to true and follow its ControlledBy edge to its Deployment.",
    zero:"Give each deployment a starting count of zero.",
    one:"Give each ready pod a value of one and keep the deployment it belongs to.",
    rows:"Combine the starting zeros and the ones from ready pods.",
    count:"Add up the values for each deployment. The sum is its readyPods count.",
    status:"Mark a deployment Healthy when its count is greater than minReady. The recorded run uses zero.",
    workloads:"Create a Workload for each Deployment, using the deployment’s identity.",
    workloadState:"Write readyPods and state onto the Workload with that same identity.",
    calls:"Match a call from one pod to another and find the deployment at each end.",
    apart:"Keep calls whose pods belong to different deployments.",
    srcStatus:"Keep each deployment’s identity and state for the caller side of the join.",
    dstStatus:"Keep each deployment’s identity and state for the destination side of the join.",
    withSrc:"Join the call’s source deployment to its health state.",
    withDst:"Join the call’s destination deployment to its health state.",
    talks:"Connect the two Workloads with a Talks edge. Either end being Unhealthy makes that edge Unhealthy."
  };
  setActivity(`<section class="case-inspect journey-inspector">
    <h2>Follow a wire through the query</h2>
    <label for="gkql-verb">Program block</label><select id="gkql-verb"></select>
    <div class="verb-neighborhood">
      <div><h3>Inputs</h3><div id="gkql-inputs"></div></div>
      <div class="current-verb"><h3 id="gkql-operation"></h3><p id="gkql-explanation"></p></div>
      <div><h3>Outputs to</h3><div id="gkql-outputs"></div></div>
    </div></section>
    <section class="case-inspect journey-inspector">
    <h2>When does the workload change color?</h2>
    <label for="gkql-time">Include observations through (UTC)</label><select id="gkql-time"></select>
    <table class="gkql-readings"><thead><tr><th scope="col">Deployment</th><th scope="col">Ready pods</th><th scope="col">Workload state</th></tr></thead><tbody></tbody></table>
    <p id="gkql-edge-state" role="status"></p>
    <details><summary>Check the target identities</summary>
    <p>The two workloads and their link keep these IDs at all six replay times.</p>
    <dl id="gkql-identities"></dl></details></section>`);

  const picker = $("#gkql-verb");
  for (const {id, verb} of verbs) picker.add(new Option(id + " · " + verb, id));
  function neighborList(host, links, incoming) {
    host.replaceChildren();
    if (!links.length) {
      host.textContent = incoming ? "master graph" : "deploymentHealth graph";
      return;
    }
    for (const wire of links) {
      const id = incoming ? wire.from : wire.to;
      const button = document.createElement("button");
      button.type = "button";
      button.textContent = wire.port ? id + " · " + wire.port + " input" : id;
      button.addEventListener("click", () => {
        selectBlock(id);
        picker.focus({preventScroll:true});
      });
      host.append(button);
    }
  }
  function selectBlock(id) {
    picker.value = id;
    const {verb} = verbs.find(block => block.id === id);
    $("#gkql-operation").textContent = id + " · " + verb;
    $("#gkql-explanation").textContent = descriptions[id];
    neighborList($("#gkql-inputs"), wires.filter(wire => wire.to === id), true);
    neighborList($("#gkql-outputs"), wires.filter(wire => wire.from === id), false);
  }
  picker.addEventListener("change", () => selectBlock(picker.value));
  selectBlock("count");

  const times = $("#gkql-time"), tbody = $(".gkql-readings tbody");
  for (const frame of frames) times.add(new Option(frame.time, frame.time));
  function showFrame() {
    const frame = frames.find(item => item.time === times.value);
    tbody.replaceChildren();
    for (const deployment of ShopGkqlExample.deployments) {
      const row = tbody.insertRow(), label = document.createElement("th");
      label.scope = "row";
      label.textContent = deployment;
      row.append(label);
      const ready = pods.filter(pod => pod.deployment === deployment && frame.ready[pod.id]);
      const count = row.insertCell();
      count.textContent = String(frame.counts[deployment]);
      if (ready.length) {
        const names = document.createElement("small");
        names.textContent = ready.map(pod => pod.id).join(", ");
        count.append(names);
      }
      const state = document.createElement("span");
      state.dataset.state = frame.states[deployment];
      state.textContent = frame.states[deployment];
      row.insertCell().append(state);
    }
    $("#gkql-edge-state").textContent = "web → db · Talks is " + frame.talks + " at " + frame.time + " UTC.";
  }
  times.addEventListener("change", showFrame);
  showFrame();
  const ids = ShopGkqlExample.identities;
  for (const [label, id] of [...Object.entries(ids.workloads), ["web → db · Talks", ids.talks]]) {
    const name = document.createElement("dt"), value = document.createElement("dd");
    name.textContent = label;
    value.textContent = id;
    $("#gkql-identities").append(name, value);
  }
}
