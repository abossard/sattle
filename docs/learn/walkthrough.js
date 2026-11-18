"use strict";

const $ = (selector) => document.querySelector(selector);
const root = "../../";
const lessons = {
  origins: ["The whole shop", "Start at the web service and follow its calls to the database. Pick a source below the map to see what it tells Sattle about pg-shop.", observations, [["Capture format","lab/CAPTURE-FORMAT.md"],["Azure lab","lab/README.md"],["Graph space","engine/definitions/acme.space.sattle"]]],
  rules: ["What you contribute", "The replay comes with a database alarm. Try changing how many good readings it takes for your shop to turn green again.", author, [["Scope and bindings","engine/definitions/acme.space.sattle"],["Shipped signal rules","engine/tests/Sattle.Tests/fixtures/estate/authoring/rules.ndjson"],["Health calculation","engine/definitions/acme.workload.workloadHealth.kql"]]],
  layers: ["The theory", "Azure sends two reports about pg-shop. Which field tells you they describe the same server?", identity, [["Paper · Identity","paper/ch01-values-and-identity.md"],["Paper · Ontologies","paper/ch02-ontologies-as-type-graphs.md"],["Paper · Projections","paper/ch05-projection-language.md"]]],
  incident: ["The pg-shop incident", "Drag the clock through the recorded outage. The database starts answering at 10:14. Watch when the alarm clears.", incident, [["Database readings","engine/tests/Sattle.Tests/fixtures/estate/azure-metrics/shop.ndjson"],["Paper · Measurements","paper/ch07-telemetry-plane.md"]]],
  dependency: ["Add a dependency", "Compare the gateway at the same moment in two runs. In the second run, the API also needs the cache.", compareChange, [["Reproduce the experiment","docs/learn/replay_change.py"],["How dependencies are matched","engine/definitions/acme.workload.workloadStructure.kql"]]],
  azure: ["Change Azure inventory", "The API already has the address for offers-cache. Add the Azure inventory record and see what the map can tell you about the new cache.", compareAzure, [["Reproduce both inventory cases","docs/learn/replay_change.py"],["Azure input format","lab/CAPTURE-FORMAT.md"],["Paper · Incomplete sources","paper/ch10-sources-and-adapters.md"]]],
  replay: ["Change and replay", "Move between versions 15 and 18 of the saved shop map. Which services change when the database goes down?", revisions, [["Exported workload patches","canvas/public/demo/estate/workload.patches.ndjson"],["Paper · Incremental updates","paper/ch06-semantics-and-incrementality.md"],["Revisions","paper/ch03-graphs-and-revisions.md"]]],
  inspect: ["Check the evidence", "Open the workload tree to find pg-shop. Then use the other two Canvas views to find the query behind its health.", inspectProject, [["Engine","engine/README.md"],["Canvas","canvas/README.md"],["Paper · Evidence and lineage","paper/ch09-evolution-evidence-provenance.md"],["Paper · Evaluation","paper/ch14-evaluation.md"]]]
};
const chapters=story.map(scene=>{
  const [nav,intro,activity,sources]=lessons[scene.id];
  return {...scene,nav,intro,activity,sources};
});

function plate(name, context, content) {
  return '<div class="plate"><div class="plate-inner"><div class="plate-top"><span>'+name+'</span><span class="chip">'+context+'</span></div>'+content+'</div></div>';
}
function choices(labels, onChange, container, initial=0) {
  const buttons = labels.map((label,index) => {
    const button = document.createElement("button");
    button.type = "button"; button.className = "pick"; button.textContent = label;
    button.addEventListener("click", () => select(index));
    container.append(button); return button;
  });
  function select(index) {
    buttons.forEach((button,i) => button.setAttribute("aria-pressed", String(i===index)));
    onChange(index);
  }
  select(initial);
}
function radioArea() { return '<div class="options" id="choices"></div>'; }
function setActivity(html) { $("#activity").innerHTML = html; }
function link(label, file) { return '<a href="'+root+file+'">'+label+'</a>'; }

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
  const grid = [38,117].map(h => '<line x1="53" x2="715" y1="'+h+'" y2="'+h+'" stroke="#486165" stroke-dasharray="3 5"/>').join('');
  const ticks = samples.map((s,i) => '<circle cx="'+x(i)+'" cy="'+y(s.value)+'" r="3.4" fill="#adddc9"/>'+([0,3,6,9].includes(i)?'<text x="'+x(i)+'" y="153" text-anchor="middle" fill="#c5d7d0">'+s.minute+'</text>':'')).join('');
  setActivity(plate("Database signal / pg-shop","Synthetic replay",'<div class="headline"><div class="clock"><span id="time"></span> <small>UTC</small></div><strong id="health" class="state"></strong></div><svg class="chart" viewBox="0 0 740 166" role="img" aria-label="Database meter. One until 10:10, zero from 10:11 through 10:13, then one again."><g font-family="monospace" font-size="15">'+grid+'<text x="22" y="43" fill="#c5d7d0">1</text><text x="22" y="122" fill="#c5d7d0">0</text><path d="'+path+'" fill="none" stroke="#9be2be" stroke-width="4" stroke-linejoin="round"/>'+ticks+'<g class="cursor" id="marker"><circle r="13" fill="#fff2bd" opacity=".25"/><circle r="6" fill="#f9db89"/></g></g></svg><div class="scrub"><label for="minute">Sample minute</label><input id="minute" type="range" min="0" max="9" value="3" step="1"></div>')+'<p class="note" id="signal-note"></p><details><summary>Why does the alarm stay on after a good reading?</summary><p>One minute at or below 0.5 trips this alarm. Two minutes in a row at or above 0.9 clear it. That’s why the first good reading leaves the database red.</p></details>');
  const slider = $("#minute");
  function update() {
    const i=Number(slider.value), s=samples[i], unhealthy=states[i];
    $("#time").textContent=s.minute;
    $("#health").textContent=unhealthy?"● Database unhealthy":"✓ Database healthy";
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
    ["Azure inventory","What exists?","Resource name","pg-shop","Azure lists a PostgreSQL server. The inventory supplies its resource ID and type. It takes another source to tell us how the server is doing."],
    ["Kubernetes","Where does the service point?","External name","pg-shop.postgres.database.azure.com","The service named database points to this network name. Sattle matches this name to the Azure server and draws the link."],
    ["Azure metrics","What happened at 10:11?","Meter / value","is_db_alive / 0","The 10:11 sample reached the capture at 10:12:01 UTC. An adapter picks out the time, server ID and value so Sattle can use the reading."]
  ];
  setActivity(radioArea()+plate("Source record","Synthetic estate",'<div id="record" aria-live="polite"></div>')+'<p class="note">If Azure skips a report, Sattle keeps the server on the map. It needs evidence of deletion to remove it.</p>');
  choices(sourceRows.map(s=>s[0]),i=>{
    const s=sourceRows[i];
    $("#record").innerHTML='<h2>'+s[1]+'</h2><dl><dt>Source</dt><dd>'+s[0]+'</dd><dt>'+s[2]+'</dt><dd class="mono">'+s[3]+'</dd></dl><p>'+s[4]+'</p>';
  },$("#choices"));
}

function identity() {
  setActivity(plate("Compare two records","Azure",'<dl><dt>Inventory</dt><dd>A server named <code>pg-shop</code></dd><dt>Meter target</dt><dd>A resource whose ID ends in <code>/flexibleservers/pg-shop</code></dd></dl><p class="mono">'+replay.resourceId+'</p>')+'<h2>What joins these reports?</h2>'+radioArea()+'<p id="answer" class="note" role="status">Choose the field you would trust.</p><details><summary>How does the paper describe this?</summary><p>An identity rule tells Sattle which key to use. Here it’s the Azure resource ID. An ontology names the kinds of things on the map and the links they can have. Sattle stores those definitions as data.</p></details>');
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
    $("#states").innerHTML=["pg-shop","database","api","web","shop","shopcache"].map(name=>'<div class="status-cell"><b>'+name+'</b><span class="'+(f.states[name]==="Unhealthy"?"danger":"")+'">'+f.states[name]+'</span></div>').join('');
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
  setActivity(radioArea()+'<div id="canvas-shot"></div><details><summary>Run the real Canvas locally</summary><p>From the repository root, follow the build steps in the '+link("getting started guide","README.md")+'. Then run <code>scripts/demo-replay.sh</code>. Its printed URL opens the local Canvas. Choose Synthetic estate (engine output, static files) to see the exact revisions used here. Select Engine for live replay and projection editing.</p></details>');
  choices(views.map(v=>v[0]),i=>{
    const [name,img,title,desc,view]=views[i];
    $("#canvas-shot").innerHTML='<img class="demo-img" src="film/assets/'+img+'.webp" alt="'+name+' in the actual Sattle Canvas"><h2>'+title+'</h2><p>'+desc+'</p><a href="http://localhost:5173/#source='+(i===2?'engine':'demo')+'&amp;view='+view+'">Open '+name+' in your running Canvas ↗</a>';
  },$("#choices"));
}

function author() {
  setActivity('<h2>When should the alarm clear?</h2><p>The recorded meter rises to one at 10:14 after three low minutes. Choose how many good minutes your rule requires to clear the alarm.</p>'+radioArea()+'<p class="note" id="rule-result" role="status"></p><details><summary>What do I supply for my own workload?</summary><p>Give Sattle read access to the sources you use. Choose the cluster and namespace for your shop. Start with the supplied queries and rules. Set the readings that count as trouble, how long trouble must last, and which services it affects. If you need a reading Sattle doesn’t collect yet, add a source mapping.</p></details>');
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
      return '<div class="status-cell"><b>'+n+'</b><span class="'+(state==="Degraded"?"amber":"")+'">'+state+'</span></div>';
    }).join("");
    $("#comparison-note").textContent=explain[i]+(run.configurationEvidence.length?" Engine evidence: "+run.configurationEvidence[0]+".":"");
    if(initialized) player.jump(beats[i],.97);
    initialized=true;
  },$("#choices"));
}
function compareChange(){
  comparison("Give the API a cache dependency",["Original recording","Add CACHE_HOST"],["original","changed"],["shopcache","web","api","apim-shop","pg-shop","shop"],[
    "The web service uses the cache. The API and its gateway stay healthy.",
    "The API now points to shopcache.redis.cache.windows.net. Sattle draws the new link. The overloaded cache now affects the API and its gateway."
  ],[0,2]);
}
function compareAzure(){
  comparison("Introduce a new Azure cache",["API names a host","Azure reports offers-cache"],["waiting","discovered"],["offers-cache","api","apim-shop","shopcache","pg-shop","shop"],[
    "The host name in the API deployment has no matching inventory record yet. The workload graph has no offers-cache component.",
    "Azure lists a Redis cache at that address, so Sattle adds the cache and its link to the API. Its health reads Unknown until a meter tells us how it’s doing."
  ],[0,2]);
}
function inspectProject(){
  canvas();
  const more=document.createElement("div");
  more.innerHTML='<details><summary>How do the repository and paper fit together?</summary><p>'+link('The lab','lab/README.md')+' captures reports. '+link('The engine','engine/README.md')+' builds the maps and tracks what changed. '+link('The Canvas','canvas/README.md')+' lets you step through past maps, inspect the evidence and edit rules. '+link('The white paper','paper/README.md')+' describes the theory and the assumptions behind it. Its real-world evaluation covers a small Azure lab. '+link('Chapter 15','paper/ch15-related-work-and-limitations.md')+' discusses scale and open questions.</p></details>';
  $("#activity").append(more);
}
const chapterNav=$("#chapters");
chapters.forEach((c,i)=>{
  const a=document.createElement("a");a.href="#"+c.id;a.innerHTML='<span>'+String(i+1).padStart(2,'0')+'</span>'+c.nav;chapterNav.append(a);
});
function show(focus=false) {
  const i=Math.max(0,chapters.findIndex(c=>c.id===location.hash.slice(1))), c=chapters[i];
  [...chapterNav.children].forEach((a,k)=>{if(k===i)a.setAttribute("aria-current","step");else a.removeAttribute("aria-current");});
  if(matchMedia("(max-width:640px)").matches){
    const selected=chapterNav.children[i];
    chapterNav.scrollLeft+=selected.getBoundingClientRect().left-chapterNav.getBoundingClientRect().left-5;
  }
  $("#eyebrow").textContent="The pg-shop replay / "+String(i+1).padStart(2,"0");
  $("#title").textContent=c.title;$("#intro").textContent=c.intro;player.load(c);c.activity();
  $("#reading").innerHTML='<div class="label">Explore the source</div>'+c.sources.map(([name,file])=>link(name,file)).join("");
  $("#back").disabled=i===0;$("#back").onclick=()=>location.hash=chapters[i-1].id;
  $("#next").textContent=i===chapters.length-1?"Replay from start ↺":"Next chapter →";
  $("#next").onclick=()=>location.hash=chapters[(i+1)%chapters.length].id;
  $("#count").textContent=(i+1)+" / "+chapters.length;
  document.title=c.nav+" · Sattle field guide";
  if(focus){$("#lesson").focus({preventScroll:true});window.scrollTo(0,0);}
}
window.addEventListener("hashchange",()=>show(true));
$(".skip").onclick=e=>{e.preventDefault();$("#lesson").focus();};
// Some basic static servers omit byte ranges. A local Blob makes their media seekable.
const mediaReady=new WeakMap();
function prepareMedia(media, source){
  if(mediaReady.has(media))return mediaReady.get(media);
  const prepare=async()=>{
    if(location.protocol==="file:")return;
    const url=source || media.currentSrc || media.src;
    const headers=await fetch(url,{method:"HEAD"});
    if(headers.headers.get("accept-ranges")?.includes("bytes")){
      if(source)media.src=source;
      return;
    }
    const response=await fetch(url);
    if(!response.ok)throw Error("Media unavailable: "+response.status);
    const blob=await response.blob();
    const loaded=new Promise((resolve,reject)=>{
      media.addEventListener("loadedmetadata",resolve,{once:true});
      media.addEventListener("error",reject,{once:true});
    });
    media.preload="metadata";media.src=URL.createObjectURL(blob);media.load();await loaded;
  };
  const ready=prepare().catch(()=>{
    mediaReady.delete(media);
    if(source)media.src=source;
  });
  mediaReady.set(media,ready);return ready;
}
const cinema=$("#cinema"), video=cinema.querySelector("video");
const filmSource=video.querySelector("source"), filmCaptions=$("#film-captions");
filmCaptions.src=location.protocol==="file:"?URL.createObjectURL(new Blob([localCaptions],{type:"text/vtt"})):"captions.vtt";

async function prepareFilm(){
  if(location.protocol==="file:")return;
  // An open guide can outlive a render. Check the small digest before reusing
  // a movie from the browser cache or the blob kept for servers without ranges.
  let version;
  try{
    const response=await fetch("film-version.txt",{cache:"no-store"});
    if(response.ok)version=(await response.text()).trim();
  }catch{/* Some static hosts serve only the media files. */}
  if(!/^[a-f0-9]{64}$/.test(version || ""))version=String(Date.now());

  const url=new URL(filmSource.src);
  url.searchParams.set("v",version);
  const changed=filmSource.src!==url.href, oldBlob=video.getAttribute("src");
  if(changed)mediaReady.delete(video);
  // Choose a native stream or a blob before changing the <source>. Starting
  // both requests for the same movie can block playback on a no-range server.
  await prepareMedia(video,url.href);
  if(changed){
    filmSource.src=url.href;
    cinema.querySelector("a[download]").href=url.href;
    filmCaptions.src="captions.vtt?v="+version;
    video.poster="film/poster.jpg?v="+version;
    if(oldBlob?.startsWith("blob:")&&oldBlob!==video.src)URL.revokeObjectURL(oldBlob);
  }
}
const watch=$("#watch");
watch.onclick=async()=>{
  player.pause();watch.disabled=true;
  try{await prepareFilm();cinema.showModal();}
  finally{watch.disabled=false;}
};
$("#close").onclick=()=>{video.pause();cinema.close();};
cinema.addEventListener("cancel",()=>video.pause());
cinema.addEventListener("close",()=>video.pause());

const player=(()=>{
  const audio=$("#narrator"), range=$("#scene-position"), listen=$("#listen");
  let scene, activeBeat=-1, map, position=0, frame=0, ghost=0, pendingPlay=0;
  function pause() {
    pendingPlay++;
    audio.pause();cancelAnimationFrame(frame);cancelAnimationFrame(ghost);
    listen.textContent="▶ Play with narration";
    if(audio.muted)listen.textContent="▶ Play animation";
  }
  function showTime(time) {
    position=Math.max(scene.beats[0].start,Math.min(time,scene.beats.at(-1).end));
    let i=scene.beats.findIndex(b=>position<b.end-.001);if(i<0)i=scene.beats.length-1;
    const beat=scene.beats[i];
    if(i!==activeBeat){
      activeBeat=i;map=ShopMap.mount($("#map"),scene.id,i);
      const [term, meaning]=ShopTerms[scene.id][i];
      $("#concept-term").textContent=term;
      $("#concept-meaning").textContent=meaning;
      [...$("#beat-buttons").children].forEach((b,k)=>b.setAttribute("aria-pressed",String(k===i)));
      $("#spoken").textContent=beat.text;
    }
    map.seek(Math.max(0,Math.min(1,(position-beat.start)/(beat.end-beat.start))));
    const seconds=position-scene.beats[0].start;
    range.value=String(Math.round(1000*seconds/(scene.beats.at(-1).end-scene.beats[0].start)));
    const clock=Math.floor(seconds/60)+":"+String(Math.floor(seconds)%60).padStart(2,"0");
    $("#elapsed").value=clock;range.setAttribute("aria-valuetext",clock+" · "+beat.focus);
  }
  matchMedia("(max-width:640px)").addEventListener("change",()=>{
    if(!scene || activeBeat<0)return;
    map=ShopMap.mount($("#map"),scene.id,activeBeat);
    showTime(position);
  });
  function tick() {
    showTime(audio.currentTime);
    if(position>=scene.beats.at(-1).end-.06){pause();return;}
    if(!audio.paused)frame=requestAnimationFrame(tick);
  }
  function jump(i,fraction=0) {
    cancelAnimationFrame(ghost);
    const beat=scene.beats[i];showTime(beat.start+(beat.end-beat.start)*fraction);
    audio.currentTime=position;
  }
  listen.onclick=async()=>{
    if(!audio.paused){pause();return;}
    cancelAnimationFrame(ghost);
    const token=++pendingPlay;
    if(position>=scene.beats.at(-1).end-.08)showTime(scene.beats[0].start);
    try{
      await prepareMedia(audio);
      if(token!==pendingPlay)return;
      audio.currentTime=position;
      await audio.play();
      if(token!==pendingPlay)return;
      listen.textContent="Ⅱ Pause";frame=requestAnimationFrame(tick);
    }catch{if(token===pendingPlay)listen.textContent="Retry playback";}
  };
  audio.addEventListener("ended",()=>{showTime(scene.beats.at(-1).end);pause();});
  range.oninput=()=>{
    cancelAnimationFrame(ghost);
    const start=scene.beats[0].start;
    showTime(start+(scene.beats.at(-1).end-start)*Number(range.value)/1000);audio.currentTime=position;
  };
  $("#mute").onclick=()=>{
    audio.muted=!audio.muted;
    $("#mute").setAttribute("aria-pressed",String(audio.muted));$("#mute").textContent=audio.muted?"Unmute":"Mute";
    if(audio.paused)listen.textContent=audio.muted?"▶ Play animation":"▶ Play with narration";
  };
  function load(nextScene){
    pause();scene=nextScene;activeBeat=-1;$("#beat-buttons").replaceChildren();
    for(const [i,beat] of scene.beats.entries()){
      const button=document.createElement("button");button.className="pick";button.textContent=beat.focus;
      button.onclick=()=>jump(i);$("#beat-buttons").append(button);
    }
    showTime(scene.beats[0].start);
    // Brief silent preview. The narration always starts at the chapter opening.
    if(!matchMedia("(prefers-reduced-motion: reduce)").matches){
      const start=performance.now();
      const preview=now=>{const p=Math.min(1,(now-start)/2900);map.seek(p*.6);if(p<1)ghost=requestAnimationFrame(preview);};
      ghost=requestAnimationFrame(preview);
    }
  }
  return {load,jump,pause};
})();
prepareMedia($("#narrator"));
show();
