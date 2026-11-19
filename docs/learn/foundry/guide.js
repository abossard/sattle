"use strict";

const structureFile="engine/definitions/acme.workload.workloadStructure.kql";
const healthFile="engine/definitions/acme.workload.workloadHealth.kql";
const lessons={
  estate:["The shop’s master graph","Start with the stuck refund for order O-104. Each point in this illustrated shop starts as a source record. Follow the same records through five views, or choose a result below to see its evidence.",exploreJourney,[
    ["Sources and adapters","paper/ch10-sources-and-adapters.md"],["Identity","paper/ch01-values-and-identity.md"],
    ["Shop source records","docs/learn/foundry/graph-journey.js"],["Retail architecture and sources","docs/learn/foundry/sources.md"]]],
  rollup:["How health rolls up","In Sattle’s pg-shop recording, the database stops answering at 10:11 UTC. Trace the alarm from the PostgreSQL server to the services that need it.",exploreRollup,[
    ["Database and cache rules","engine/tests/Sattle.Tests/fixtures/estate/authoring/rules.ndjson"],["How the health query rolls up severity",healthFile],
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

function exploreRollup(){
  setActivity('<section class="case-inspect"><h2>Compare the recorded revisions</h2><div id="revision-choices" class="options"></div><div class="flow-states" id="revision-states" role="status"></div><p id="revision-note"></p></section>');
  const moments=[
    [15,"Before the outage","The database and its callers are healthy."],
    [16,"Alarm opens","The database, API and web turn red together in this patch. The cache stays healthy."],
    [18,"Alarm clears","Two good database samples clear the alarm. The callers recover."]
  ];
  choices(moments.map(([rev,label])=>"Revision "+rev+" · "+label),i=>{
    const [rev,,note]=moments[i];
    const {states}=ShopReplayCases.rollup.frames.find(entry=>entry.revision===rev);
    stateCells($("#revision-states"),states,[["pg-shop","PostgreSQL"],["database","Database service"],["api","API"],["web","Web"],["apim-shop","Gateway"],["shopcache","Redis"]]);
    $("#revision-note").textContent=note;
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
