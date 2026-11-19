"use strict";

const structureFile="engine/definitions/acme.workload.workloadStructure.kql";
const healthFile="engine/definitions/acme.workload.workloadHealth.kql";
const lessons={
  estate:["The shop’s master graph","Start with the stuck refund for order O-104. Each point in this illustrated shop starts as a source record. Follow the same records through five views, or choose a result below to see its evidence.",exploreJourney,[
    ["Sources and adapters","paper/ch10-sources-and-adapters.md"],["Identity","paper/ch01-values-and-identity.md"],
    ["Shop source records","docs/learn/foundry/graph-journey.js"],["Retail architecture and sources","docs/learn/foundry/sources.md"]]],
  rollup:["Health Tree","The web service turns red when PostgreSQL stops answering. Open its Health Tree to see which branch carried the alarm, where the branches came from, and how the query decides the color at the root.",exploreHealthTree,[
    ["How the graphs are connected","engine/definitions/acme.space.sattle"],["Where the branches come from",structureFile],
    ["Database and cache rules","engine/tests/Sattle.Tests/fixtures/estate/authoring/rules.ndjson"],["How the health query rolls up severity",healthFile],
    ["How the runtime updates reachable components","engine/src/Sattle.Runtime/Engine.cs#L208"],
    ["Recorded workload patches","canvas/public/demo/estate/workload.patches.ndjson"]]],
  discovery:["When the system changes","What if the shop changes and nobody updates its health model? Here an API pod starts calling Redis. When Sattle processes the new flow, its existing rules add the dependency and carry the cache warning to the API and its gateway.",exploreDiscovery,[
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

function stateCells(host, states, components){
  const cells=components.map(([key,label])=>{
    const cell=document.createElement("div");
    cell.dataset.state=states[key];
    cell.textContent=label+" · "+states[key];
    return cell;
  });
  host.replaceChildren(...cells);
}

function exploreHealthTree(){
  const {tree,frames,rule}=ShopReplayCases.rollup;
  const byKey=new Map(tree.nodes.map(node=>[node.key,node]));
  const resourceNames={"resource:pg-shop":"PostgreSQL · pg-shop","resource:shopcache":"Redis · shopcache","resource:shopassets":"Storage · shopassets"};
  const nameOf=node=>resourceNames[node.key] || node.name+" "+node.kind;
  setActivity(`<section class="case-inspect health-explorer"><h2>Trace a state through the Health Tree</h2>
    <fieldset><legend>Recorded workload revision</legend><div id="revision-choices" class="options"></div></fieldset>
    <div class="health-tree-inspector"><div id="revision-tree"></div>
    <section class="health-tree-evidence" aria-labelledby="health-selection">
      <h3 id="health-selection"></h3><p id="health-explanation" role="status"></p>
    </section></div>
    <p id="revision-note"></p>
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
      explanation="The "+rule.id+" signal is "+frame.signal+". That gives PostgreSQL its own severity of "+own+".";
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
  const notes=[
    "Before the outage, every branch reports Healthy.",
    "The database alarm opens. Follow the red branch from web to PostgreSQL.",
    "The alarm is still open. The database rule needs two good minutes in a row to clear.",
    "The alarm clears. The same branches now lead to a healthy database."
  ];
  const labels=["Before the outage","Alarm opens","Alarm still open","Alarm clears"];
  choices(frames.map((f,i)=>f.revision+" · "+labels[i]),i=>{
    frame=frames[i]; draw();
    $("#revision-note").textContent=notes[i];
  },$("#revision-choices"),1);
}

function exploreDiscovery(){
  const data=ShopReplayCases.discovery;
  setActivity('<section class="case-inspect journey-inspector"><h2>How does one new flow change the model?</h2><div id="flow-choices" class="options"></div><div class="flow-states" id="compared-states" role="status"></div><p id="comparison-note"></p><details id="redis-trace"><summary>Trace the added dependency</summary></details></section>');
  choices(["Original replay","With the API flow"],i=>{
    stateCells($("#compared-states"),i ? data.after : data.before,[["shopcache","Redis"],["api","API"],["apim-shop","Gateway"]]);
    $("#comparison-note").textContent=i
      ? "The existing rules pick up the API’s new dependency. The cache warning now reaches the API and its gateway."
      : "Web already uses Redis. The API has no dependency on that cache in this run.";
    $("#redis-trace").hidden=!i;
  },$("#flow-choices"),1);
  const trace=$("#redis-trace"), list=document.createElement("dl");
  for(const [name,value] of [
    ["Hubble flow",data.flow.time+" UTC · "+data.flow.pod+" → "+data.flow.address+" · "+data.flow.verdict],
    ["Input record in the replay",data.flow.record],
    ["Master graph","Revision "+data.masterPatch.from+" → "+data.masterPatch.to+" · new Sends link"],
    ["Structure graph","Revision "+data.structurePatch.from+" → "+data.structurePatch.to+" · API depends on shopcache"],
    ["Health graph","Revision "+data.healthPatch.from+" → "+data.healthPatch.to+" · added DependsOn relationship "+data.lineage.workload.id],
    ...data.proofs.map(proof=>[proof.how==="privateEndpoint" ? "Private endpoint evidence" : "Host name evidence",proof.detail])
  ]){
    const term=document.createElement("dt"), meaning=document.createElement("dd");
    term.textContent=name;meaning.textContent=value;list.append(term,meaning);
  }
  trace.append(list);
}
