"use strict";
const lessons = {
  origins: ["The master graph", "One request travels from web to the API and on to pg-shop. The master graph shows what the sources saw across the estate. The workload view picks out what the shop depends on.", observations, [["Full master graph snapshot","canvas/public/demo/estate/master.final.snapshot.json"],["Capture format","lab/CAPTURE-FORMAT.md"],["Azure lab","lab/README.md"],["Graph space","https://github.com/abossard/sattle/blob/fcf7725/engine/definitions/acme.space.sattle"]]],
  rules: ["How rules shape health", "The database answers again at 10:14. The alarm clears at 10:15. That minute of waiting comes from a rule the team can change.", author, [["Scope and bindings","https://github.com/abossard/sattle/blob/fcf7725/engine/definitions/acme.space.sattle"],["Shipped signal rules","https://github.com/abossard/sattle/blob/fcf7725/engine/tests/Sattle.Tests/fixtures/estate/authoring/rules.ndjson"],["Health calculation","https://github.com/abossard/sattle/blob/fcf7725/engine/definitions/acme.workload.workloadHealth.kql"]]],
  layers: ["Identity and projection", "Three web pods call two API pods. Those calls support a single service link. A shared resource ID also lets two Azure records describe the same server.", identity, [["Workload ontology","https://github.com/abossard/sattle/blob/fcf7725/engine/definitions/acme.workload.sattle"],["Paper · Identity","paper/core/ch02-values-and-identity.md"],["Paper · Ontologies","paper/ch02-ontologies-as-type-graphs.md"],["Paper · Projections","paper/ch05-projection-language.md"]]],
  incident: ["The pg-shop incident", "Read across a row to follow one service through time. Read down a minute to see which services share the outage.", incident, [["Database readings","https://github.com/abossard/sattle/blob/fcf7725/engine/tests/Sattle.Tests/fixtures/estate/azure-metrics/shop.ndjson"],["Paper · Measurements","paper/ch07-telemetry-plane.md"]]],
  dependency: ["A new dependency", "Both runs use the same cache readings. In the second, one new dependency gives the cache warning a path to the gateway.", compareChange, [["Reproduce the experiment","docs/learn/replay_change.py"],["How dependencies are matched","https://github.com/abossard/sattle/blob/fcf7725/engine/definitions/acme.workload.workloadStructure.kql"]]],
  azure: ["A change on Azure", "The API names a host. A later Azure Resource Graph result identifies the cache by name and type. Its health still needs a reading.", compareAzure, [["Reproduce both resource queries","docs/learn/replay_change.py"],["Azure input format","lab/CAPTURE-FORMAT.md"],["Paper · Incomplete sources","paper/ch10-sources-and-adapters.md"]]],
  replay: ["Rewind a revision", "Move between versions 15 and 18 of the saved shop map. Which services change when the database goes down?", revisions, [["Exported workload patches","canvas/public/demo/estate/workload.patches.ndjson"],["Paper · Incremental updates","paper/core/ch07-requirements-on-a-projection-language.md"],["Revisions","paper/ch03-graphs-and-revisions.md"]]],
  inspect: ["Trace an alarm", "Open the workload tree to find pg-shop. Then use the other two Canvas views to find the query behind its health.", inspectProject, [["Engine","engine/README.md"],["Canvas","canvas/README.md"],["Paper · Evidence and lineage","paper/ch09-evolution-evidence-provenance.md"],["Paper · Evaluation","paper/ch14-evaluation.md"]]]
};
function incident() {
  const samples = replay.readings.filter(s => s.minute >= "10:08" && s.minute <= "10:17");
  const rule = replay.rule;
  let alarm = false, bad = 0, good = 0;
  const states = samples.map(sample => {
    bad = sample.value <= -rule.above ? bad+1 : 0;
    good = sample.value >= -rule.below ? good+1 : 0;
    if (bad >= rule.enterAfter) alarm = true;
    if (good >= rule.exitAfter) alarm = false;
    return alarm;
  });
  const x = i => 65+i*70, y = value => value ? 38 : 117;
  const path = 'M'+x(0)+' '+y(samples[0].value)+samples.slice(1).map((s,i) => 'H'+x(i+1)+'V'+y(s.value)).join('');
  const grid = [38,117].map(h => '<line x1="53" x2="715" y1="'+h+'" y2="'+h+'" stroke="var(--wire)" stroke-dasharray="3 5"/>').join('');
  const ticks = samples.map((s,i) => '<circle cx="'+x(i)+'" cy="'+y(s.value)+'" r="3.4" fill="var(--blue)"/>'+([0,3,6,9].includes(i)?'<text x="'+x(i)+'" y="153" text-anchor="middle" fill="var(--muted)">'+s.minute+'</text>':'')).join('');
  setActivity(plate("Database signal / pg-shop","Synthetic replay",'<div class="headline"><div class="clock"><span id="time"></span> <small>UTC</small></div><strong id="health" class="state"></strong></div><svg class="chart" viewBox="0 0 740 166" role="img" aria-label="Database meter. One until 10:10, zero from 10:11 through 10:13, then one again."><g font-family="var(--mono)" font-size="15">'+grid+'<text x="22" y="43" fill="var(--muted)">1</text><text x="22" y="122" fill="var(--muted)">0</text><path d="'+path+'" fill="none" stroke="var(--blue)" stroke-width="4" stroke-linejoin="round"/>'+ticks+'<g class="cursor" id="marker"><circle r="13" fill="var(--input)" opacity=".25"/><circle r="6" fill="var(--input)"/></g></g></svg><div class="scrub"><label for="minute">Sample minute</label><input id="minute" type="range" min="0" max="9" value="3" step="1"></div>')+'<p class="note" id="signal-note"></p><details><summary>Why does the alarm stay on after a good reading?</summary><p>One minute at or below 0.5 trips this alarm. Two minutes in a row at or above 0.9 clear it. That’s why the first good reading leaves the database red.</p></details>');
  const slider = $("#minute");
  function update() {
    const i=Number(slider.value), s=samples[i], unhealthy=states[i];
    $("#time").textContent=s.minute;
    $("#health").textContent=unhealthy?"× Database unhealthy":"✓ Database healthy";
    $("#health").classList.toggle("danger",unhealthy);
    $("#marker").style.transform="translate("+x(i)+"px,"+y(s.value)+"px)";
    slider.setAttribute("aria-valuetext",s.minute+" UTC, reading "+s.value+", database "+(unhealthy?"unhealthy":"healthy"));
    const why = s.value===0 ? "One low minute meets the database-down rule." : unhealthy ? "This is the first good minute. The rule waits for one more." : s.minute==="10:15" ? "Two good minutes in a row clear the database alarm." : "The database rule is clear.";
    $("#signal-note").textContent="Reading "+s.value+" at "+s.minute+" UTC. "+why+" This sample was captured at "+s.received+" UTC.";
  }
  slider.oninput=update;update();
}

function observations() {
  const sourceRows=[
    ["Azure Resource Graph","Which resource is this?","Resource name","pg-shop","The query returns the PostgreSQL server’s name, Azure resource ID and resource type. Azure Monitor uses the same ID for its meter readings."],
    ["Kubernetes","Where does the service point?","External name","pg-shop.postgres.database.azure.com","The service named database points to this network name. Sattle matches this name to the Azure server and draws the link."],
    ["Azure Monitor","What happened at 10:11?","Meter / value","is_db_alive / 0","The 10:11 sample reached the capture at 10:12:01 UTC. An adapter picks out the time, server ID and value so Sattle can use the reading."]
  ];
  setActivity(radioArea()+plate("Source record","Synthetic estate",'<div id="record" aria-live="polite"></div>')+'<p class="note">A gap between Resource Graph results leaves the known server on the map. A deletion record can remove it.</p>');
  choices(sourceRows.map(s=>s[0]),i=>{
    const s=sourceRows[i];
    $("#record").innerHTML='<h2>'+s[1]+'</h2><dl><dt>Source</dt><dd>'+s[0]+'</dd><dt>'+s[2]+'</dt><dd class="mono">'+s[3]+'</dd></dl><p>'+s[4]+'</p>';
  },$("#choices"));
}

function identity() {
  setActivity(plate("Compare two records","Azure",'<dl><dt>Resource Graph result</dt><dd>A server named <code>pg-shop</code></dd><dt>Meter target</dt><dd>A resource whose ID ends in <code>/flexibleservers/pg-shop</code></dd></dl><p class="mono">'+replay.resourceId+'</p>')+'<h2>What joins these reports?</h2>'+radioArea()+'<p id="answer" class="note" role="status">Which field identifies the same server in both records?</p><details><summary>Who defines the terms on the map?</summary><p>The workload ontology names the kinds of things the map can hold and which links connect them. The team can maintain this vocabulary as its workload changes. An identity rule says which key identifies each thing. For an Azure server, that key is its full resource ID.</p></details>');
  const buttons = $("#choices");
  ["The display name","The shared resource ID","The same arrival minute"].forEach((label,i)=>{
    const b=document.createElement("button"); b.className="pick";b.textContent=label;b.setAttribute("aria-pressed","false");
    b.onclick=()=>{for(const other of buttons.children)other.setAttribute("aria-pressed",String(other===b));$("#answer").textContent=[
      "Names can repeat in different resource groups. Inspect the full ID that both records carry.",
      "Yes. The full Azure resource ID identifies the same server in both records. The Kubernetes service has a different key, and a BackedBy link connects the two entities.",
      "Reports about unrelated things can arrive together. Look for a key that still works when the next reading arrives later."
    ][i];};
    buttons.append(b);
  });
}
function revisions() {
  const frame = replay.revisions.filter(r => r.revision >= 15 && r.revision <= 18);
  setActivity(plate("Workload / static export","Revision history",'<div class="scrub"><label for="revision">Workload revision</label><input id="revision" type="range" min="15" max="18" value="16" step="1"><output id="rev"></output></div><div id="states" class="status-grid"></div>')+'<p class="note" id="change"></p><details><summary>Why can I rewind and replay?</summary><p>A snapshot records a starting graph. A patch records the edits between two revisions. The engine can apply the patches in order to get back to a past graph. Its incremental calculation updates only what changed. A separate evaluator recomputes from scratch so tests can compare the results, including when the records arrive in different batch sizes.</p></details>');
  const slider=$("#revision");
  function update(){
    const i=Number(slider.value)-15, f=frame[i];
    $("#rev").textContent=f.revision;slider.setAttribute("aria-valuetext","workload revision "+f.revision);
    $("#states").innerHTML=["pg-shop","database","api","web","shop","shopcache"].map(name=>'<div class="status-cell"><b>'+name+'</b><span data-state="'+f.states[name]+'">'+f.states[name]+'</span></div>').join('');
    $("#change").textContent=["Before the database incident, the six components shown here are healthy.","The patch marks pg-shop, its dependent services, and the shop unhealthy. The cache stays healthy.","This revision has other edits. The health states of these six components stay the same.","The recovery patch returns the database and the affected services to healthy."][i];
  }
  slider.oninput=update;update();
}

function canvas() {
  const views=[
    ["Workload tree","incident","Which component is the origin?","In Data graph, select workload and open Tree. In the static demo, step to workload revision 16. Select pg-shop and inspect its own state and the other graphs that describe it.","data"],
    ["Graph space","graph-space","Which graphs feed this answer?","Graph space lays out the projections. Find workloadStructure and workload, then follow their inputs back to master and the authored rules.","space"],
    ["Projection editor","editor-kql","Which rule produced this result?","On a running engine, open the workloadHealth projection. Check a draft and preview its rows to see which step changes the result before committing.","editor"]
  ];
  setActivity(radioArea()+'<div id="canvas-shot"></div><p><a href="film/assets/canvas-replay.mp4">Watch the recorded Canvas replay</a></p><details><summary>Run the real Canvas locally</summary><p>From the repository root, follow the build steps in the '+link("getting started guide","README.md")+'. Then run <code>scripts/demo-replay.sh</code>. Its printed URL opens the local Canvas. Choose Synthetic estate (engine output, static files) to see the exact revisions used here. Select Engine for live replay and projection editing.</p></details>');
  choices(views.map(v=>v[0]),i=>{
    const [name,img,title,desc,view]=views[i];
    $("#canvas-shot").innerHTML='<img class="demo-img" src="film/assets/'+img+'.webp" alt="'+name+' in the actual Sattle Canvas"><h2>'+title+'</h2><p>'+desc+'</p><a href="http://localhost:5173/#source='+(i===2?'engine':'demo')+'&amp;view='+view+'">Open '+name+' in your running Canvas ↗</a>';
  },$("#choices"));
}

function author() {
  setActivity('<h2>When does the alarm clear?</h2><p>The meter rises to one at 10:14. Switch between two recovery rules to see when the alarm clears.</p>'+radioArea()+'<p class="note" id="rule-result" role="status"></p><details><summary>What can a team change?</summary><p>The replay includes the shop’s scope, source mappings and signal rules. A team can select another cluster and namespace, refine a threshold or choose which calls affect health. A source mapping can bring in another kind of reading.</p></details>');
  choices(["1 good minute · what-if","2 good minutes · shipped rule"],i=>{
    $("#rule-result").textContent=i===0?"With this hypothetical rule, pg-shop recovers at the 10:14 sample. The meter values are identical.":"The shipped rule keeps pg-shop unhealthy at 10:14 and clears it at the 10:15 sample.";
  },$("#choices"),1);
}

function comparison(title, labels, runs, names, explain, beats) {
  setActivity('<h2>'+title+'</h2>'+radioArea()+plate("Engine output at 10:06:30 UTC","Local replay",'<div id="compare-states" class="status-grid"></div>')+'<p id="comparison-note" class="note" role="status"></p>');
  let initialized=false;
  choices(labels,i=>{
    const run=changeResult[runs[i]];
    $("#compare-states").innerHTML=names.map(n=>{
      const state=run.states[n]||"Absent";
      return '<div class="status-cell"><b>'+n+'</b><span data-state="'+state+'">'+state+'</span></div>';
    }).join("");
    $("#comparison-note").textContent=explain[i]+(run.configurationEvidence.length?" Engine evidence: "+run.configurationEvidence[0]+".":"");
    if(initialized) player.jump(beats[i],.97);
    initialized=true;
  },$("#choices"));
}
function compareChange(){
  comparison("The API gains a cache dependency",["Original recording","With CACHE_HOST"],["original","changed"],["shopcache","web","api","apim-shop","pg-shop","shop"],[
    "The web service uses the cache. The API and its gateway stay healthy.",
    "The API now points to shopcache.redis.cache.windows.net. Sattle draws the new link. The overloaded cache now affects the API and its gateway."
  ],[0,2]);
}
function compareAzure(){
  comparison("A new cache appears on Azure",["API names a host","Resource Graph lists the cache"],["waiting","discovered"],["offers-cache","api","apim-shop","shopcache","pg-shop","shop"],[
    "The replay has a host name in the API deployment. Its Resource Graph results have no matching cache yet.",
    "The Resource Graph row names a matching Redis cache, so Sattle adds the cache and its link to the API. Its health reads Unknown until a meter tells us how it’s doing."
  ],[0,2]);
}
function inspectProject(){
  canvas();
  const more=document.createElement("div");
  more.innerHTML='<details><summary>How do the repository and paper fit together?</summary><p>'+link('The lab','lab/README.md')+' captures reports. '+link('The engine','engine/README.md')+' builds the maps and tracks what changed. '+link('The Canvas','canvas/README.md')+' lets you step through past maps, inspect the evidence and edit rules. '+link('The white paper','paper/README.md')+' describes the theory and the assumptions behind it. Its real-world evaluation covers a small Azure lab. '+link('Chapter 8','paper/core/ch08-derived-results-limits-related-work.md')+' discusses scale and open questions.</p></details>';
  $("#activity").append(more);
}
