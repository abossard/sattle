"use strict";

const structureFile="engine/definitions/acme.workload.workloadStructure.kql";
const healthFile="engine/definitions/acme.workload.workloadHealth.kql";
const lessons={
  estate:["The shop’s master graph","Sattle connects records from the shop’s systems. Start with the stuck refund for order O-104. Follow its payment call, then switch views to see which pods run the refund job.",exploreJourney,[
    ["Sources and adapters","paper/ch10-sources-and-adapters.md"],["Identity","paper/core/ch02-values-and-identity.md"],
    ["Shop source records","docs/learn/foundry/graph-journey.js"],["Retail architecture and sources","docs/learn/foundry/sources.md"]]],
  rollup:["Health Tree","The database answered at 10:10 UTC. A minute later its check failed. Follow the web service’s branch to PostgreSQL, then move through the checks to see when the alarm clears.",exploreHealthTree,[
    ["How the graphs are connected","engine/definitions/acme.space.sattle"],["Where the branches come from",structureFile],
    ["Database and cache rules","engine/tests/Sattle.Tests/fixtures/estate/authoring/rules.ndjson"],["How the health query rolls up severity",healthFile],
    ["How the runtime updates reachable components","engine/src/Sattle.Runtime/Engine.cs#L208"],
    ["Recorded workload patches","canvas/public/demo/estate/workload.patches.ndjson"]]],
  discovery:["System Changes","Redis already appears under web in the shop’s health model. When the API starts using the same cache, Sattle adds Redis under the API too. Follow the new branch back to the call that put it there.",exploreDiscovery,[
    ["How the structure query matches traffic",structureFile],["How the health query uses the result",healthFile],
    ["Replay the extra Hubble flow","docs/learn/build_graph_cases.py"],["Evidence from the controlled replay","docs/learn/foundry/replay-cases.js"]]],
  gkql:["Reading a GKQL program","GKQL lets you draw a query as a graph. In Sattle’s deployment-health experiment, each ready pod counts toward one workload. Follow the blocks to see how two calls from web to db produce one link.",exploreGkql,[
    ["The executable GKQL program",ShopGkqlExample.sources.program],
    ["The pod readiness events",ShopGkqlExample.sources.kubernetes],
    ["The two recorded calls",ShopGkqlExample.sources.hubble],
    ["The replay comparison",ShopGkqlExample.sources.tests],
    ["The graph-language design","paper/research/14-graph-native-projection-language.md"],
    ["GKQL migration progress","https://github.com/abossard/sattle/issues/7"]]]
};

function exploreHealthTree(){
  const {tree,frames,rule,alarmTimes}=ShopReplayCases.rollup;
  const byKey=new Map(tree.nodes.map(node=>[node.key,node]));
  const resourceNames={"resource:pg-shop":"PostgreSQL · pg-shop","resource:shopcache":"Redis · shopcache","resource:shopassets":"Storage · shopassets"};
  const nameOf=node=>resourceNames[node.key] || node.name+" "+node.kind;
  setActivity(`<section class="case-inspect health-explorer"><h2>Trace a state through the Health Tree</h2>
    <fieldset><legend>Choose a database check (UTC)</legend><div id="revision-choices" class="options"></div></fieldset>
    <p id="revision-note"></p>
    <div class="health-tree-inspector"><div id="revision-tree"></div>
    <section class="health-tree-evidence" aria-labelledby="health-selection">
      <h3 id="health-selection"></h3><p id="health-explanation" role="status"></p>
    </section></div>
    <details><summary>See the signal rule and the roll-up query</summary>
      <p id="database-config"></p>
      <dl class="health-query"><dt>Which paths count?</dt><dd><code>(c)-[a*0..16]-&gt;(d)</code> includes the component itself and paths up to 16 links long.</dd>
      <dt>Which links count?</dt><dd><code>all(a, labels(a) has "Affects")</code> keeps paths made entirely of Affects links.</dd>
      <dt>How does a parent get its state?</dt><dd><code>summarize w = max(own) by k = rk</code> picks the highest contributing severity for each starting component.</dd></dl>
      <p>The compiler turns the path match into a <code>Traverse</code> operation. When the structure changes, the runtime updates which components each affected starting point can reach.</p>
    </details></section>`);
  $("#database-config").textContent=rule.id+" watches "+rule.meter+" on PostgreSQL. A zero means the server failed its check. The rule opens after "+rule.enterAfter+" bad minute and clears after "+rule.exitAfter+" consecutive good minutes.";
  const buttons=[], expanded=new Set();
  let selected=tree.root, frame;
  function addBranch(key,list){
    const node=byKey.get(key), item=document.createElement("li");
    const button=document.createElement("button"), label=document.createElement("span"), state=document.createElement("span");
    button.type="button";
    label.textContent=nameOf(node);
    button.append(label,state);
    button.addEventListener("click",()=>{selected=key;draw();});
    buttons.push({key,button,state});
    item.append(button);
    if(node.children.length && !expanded.has(key)){
      expanded.add(key);
      const children=document.createElement("ul");
      for(const child of node.children)addBranch(child,children);
      item.append(children);
    }
    list.append(item);
  }
  const list=document.createElement("ul");
  list.className="health-tree-list";
  addBranch(tree.root,list);
  $("#revision-tree").append(list);
  function draw(){
    for(const {key,button,state} of buttons){
      const value=frame.treeStates[key];
      state.textContent=value;
      state.dataset.state=value;
      button.setAttribute("aria-pressed",String(key===selected));
    }
    const node=byKey.get(selected), contributors=[], visited=new Set([selected]);
    const pending=[{key:selected,hops:0}];
    for(const {key,hops} of pending){
      if(Object.hasOwn(frame.treeOwn,key))contributors.push([nameOf(byKey.get(key)),frame.treeOwn[key]]);
      if(hops===16)continue;
      for(const child of byKey.get(key).children){
        if(visited.has(child))continue;
        visited.add(child);
        pending.push({key:child,hops:hops+1});
      }
    }
    const worst=Math.max(...contributors.map(([,level])=>level));
    const own=frame.treeOwn[selected], names=contributors.filter(([,level])=>level===worst).map(([name])=>name);
    $("#health-selection").textContent=nameOf(node)+" · "+frame.treeStates[selected];
    let explanation;
    if(selected==="resource:pg-shop"){
      explanation=frame.signal==="on" ? "The database alarm is open. PostgreSQL contributes severity "+own+" to the shop’s Health Tree." : "The database alarm is clear. PostgreSQL contributes severity "+own+".";
    }else if(!contributors.length){
      explanation="No measured severity is available on this branch.";
    }else if(!node.children.length){
      explanation="This component contributes its own severity of "+own+" to the branches above it.";
    }else{
      const local=own===undefined?"This service has no severity reading of its own.":"This component’s own severity is "+own+".";
      explanation=local+" The highest severity on this branch is "+worst+". It comes from "+names.join(", ")+".";
    }
    $("#health-explanation").textContent=explanation;
  }
  const labels=["Last good check","First failed check","First good check","Second good check"];
  const notes=[
    "PostgreSQL answered. The shop’s database branch is healthy.",
    "PostgreSQL missed its check. The tree shows the alarm it triggered at "+alarmTimes.opened.slice(0,5)+" UTC.",
    "PostgreSQL answered again. The alarm stays open while the rule waits for a second good check.",
    "PostgreSQL answered twice in a row. The model cleared the alarm at "+alarmTimes.cleared.slice(0,5)+" UTC."
  ];
  choices(frames.map((f,i)=>f.minute+" · "+labels[i]),i=>{
    frame=frames[i]; draw();
    $("#revision-note").textContent=notes[i]+" Sampled at "+frame.minute+" UTC, received at "+frame.received+" UTC.";
  },$("#revision-choices"),1);
}

function exploreDiscovery(){
  const {topology,flow,cutoff,proofs}=ShopReplayCases.discovery;
  const targets=new Map(topology.nodes.map(node=>[node.key,node.name]));
  const names={"service:api":"API","service:web":"Web","service:database":"Database service","resource:shopcache":"Redis · shopcache","resource:shopassets":"Storage · shopassets"};
  setActivity(`<section class="case-inspect journey-inspector dependency-compare">
    <h2>Where does Redis appear in the health model?</h2>
    <p>Both replays use the same rules and end at ${cutoff} UTC. The second includes one extra call from the API pod to Redis.</p>
    <fieldset><legend>Choose a service</legend><div id="model-services" class="options"></div></fieldset>
    <div class="model-branches">
      <section><h3>Without the new call</h3><p></p><ul></ul></section>
      <section><h3>With the new call</h3><p></p><ul></ul></section>
    </div>
    <p id="model-result" role="status"></p>
    <details id="redis-trace"><summary>See the records behind the API’s Redis branch</summary></details>
    </section>`);
  const services=["service:api","service:web"], cards=[...document.querySelectorAll(".model-branches > section")];
  const baseline=new Set(topology.before.map(edge=>edge.id));
  choices(services.map(id=>names[id]),index=>{
    const service=services[index];
    for(const [i,edges] of [topology.before,topology.after].entries()){
      const card=cards[i], list=card.querySelector("ul");
      card.querySelector("p").textContent=names[service]+" depends on";
      list.replaceChildren();
      const dependencies=edges.filter(edge=>edge.from===service);
      dependencies.sort((a,b)=>Number(baseline.has(b.id))-Number(baseline.has(a.id)));
      for(const edge of dependencies){
        const item=document.createElement("li");
        item.textContent=names[edge.to] || targets.get(edge.to);
        if(!baseline.has(edge.id)){
          const mark=document.createElement("strong");mark.textContent="+ New dependency";
          item.className="added-dependency";item.append(mark);
        }
        list.append(item);
      }
    }
    $("#model-result").textContent=service==="service:api"
      ? "The API still needs the database. Its first observed call to Redis adds a second dependency under the API."
      : "Web already used this Redis cache. Its branch looks the same in both runs.";
  },$("#model-services"));
  const trace=$("#redis-trace"), list=document.createElement("dl");
  for(const [name,value] of [
    ["Observed call",flow.pod+" → "+flow.address+" at "+flow.time+" UTC"],
    ...proofs.map(proof=>[proof.how==="privateEndpoint" ? "Private endpoint" : "DNS name",proof.detail]),
    ["Result in the health model","API → DependsOn → Redis (shopcache)"]
  ]){
    const term=document.createElement("dt"), meaning=document.createElement("dd");
    term.textContent=name;meaning.textContent=value;list.append(term,meaning);
  }
  trace.append(list);
}
