'use strict';
(() => {
  const $ = (s, p = document) => p.querySelector(s);
  const $$ = (s, p = document) => [...p.querySelectorAll(s)];
  const sources = [
    {id:'graph-lenses',type:'RESEARCH PAPER',year:'2010',title:'Bidirectionalizing Graph Transformations',url:'https://zhenjiang888.github.io/pub/icfp10a.pdf',note:'Forward graph views, backward updates and well-behaved bidirectional transformations.',tags:['transformation']},
    {id:'bijective-lenses',type:'RESEARCH PAPER',year:'2018',title:'Synthesizing Bijective Lenses',url:'https://www.cs.princeton.edu/~dpw/papers/synthesizing-lenses-popl2018.pdf',note:'Foundations for bijective lenses and invertible mappings; examples concern data formats.',tags:['transformation']},
    {id:'actionnex', type:'RESEARCH PREPRINT', year:'2026', title:'ActionNex: A Virtual Outage Manager for Cloud Computing', url:'https://arxiv.org/abs/2604.03512', note:'Outage context, critical events and human-assisted action recommendations.', tags:['observability']},
    {id:'stream-model', type:'RESEARCH PAPER', year:'2016 / 2018', title:'Foundations for Streaming Model Transformations by Complex Event Processing', url:'https://link.springer.com/article/10.1007/s10270-016-0533-1', note:'Incremental model transformations and complex events.', tags:['transformation','streaming']},
    {id:'software-arch', type:'RESEARCH PAPER', year:'2002', title:'A graph transformation approach to software architecture reconfiguration', url:'https://pure.royalholloway.ac.uk/en/publications/a-graph-transformation-approach-to-software-architecture-reconfig/', note:'Graph transformation applied to software architecture.', tags:['transformation']},
    {id:'tgg-roadmap', type:'RESEARCH SURVEY', year:'2016', title:'20 years of triple graph grammars: a roadmap for future research', url:'https://research.chalmers.se/en/publication/501213', note:'Source, correspondence and target models; synchronization.', tags:['transformation']},
    {id:'tgg-version', type:'RESEARCH PAPER', year:'2024', title:'Incremental model transformations with triple graph grammars for multi-version models', url:'https://link.springer.com/article/10.1007/s10270-024-01238-1', note:'Incremental synchronization across model versions.', tags:['transformation','streaming']},
    {id:'tgg-correct', type:'RESEARCH PAPER', year:'2013 / 2015', title:'Model synchronization based on triple graph grammars: correctness, completeness and invertibility', url:'https://orbilu.uni.lu/handle/10993/4552', note:'Formal properties of model synchronization.', tags:['transformation']},
    {id:'multi-view', type:'RESEARCH PAPER', year:'2016', title:'Towards the Propagation of Model Updates along different Views in Multi-View Models', url:'https://orbilu.uni.lu/handle/10993/26582', note:'Prior work on updating multiple overlapping views.', tags:['transformation','ontology']},
    {id:'ontology-change', type:'RESEARCH PAPER', year:'2015', title:'Algebraic graph transformations for formalizing ontology changes and evolving ontologies', url:'https://www.sciencedirect.com/science/article/abs/pii/S0950705114003748', note:'Graph transformations and ontology evolution.', tags:['ontology','transformation']},
    {id:'raphtory', type:'RESEARCH PAPER', year:'2020', title:'Raphtory: Streaming Analysis of Distributed Temporal Graphs', url:'https://richardclegg.org/steer_raphtory_streaming_2020', note:'Streaming analysis of temporal graph history.', tags:['streaming']},
    {id:'memory', type:'RESEARCH PAPER', year:'2021', title:'Controlling Memory Footprint of Stateful Streaming Graph Processing', url:'https://www.usenix.org/conference/atc21/presentation/vaziri', note:'Memory management for stateful graph streaming.', tags:['streaming']},
    {id:'timely', type:'RESEARCH PAPER', year:'2013', title:'Incremental, iterative data processing with timely dataflow', url:'https://research.google/pubs/incremental-iterative-data-processing-with-timely-dataflow/', note:'Incremental and iterative distributed data processing.', tags:['streaming']},
    {id:'dataflow', type:'RESEARCH PAPER', year:'2015', title:'The Dataflow Model', url:'https://research.google/pubs/the-dataflow-model-a-practical-approach-to-balancing-correctness-latency-and-cost-in-massive-scale-unbounded-out-of-order-data-processing/', note:'Correctness, latency and cost for unbounded, out-of-order data.', tags:['streaming']},
    {id:'prism', type:'RESEARCH PAPER', year:'2026', title:'PRISM: Cross-Layer Observability Framework for Mobile Core Networks', url:'https://repository.tudelft.nl/record/uuid:799f60ab-0b2c-42d7-8768-7c7d389ca9e0', note:'Ontology-driven, cross-layer observability.', tags:['observability','ontology']},
    {id:'aiops', type:'RESEARCH PAPER', year:'2026', title:'Toward context-aware anomaly detection for AIOps in microservices using dynamic knowledge graphs', url:'https://biblio.ugent.be/publication/01KGFFQAQX5QV2K3CADQ4TR04F', note:'Dynamic knowledge graphs for anomaly context.', tags:['observability']},
    {id:'logs-llm', type:'RESEARCH PAPER', year:'2025', title:'From Logs to Knowledge: LLM-Powered Dynamic Knowledge Graphs for Real-Time Cloud Observability', url:'https://thesai.org/Publications/ViewPaper?Code=IJACSA&Issue=10&SerialNo=3&Volume=16', note:'A published approach to extracting graph knowledge from logs.', tags:['observability']},
    {id:'infer', type:'RESEARCH PREPRINT', year:'2024', title:'Automated Inference of Graph Transformation Rules', url:'https://arxiv.org/abs/2404.02692', note:'Research on inferring graph transformation rules.', tags:['transformation']},
    {id:'quine', type:'TECHNICAL DOCUMENTATION', year:'CURRENT', title:'Quine: Streaming Graph Architecture', url:'https://quine.io/core-concepts/architecture/', note:'Architecture documentation for streaming graph processing.', tags:['streaming','observability']},
    {id:'smartscape', type:'PRODUCT DOCUMENTATION', year:'CURRENT', title:'Dynatrace Smartscape topology', url:'https://docs.dynatrace.com/docs/analyze-explore-automate/smartscape/smartscape-in-context-views/smartscape-view-topology', note:'A product example of connected observability topology.', tags:['observability']},
    {id:'backstage', type:'PRODUCT DOCUMENTATION', year:'CURRENT', title:'Backstage software catalog: system model', url:'https://backstage.io/docs/features/software-catalog/system-model/', note:'A software catalog model for systems and components.', tags:['observability','ontology']}
  ];
  const byId = Object.fromEntries(sources.map(s => [s.id,s]));
  const sourceLinks = ids => ids.map(id => byId[id]).filter(Boolean).map(s => `<a class="research-link" href="${s.url}" target="_blank" rel="noopener noreferrer"><b>${s.title}</b><small>${s.type} · ${s.year} <span aria-hidden="true">↗</span></small></a>`).join('');

  const mapNodes = [
    {id:'impact', label:'Understand impact', x:25, y:9, title:'Understand impact', copy:'Connect changes in infrastructure and services to the capabilities they support.', sources:['prism','aiops','smartscape','actionnex','backstage'], explore:[['examples.html#detection','See the detection examples'],['examples.html#payment-path','See the payment capability'],['#topics','Explore the topic map']]},
    {id:'response', label:'Incident assistance', x:67, y:9, title:'Incident assistance', copy:'ActionNex summarizes outage context and recommends actions to people. Graph snapshots and diffs could provide extra context.', sources:['actionnex','aiops','prism'], explore:[['#actionnex','Read about ActionNex'],['#papers','Browse all research sources']]},
    {id:'ontology', label:'Multiple ontologies', x:23.5, y:29, title:'Multiple ontologies', copy:'Ontologies define the types, relationships and properties used in a domain. The engine supports multiple ontologies; health modelling is one example. Each ontology has a version.', sources:['ontology-change','prism','backstage','multi-view'], explore:[['examples.html#identity','Try the identity example'],['examples.html#explorer','Explore the commerce and clinical examples'],['#topics','Explore the topic map']]},
    {id:'views', label:'Multiple graph views', x:43, y:29, title:'Multiple graph views', copy:'Each projection creates its own graph. Several projections can use the same inputs or another projection’s output.', sources:['multi-view','tgg-roadmap','stream-model','tgg-version','graph-lenses'], explore:[['examples.html#explorer','Explore the output graphs'],['#priorities','Choose research priorities'],['#topics','Explore the topic map'],['#papers','Browse all research sources']]},
    {id:'transforms', label:'Graph transformations', x:44, y:49, title:'Graph transformations', copy:'Rules select and transform graph entities, relationships and attributes.', sources:['software-arch','tgg-version','tgg-correct','infer','tgg-roadmap','multi-view','graph-lenses','bijective-lenses'], explore:[['#transformation','Try the TGG example'],['examples.html#propagation','Try attribute propagation'],['examples.html#how-it-works','See the transformation overview']]},
    {id:'replay', label:'Deterministic replay', x:35.5, y:68, title:'Deterministic replay', copy:'A fixed projection, initial state, input order and time schedule define the result of a replay.', sources:['dataflow','raphtory','tgg-version','stream-model'], explore:[['examples.html#replay','Try a projection update'],['examples.html#memory','See retained inputs'],['evaluation.html','Open the test design']]},
    {id:'stream', label:'Streaming graph processing', x:61, y:68, title:'Streaming graph processing', copy:'Process records and update graphs as inputs arrive.', sources:['stream-model','quine','timely','memory','dataflow','raphtory'], explore:[['#transformation','Explore the graph example'],['evaluation.html','See the test design'],['#papers','Browse all research sources']]},
    {id:'ingest', label:'Telemetry & logs', x:76.5, y:83, title:'Telemetry and logs', copy:'The engine can process cloud records, network flows, telemetry and logs. The available inputs determine what it can show.', sources:['logs-llm','smartscape','prism','actionnex','aiops','quine'], explore:[['examples.html#how-it-works','See how data is processed'],['#topics','Explore the topic map'],['#papers','Browse all research sources']]},
    {id:'runtime', label:'Cloud runtime', x:90, y:95, title:'Cloud runtime', copy:'Compute, networking and storage provide the infrastructure for graph processing.', sources:['memory','timely','quine'], explore:[['#transformation','Try the large graph example'],['evaluation.html','See the test design'],['#priorities','Choose research priorities']]}
  ];
  const mapConnections=[['impact','ontology'],['impact','views'],['ontology','transforms'],['views','transforms'],['transforms','replay'],['transforms','stream'],['replay','stream'],['stream','ingest'],['ingest','runtime'],['impact','response'],['response','ingest']];
  window.atlasContentModel={mapNodes,sources};
  const guideSteps = [
    {node:'ingest',title:'Receive data',copy:'The engine receives structured and unstructured data, logs and telemetry as inputs.',link:'examples.html#how-it-works',linkText:'See the inputs'},
    {node:'ingest',title:'Create a rule for a new format',copy:'AI can analyze a new format and propose a mapping. A fixed rule then processes later inputs in that format.',link:'examples.html#how-it-works',linkText:'See how data is processed'},
    {node:'transforms',title:'Find entities and relationships',copy:'Inputs can describe entities, their attributes and their relationships. A relationship can also carry data, such as a failure signal.',link:'examples.html#explorer',linkText:'Explore the graph'},
    {node:'transforms',title:'Choose identity fields',copy:'A projection selects the fields that identify its entities. For example, it can group several pods under one deployment.',link:'examples.html#identity',linkText:'Try the identity example'},
    {node:'ontology',title:'Choose an ontology',copy:'An ontology defines the meaning of entity types, relationships and attributes. Ontologies are versioned.',link:'examples.html#model',linkText:'See identity and ontology'},
    {node:'transforms',title:'Configure a projection',copy:'A projection is a configuration graph. It defines transformation rules and can include a scaffold of expected structure.',link:'examples.html#how-it-works',linkText:'See the projection overview'},
    {node:'views',title:'Create several graph views',copy:'Different projections can process the same inputs at the same time. Their output graphs may contain overlapping entities and relationships.',link:'examples.html#explorer',linkText:'Explore the views'},
    {node:'transforms',title:'Propagate attributes',copy:'Rules can pass attributes between graph elements, including up to services and capabilities or down to their components.',link:'examples.html#propagation',linkText:'Try rollup and rolldown'},
    {node:'views',title:'Connect projections',copy:'A projection can consume raw inputs, another graph, or graph-change events. One projection can check the output of another.',link:'examples.html#how-it-works',linkText:'See the projection overview'},
    {node:'views',title:'Emit graph changes',copy:'Each projection produces a graph and graph-change events. An input that leaves the graph unchanged produces no diff. Operational telemetry is a separate stream.',link:'evaluation.html',linkText:'See expected outputs'},
    {node:'replay',title:'Keep recent inputs',copy:'Each graph element keeps its most recent N inputs that changed it. An input with no graph change does not enter that history.',link:'examples.html#memory',linkText:'Try the history example'},
    {node:'impact',title:'Detect changes and problems',copy:'Graph changes and signals can reveal new or failing dependencies, instability and recurring behavior.',link:'examples.html#detection',linkText:'See detection examples'},
    {node:'impact',title:'Connect systems to a capability',copy:'An example maps Kubernetes, Azure and VM components into payment services and the capability to process payments.',link:'examples.html#payment-path',linkText:'Explore the payment example'},
    {node:'ontology',title:'Use a clinical trial ontology',copy:'With supplied trial context, a view can connect technical systems and ML pipelines to trials and epochs.',link:'examples.html#explorer',linkText:'Open the graph and select Clinical trial'},
    {node:'response',title:'Connect to incident assistance',copy:'Graph snapshots and diffs could provide context to a tool such as ActionNex. That connection is a proposed integration.',link:'#actionnex',linkText:'Read about ActionNex'},
    {node:'replay',title:'Replay a projection update',copy:'When a projection changes, the previous graph is its first input. Retained inputs are then replayed with the updated rules.',link:'examples.html#replay',linkText:'Try a projection update'},
    {node:'replay',title:'Check determinism',copy:'With the same initial state, projection, input order and time schedule, a replay should produce the same graph and graph-change events.',link:'evaluation.html',linkText:'See the determinism tests'},
    {node:'transforms',title:'Compare with TGG research',copy:'Triple graph grammars describe consistency between source, correspondence and target graphs. The example illustrates those ideas.',link:'#transformation',linkText:'Try the TGG example'},
    {node:'stream',title:'Process updates as they arrive',copy:'The engine processes events and updates its projections as new information arrives.',link:'examples.html#how-it-works',linkText:'See the processing flow'},
    {node:'runtime',title:'Test correctness and scale',copy:'Fix a workload and compare graph snapshots and event streams. Measure throughput, latency and memory for that workload.',link:'evaluation.html',linkText:'Open the test design'},
    {node:'views',title:'Choose research priorities',copy:'Compare the proposed work with prior research, then put the next questions in order.',link:'#priorities',linkText:'Choose priorities'}
  ];
  window.atlasContentModel.guideSteps=guideSteps;
  let guideIndex=0;
  try{const saved=Number(sessionStorage.getItem('health-graph-guide-step'));if(Number.isInteger(saved)&&saved>=0&&saved<guideSteps.length)guideIndex=saved;}catch(e){}
  const pad=n=>String(n).padStart(2,'0');
  const motion=()=>window.matchMedia('(prefers-reduced-motion: reduce)').matches?'auto':'smooth';
  const shortMapLabels={views:'Graph views',transforms:'Transform graphs',stream:'Stream processing'};
  $('#wardley-nodes').innerHTML=mapNodes.map(n=>`<button type="button" class="map-node" aria-label="${n.label}" data-map="${n.id}" aria-controls="wardley-detail" style="--map-x:${n.x}%;left:${n.x}%;top:${n.y}%"><span class="map-label">${n.label}</span><span class="map-label-short" aria-hidden="true">${shortMapLabels[n.id]||n.label}</span></button>`).join('');
  function drawMapConnections(){
    const svg=$('.wardley-lines'),area=svg.getBoundingClientRect();if(!area.width||!area.height)return;
    const point=(x,y)=>`${((x-area.left)/area.width*1000).toFixed(2)} ${((y-area.top)/area.height*470).toFixed(2)}`;
    const mobile=window.matchMedia('(max-width:760px)').matches;
    const paths=mapConnections.map(([from,to])=>{
      const a=$(`[data-map="${from}"]`).getBoundingClientRect(),b=$(`[data-map="${to}"]`).getBoundingClientRect();
      const ax=a.left+a.width/2,ay=a.top+a.height/2,bx=b.left+b.width/2,by=b.top+b.height/2,dx=bx-ax,dy=by-ay;
      const start=Math.min(a.width/2/Math.abs(dx||.00001),a.height/2/Math.abs(dy||.00001));
      const end=Math.min(b.width/2/Math.abs(dx||.00001),b.height/2/Math.abs(dy||.00001));
      let x1=ax+dx*start,y1=ay+dy*start,x2=bx-dx*end,y2=by-dy*end;
      // Keep every connection straight and outside unrelated boxes on narrow screens.
      if(mobile){
        if(from==='impact'&&to==='ontology'){x1=a.left+8;y1=a.bottom;x2=bx;y2=b.top;}
        if(from==='views'&&to==='transforms'){x1=a.right;y1=ay;x2=b.right;y2=b.top;}
        if(from==='transforms'&&to==='stream'){x1=a.right;y1=ay;x2=b.right-8;y2=b.top;}
        if(from==='response'&&to==='ingest'){x1=a.right;y1=ay;x2=b.right;y2=b.top;}
      }
      const path=document.createElementNS('http://www.w3.org/2000/svg','path');
      path.dataset.from=from;path.dataset.to=to;path.setAttribute('d',`M${point(x1,y1)} L${point(x2,y2)}`);return path;
    });svg.replaceChildren(...paths);
  }
  const redrawMap=()=>requestAnimationFrame(drawMapConnections);window.addEventListener('resize',redrawMap);new ResizeObserver(redrawMap).observe($('#wardley-map'));document.fonts?.ready.then(redrawMap);redrawMap();
  $('#guide-numbers').innerHTML=guideSteps.map((step,i)=>`<button type="button" data-guide-step="${i}" aria-label="Step ${i+1}: ${step.title}" title="${step.title}">${pad(i+1)}</button>`).join('');
  function selectMap(id){
    const n=mapNodes.find(x=>x.id===id);
    $$('.map-node').forEach(b=>{const selected=b.dataset.map===id;b.classList.toggle('selected',selected);b.setAttribute('aria-pressed',String(selected));});
    const stepLinks=guideSteps.map((step,i)=>({step,i})).filter(x=>x.step.node===id).map(x=>`<button type="button" data-map-guide-step="${x.i}">${pad(x.i+1)} · ${x.step.title}</button>`).join('');
    const pageLinks=n.explore.map(([url,label])=>`<a href="${url}">${label} →</a>`).join('');
    $('#wardley-detail').innerHTML=`<h3>${n.title}</h3><p>${n.copy}</p><details class="map-explore"><summary>Show guide steps and examples</summary><div class="guide-step-links"><span>GUIDE STEPS</span>${stepLinks}<span>EXAMPLES AND TESTS</span>${pageLinks}</div></details><details class="inspector-sources"><summary>Related research and sources (${n.sources.length})</summary>${sourceLinks(n.sources)}</details>`;
    $('#map-topic').value=id; $('#wardley-detail').scrollTop=0; document.dispatchEvent(new CustomEvent('atlas-map-selected',{detail:id}));
  }
  function selectGuide(index,showGuide=false){
    guideIndex=Math.max(0,Math.min(guideSteps.length-1,index));try{sessionStorage.setItem('health-graph-guide-step',String(guideIndex));}catch(e){}const step=guideSteps[guideIndex];
    $('#guide-number').textContent=pad(guideIndex+1);$('#guide-label').textContent=`STEP ${guideIndex+1} OF ${guideSteps.length}`;
    $('#guide-title').textContent=step.title;$('#guide-description').textContent=step.copy;$('#guide-link').textContent=`${step.linkText} →`;$('#guide-link').setAttribute('href',step.link);
    $('#guide-prev').disabled=guideIndex===0;$('#guide-next').disabled=guideIndex===guideSteps.length-1;
    $$('#guide-numbers button').forEach((b,i)=>{const active=i===guideIndex;b.classList.toggle('active',active);if(active)b.setAttribute('aria-current','step');else b.removeAttribute('aria-current');});
    selectMap(step.node); document.dispatchEvent(new CustomEvent('atlas-guide-selected',{detail:{index:guideIndex,title:step.title,total:guideSteps.length}}));
    const selected=$(`[data-map="${step.node}"]`),scroll=$('.wardley-scroll');if(scroll.scrollWidth>scroll.clientWidth)scroll.scrollTo({left:selected.offsetLeft-scroll.clientWidth/2,behavior:motion()});
    const number=$(`[data-guide-step="${guideIndex}"]`),numbers=$('#guide-numbers');if(numbers.scrollWidth>numbers.clientWidth)numbers.scrollTo({left:number.offsetLeft-numbers.offsetLeft-numbers.clientWidth/2,behavior:motion()});
    if(showGuide){$('#guide-panel').open=true;$('#engine-guide').scrollIntoView({behavior:motion(),block:'start'});}
  }
  $('#guide-prev').addEventListener('click',()=>selectGuide(guideIndex-1));
  $('#guide-next').addEventListener('click',()=>selectGuide(guideIndex+1));
  $('#guide-numbers').addEventListener('click',e=>{const b=e.target.closest('[data-guide-step]');if(b)selectGuide(Number(b.dataset.guideStep));});
  $('#wardley-detail').addEventListener('click',e=>{const b=e.target.closest('[data-map-guide-step]');if(b)selectGuide(Number(b.dataset.mapGuideStep),true);});
  $('#wardley-nodes').addEventListener('click',e=>{const b=e.target.closest('[data-map]');if(b){selectMap(b.dataset.map);location.hash='explore/'+b.dataset.map}});
  $('#map-topic').innerHTML=mapNodes.map((n,i)=>`<option value="${n.id}">${n.label}</option>`).join('');
  $('#map-topic').addEventListener('change',e=>{if(e.target.value){selectMap(e.target.value);location.hash='explore/'+e.target.value}});
  const returnMap=$('#return-map');
  if('IntersectionObserver'in window)new IntersectionObserver(entries=>{returnMap.hidden=entries[0].isIntersecting;},{threshold:0}).observe($('#wardley-map'));
  selectGuide(guideIndex); selectMap('impact');

  const topics = [
    {id:'engine',label:'THE ENGINE',x:50,y:48,center:true,title:'Structure and telemetry',copy:'Incoming records and graph changes feed configured projections. Each projection defines a view and can emit its own graph and changes.',sources:['stream-model','quine']},
    {id:'inputs',label:'Inputs & signals',x:13.5,y:23,title:'Inputs and signals',copy:'Infrastructure records, network flows, logs and telemetry contribute observations. Source coverage determines what can be seen.',sources:['logs-llm','prism']},
    {id:'incident',label:'Incident response',x:14,y:46,title:'Incident response',copy:'ActionNex turns operational context into critical events and recommendations. A graph snapshot or diff could provide additional context; the integration remains a proposal.',sources:['actionnex','prism']},
    {id:'identity',label:'Identity',x:40,y:13.5,title:'Identity',copy:'A projection selects the fields used to identify an entity. A pod-level view and deployment-level view can group the same records differently.',sources:['backstage','tgg-correct']},
    {id:'ontology',label:'Ontologies',x:76,y:21,title:'Ontologies',copy:'Ontologies describe types, relationships and properties. Versions make changes in meaning explicit.',sources:['ontology-change','prism']},
    {id:'views',label:'Many graph views',x:85,y:54,title:'Many graph views',copy:'Different projections can produce different graphs from shared data; graph outputs and diffs can feed other projections.',sources:['multi-view','stream-model']},
    {id:'tgg',label:'TGG & consistency',x:72,y:83,title:'TGG and consistency',copy:'A triple graph grammar relates source, correspondence and target graphs. Derived rules can synchronize changes between models.',sources:['tgg-roadmap','tgg-correct','tgg-version']},
    {id:'replay',label:'Replay & time',x:32,y:84,title:'Replay and time',copy:'An ordered input sequence, rules, initial state and time schedule provide the basis for reproducible checkpoints.',sources:['dataflow','raphtory']},
    {id:'scale',label:'Scale & evaluation',x:13.5,y:66,title:'Scale and evaluation',copy:'Measure throughput, update latency, memory, graph size and replay cost with published workloads and clear correctness checks.',sources:['memory','timely','tgg-version']}
  ];
  $('#topic-nodes').innerHTML=topics.map(n=>`<button type="button" class="topic-node ${n.center?'center':''}" style="--map-x:${n.x}%;left:${n.x}%;top:${n.y}%" data-topic="${n.id}">${n.label}</button>`).join('');
  function selectTopic(id){const n=topics.find(x=>x.id===id);$$('.topic-node').forEach(b=>{let on=b.dataset.topic===id;b.classList.toggle('selected',on);b.setAttribute('aria-pressed',String(on))});$('#topic-detail').innerHTML=`<span class="topic-number">TOPIC / ${String(topics.indexOf(n)+1).padStart(2,'0')}</span><h3>${n.title}</h3><p>${n.copy}</p><div class="inspector-sources"><span>READ MORE</span>${sourceLinks(n.sources)}</div>`;}
  $('#topic-nodes').addEventListener('click',e=>{const b=e.target.closest('[data-topic]');if(b)selectTopic(b.dataset.topic)});
  selectTopic('engine');

  const priorities = [
    {id:'graphs',title:'Multiple output graphs',detail:'Independent, simultaneous projection results'},
    {id:'ontologies',title:'Multiple ontologies',detail:'Versioned meanings across domain contexts'},
    {id:'composition',title:'Composable projections',detail:'Graph configurations, graph and diff inputs'},
    {id:'identity',title:'Projection-defined identity',detail:'Select identity fields for each view'},
    {id:'determinism',title:'Deterministic replay',detail:'Fixed inputs, projection, initial state and time'},
    {id:'diffs',title:'Graph changes as events',detail:'Observable additions, updates and removals'},
    {id:'propagation',title:'Attribute propagation',detail:'Defined rules for rollup and rolldown'},
    {id:'history',title:'Bounded evidence',detail:'Recent N graph-changing inputs per element'},
    {id:'format',title:'Learn a format, use a fixed rule',detail:'Separate AI-assisted mapping from deterministic execution'},
    {id:'evaluation',title:'Correctness and scale evaluation',detail:'Publish workloads, benchmarks and reproducible tests'}
  ];
  const priorityKey='health-graph-roadmap-priority-v1';
  let order=priorities.map(p=>p.id);
  try {const saved=JSON.parse(localStorage.getItem(priorityKey)||'null');if(Array.isArray(saved)&&saved.length===order.length&&new Set(saved).size===order.length&&saved.every(id=>order.includes(id)))order=saved}catch(e){}
  function saveOrder(){try{localStorage.setItem(priorityKey,JSON.stringify(order))}catch(e){}}
  function renderPriority(){
    $('#priority-list').innerHTML=order.map((id,i)=>{const p=priorities.find(x=>x.id===id);return `<li class="priority-item" data-id="${id}"><button class="drag-handle" type="button" aria-label="Drag ${p.title}" title="Drag to reorder">⠿</button><span class="priority-number">${String(i+1).padStart(2,'0')}</span><div class="priority-text"><b>${p.title}</b><small>${p.detail}</small></div><div class="priority-buttons"><button type="button" data-direction="-1" aria-label="Move ${p.title} up" ${i===0?'disabled':''}>↑</button><button type="button" data-direction="1" aria-label="Move ${p.title} down" ${i===order.length-1?'disabled':''}>↓</button></div></li>`}).join('');
  }
  $('#priority-list').addEventListener('click',e=>{const b=e.target.closest('[data-direction]');if(!b)return;const li=b.closest('[data-id]');const i=order.indexOf(li.dataset.id),next=i+Number(b.dataset.direction);if(next<0||next>=order.length)return;[order[i],order[next]]=[order[next],order[i]];saveOrder();renderPriority();const newItem=$(`[data-id="${li.dataset.id}"]`);const newButton=$(`[data-direction="${b.dataset.direction}"]`,newItem);if(newButton&&!newButton.disabled)newButton.focus();else $('.drag-handle',newItem).focus();$('#priority-announcement').textContent=`${priorities.find(p=>p.id===li.dataset.id).title} moved to position ${next+1}.`;});
  $('#priority-reset').addEventListener('click',()=>{order=priorities.map(p=>p.id);saveOrder();renderPriority();$('#priority-announcement').textContent='Default order restored.'});
  let drag=null;
  $('#priority-list').addEventListener('pointerdown',e=>{const h=e.target.closest('.drag-handle');if(!h)return;const li=h.closest('.priority-item');drag={id:li.dataset.id,li,handle:h,pointer:e.pointerId,moved:false};h.setPointerCapture(e.pointerId);li.classList.add('dragging');e.preventDefault();});
  $('#priority-list').addEventListener('pointermove',e=>{if(!drag||e.pointerId!==drag.pointer)return;drag.moved=true;const others=$$('.priority-item',$('#priority-list')).filter(x=>x!==drag.li);let anchor=others.find(x=>e.clientY<x.getBoundingClientRect().top+x.getBoundingClientRect().height/2);if(anchor)$('#priority-list').insertBefore(drag.li,anchor);else $('#priority-list').appendChild(drag.li);e.preventDefault();});
  function endPriority(e){if(!drag||e.pointerId!==drag.pointer)return;const id=drag.id;drag.li.classList.remove('dragging');order=$$('.priority-item',$('#priority-list')).map(li=>li.dataset.id);saveOrder();renderPriority();if(drag.moved)$('#priority-announcement').textContent=`${priorities.find(p=>p.id===id).title} moved to position ${order.indexOf(id)+1}.`;drag=null;}
  $('#priority-list').addEventListener('pointerup',endPriority);$('#priority-list').addEventListener('pointercancel',endPriority);
  renderPriority();

  function renderPapers(filter='all'){$('#paper-list').innerHTML=sources.filter(s=>filter==='all'||s.tags.includes(filter)).map(s=>`<article class="paper-card"><span class="paper-type">${s.type} / ${s.year}</span><a href="${s.url}" target="_blank" rel="noopener noreferrer">${s.title} ↗</a><p>${s.note}</p><span class="paper-topic">${s.tags.map(x=>x.toUpperCase()).join(' / ')}</span></article>`).join('');}
  $$('.paper-filter button').forEach(b=>b.addEventListener('click',()=>{$$('.paper-filter button').forEach(x=>{x.classList.toggle('active',x===b);x.setAttribute('aria-pressed',String(x===b))});renderPapers(b.dataset.filter)}));renderPapers();

  // Synthetic graph: each correspondence node relates one source to one target.
  const canvas=$('#graph-canvas'), ctx=canvas.getContext('2d');
  const slider=$('#time-slider'), size=$('#size-select');
  const stageTitles=['Source arrives','Health view','Trial view','Signal changes'];
  const stageCopy=[
    'Workloads arrive with identifiers and supplied mapping context.',
    'A health rule groups workloads into services and maps dependencies.',
    'A separate rule uses supplied context to map workloads to trial epochs.',
    'A source signal changes. The health rule updates affected services.'
  ];
  let stage=0,n=Number(size.value),zoom=1,panX=0,panY=0,cssW=0,cssH=0,selected=null,pointer=null,needsFit=true;
  const C={source:'#846cd9',corr:'#d3a049',health:'#4ca786',trial:'#dc9572',line:'#a9beb2',bad:'#d98062'};
  function format(x){return x.toLocaleString('en-US')}
  function cols(){return n<=40?1:Math.min(35,Math.max(3,Math.ceil(Math.sqrt(n/15))))}
  function gap(){return n<=40?58:40}
  function nodeWidth(){return n<=40?134:57}
  function laneWidth(){return Math.max(300,cols()*67+100)}
  function graphWidth(){return 80+4*laneWidth()+(cols()-1)*67+nodeWidth()+80}
  function positions(i){const col=i%cols(),row=Math.floor(i/cols()),dx=col*67,y=92+row*gap(),w=laneWidth();return {health:{x:80+dx,y},hcorr:{x:80+w+dx,y},source:{x:80+2*w+dx,y},tcorr:{x:80+3*w+dx,y},trial:{x:80+4*w+dx,y}};}
  function graphHeight(){return 145+Math.ceil(n/cols())*gap()}
  function isAffected(i){return i%8===0}
  function healthCount(){return Math.ceil(n/4)}
  function trialCount(){return Math.ceil(n/8)}
  function healthPos(i){return positions(Math.floor(i/4)*4).health}
  function trialPos(i){return positions(Math.floor(i/8)*8).trial}
  function nodeKinds(){return ['source',...(stage>=1?['hcorr','health']:[]),...(stage>=2?['tcorr','trial']:[])]}
  function nodeExists(kind){return nodeKinds().includes(kind)}
  function resize(){const r=canvas.getBoundingClientRect();cssW=r.width;cssH=r.height;const dpr=Math.min(window.devicePixelRatio||1,2);canvas.width=Math.round(cssW*dpr);canvas.height=Math.round(cssH*dpr);ctx.setTransform(dpr,0,0,dpr,0,0);if(needsFit){fit();needsFit=false}else draw();}
  function fit(){const graphW=graphWidth(), graphH=graphHeight();zoom=Math.min((cssW-35)/graphW,(cssH-46)/graphH,1);zoom=Math.max(zoom,.0005);panX=(cssW-graphW*zoom)/2;panY=20;draw();}
  function rounded(x,y,w,h,r){ctx.beginPath();ctx.roundRect(x,y,w,h,r)}
  function arrow(ax,ay,bx,by){
    const dx=bx-ax,dy=by-ay,len=Math.hypot(dx,dy);if(len<1)return;const ux=dx/len,uy=dy/len;
    ctx.beginPath();ctx.moveTo(ax,ay);ctx.lineTo(bx-ux*7,by-uy*7);ctx.stroke();
    ctx.beginPath();ctx.moveTo(bx,by);ctx.lineTo(bx-ux*8-uy*3.8,by-uy*8+ux*3.8);ctx.lineTo(bx-ux*8+uy*3.8,by-uy*8-ux*3.8);ctx.closePath();ctx.fill();
  }
  function flowEdge(from,to){const w=nodeWidth(),h=25,dx=to.x-from.x,dy=to.y-from.y;let ax,ay,bx,by;if(Math.abs(dy)>Math.abs(dx)){ax=from.x+w/2;bx=to.x+w/2;if(dy>0){ay=from.y+h;by=to.y}else{ay=from.y;by=to.y+h}}else{ay=from.y+h/2;by=to.y+h/2;if(dx>0){ax=from.x+w;bx=to.x}else{ax=from.x;bx=to.x+w}}arrow(ax,ay,bx,by)}
  function drawNode(kind,p,i){
    const w=nodeWidth(),h=25, affected=stage>=3&&isAffected(i)&&(kind==='source'||kind==='health');
    const color=kind==='source'?C.source:kind==='hcorr'||kind==='tcorr'?C.corr:kind==='health'?C.health:C.trial;
    if(zoom<.08){ctx.fillStyle=affected?C.bad:color;ctx.fillRect(p.x,p.y,Math.max(w,1.7/zoom),Math.max(h,1.7/zoom));return}
    ctx.fillStyle='#ffffff';ctx.strokeStyle=affected?C.bad:color;ctx.lineWidth=affected?2:1.3;rounded(p.x,p.y,w,h,5);ctx.fill();ctx.stroke();
    ctx.fillStyle=affected?C.bad:color;
    if(kind==='hcorr'||kind==='tcorr'){ctx.strokeStyle=color;ctx.lineWidth=1.4;ctx.beginPath();ctx.arc(p.x+8.5,p.y+12.5,3,0,Math.PI*2);ctx.arc(p.x+12,p.y+12.5,3,0,Math.PI*2);ctx.stroke()}else if(kind==='source'){for(let a=0;a<2;a++)for(let b=0;b<2;b++)ctx.fillRect(p.x+6+a*4.2,p.y+8.5+b*4.2,3,3)}else if(kind==='health'){ctx.fillRect(p.x+8.8,p.y+8,3,9);ctx.fillRect(p.x+6,p.y+11,9,3)}else{ctx.beginPath();ctx.moveTo(p.x+8,p.y+8);ctx.lineTo(p.x+12,p.y+8);ctx.lineTo(p.x+12,p.y+12);ctx.lineTo(p.x+14,p.y+16);ctx.lineTo(p.x+6,p.y+16);ctx.lineTo(p.x+8,p.y+12);ctx.closePath();ctx.fill()}
    if(zoom>.27){ctx.font='600 9px sans-serif';ctx.fillStyle='#3d554a';const prefix=kind==='source'?'workload':kind==='health'?'service':kind==='trial'?'epoch':kind==='hcorr'?'health link':'trial link';const number=kind==='health'?Math.floor(i/4)+1:kind==='trial'?Math.floor(i/8)+1:i+1;const label=n<=40?`${prefix} ${String(number).padStart(2,'0')}`:`${kind==='source'?'W':kind==='health'?'S':kind==='trial'?'E':'C'}${number}`;ctx.fillText(label,p.x+20,p.y+16,w-24)}
    if(selected&&selected.kind===kind&&selected.i===i){ctx.strokeStyle='#8068e4';ctx.lineWidth=3;rounded(p.x-3,p.y-3,w+6,h+6,7);ctx.stroke()}
  }
  function draw(){
    if(!cssW||!cssH)return;const dpr=Math.min(window.devicePixelRatio||1,2);ctx.setTransform(dpr,0,0,dpr,0,0);ctx.clearRect(0,0,cssW,cssH);ctx.save();ctx.translate(panX,panY);ctx.scale(zoom,zoom);
    const lane=laneWidth();const header=[['HEALTH TARGET',80],['CORRESPONDENCE A',80+lane],['SOURCE',80+2*lane],['CORRESPONDENCE B',80+3*lane],['TRIAL TARGET',80+4*lane]];
    $('#graph-instruction').innerHTML=zoom<.08?'SAMPLED OVERVIEW · ZOOM IN FOR INDIVIDUAL NODES':'DRAG TO PAN <span>·</span> SELECT A NODE';
    ctx.font='600 11px monospace';ctx.fillStyle='#7c9184';header.forEach(([label,x])=>ctx.fillText(label,x,34));
    const first=Math.max(0,Math.floor(((-panY/zoom)-120)/gap())*cols()),last=Math.min(n,Math.ceil(((cssH-panY)/zoom+50)/gap())*cols());
    ctx.strokeStyle=C.line;ctx.fillStyle=C.line;ctx.lineWidth=1.2;
    const rowStride=zoom<.08?Math.max(1,Math.ceil(4/(gap()*zoom))):1;const indexes=[];for(let row=Math.floor(first/cols());row*cols()<last;row+=rowStride){for(let c=0;c<cols();c++){const i=row*cols()+c;if(i>=first&&i<last)indexes.push(i)}}
    for(const i of indexes){const p=positions(i);if(i%4===0&&i+4<n){flowEdge(p.source,positions(i+4).source);if(stage>=1)flowEdge(p.health,positions(i+4).health)}if(stage>=1){flowEdge(p.hcorr,healthPos(i));flowEdge(p.hcorr,p.source)}if(stage>=2){flowEdge(p.tcorr,p.source);flowEdge(p.tcorr,trialPos(i))}}
    for(const i of indexes){const p=positions(i);for(const kind of nodeKinds()){if(kind==='health'&&i%4!==0)continue;if(kind==='trial'&&i%8!==0)continue;drawNode(kind,p[kind],i)}}
    ctx.restore();
  }
  function updateText(){
    $('#time-title').textContent=stageTitles[stage];$('#time-copy').textContent=stageCopy[stage];$('#lab-stage').textContent=`0${stage+1} / 04`;
    $('#step-back').disabled=stage===0;$('#step-next').disabled=stage===3;
    const dep=Math.max(0,healthCount()-1);
    let events=[`${format(n)} WORKLOADS · ${format(dep)} ${dep===1?'DEPENDENCY':'DEPENDENCIES'} RECEIVED`,`+${format(healthCount())} SERVICES · +${format(n)} CORRESPONDENCES · +${format(2*n+dep)} LINKS`,`+${format(trialCount())} ${trialCount()===1?'EPOCH':'EPOCHS'} · +${format(n)} CORRESPONDENCES · +${format(2*n)} LINKS`,`${format(Math.ceil(n/8))} SOURCE SIGNALS CHANGED · ${format(Math.ceil(n/8))} SERVICES UPDATED`];
    $('#lab-event').textContent=events[stage];
    const corr=(stage>=1?n:0)+(stage>=2?n:0),targets=(stage>=1?healthCount():0)+(stage>=2?trialCount():0),links=dep+(stage>=1?2*n+dep:0)+(stage>=2?2*n:0);
    $('#lab-metrics').innerHTML=[[n+corr+targets,'TOTAL NODES'],[n,'SOURCE'],[corr,'CORRESPONDENCE'],[targets,'TARGET'],[links,'LINKS']].map(([x,label])=>`<div class="lab-metric"><b>${format(x)}</b><span>${label}</span></div>`).join('');
  }
  function update(shouldFit=false){stage=Number(slider.value);n=Number(size.value);selected=null;$('#node-inspector').innerHTML='<span>NODE INSPECTOR</span><b>Select a node in the graph</b><p>Correspondences connect workloads to services or trial epochs.</p>';updateText();updateInspectChoices();if(shouldFit)fit();else draw()}
  slider.addEventListener('input',()=>update());size.addEventListener('change',()=>update(true));
  $('#step-back').addEventListener('click',()=>{slider.value=Math.max(0,stage-1);update()});$('#step-next').addEventListener('click',()=>{slider.value=Math.min(3,stage+1);update()});
  function zoomAt(factor,x=cssW/2,y=cssH/2){const old=zoom;zoom=Math.max(.0005,Math.min(3,zoom*factor));const ratio=zoom/old;panX=x-(x-panX)*ratio;panY=y-(y-panY)*ratio;draw()}
  $('#zoom-in').addEventListener('click',()=>zoomAt(1.4));$('#zoom-out').addEventListener('click',()=>zoomAt(1/1.4));$('#zoom-fit').addEventListener('click',fit);
  canvas.addEventListener('wheel',e=>{e.preventDefault();const r=canvas.getBoundingClientRect();zoomAt(e.deltaY<0?1.15:1/1.15,e.clientX-r.left,e.clientY-r.top)},{passive:false});
  canvas.addEventListener('keydown',e=>{if(e.key==='+'||e.key==='='){e.preventDefault();zoomAt(1.4)}else if(e.key==='-'){e.preventDefault();zoomAt(1/1.4)}else if(e.key==='0'){e.preventDefault();fit()}else if(['ArrowUp','ArrowDown','ArrowLeft','ArrowRight'].includes(e.key)){e.preventDefault();if(e.key==='ArrowUp')panY+=60;if(e.key==='ArrowDown')panY-=60;if(e.key==='ArrowLeft')panX+=60;if(e.key==='ArrowRight')panX-=60;draw()}});
  canvas.addEventListener('pointerdown',e=>{pointer={id:e.pointerId,x:e.clientX,y:e.clientY,ox:e.clientX,oy:e.clientY,panX,panY};canvas.setPointerCapture(e.pointerId)});
  canvas.addEventListener('pointermove',e=>{if(!pointer||pointer.id!==e.pointerId)return;panX=pointer.panX+e.clientX-pointer.x;panY=pointer.panY+e.clientY-pointer.y;draw()});
  function showSelection(kind,i){
    selected={kind,i};const num=String(i+1).padStart(2,'0');let title,description;
    if(kind==='source'){title=`Workload ${num}`;description=`Source identifier: workload-${num}. ${stage>=3&&isAffected(i)?'The sample signal is degraded.':'The sample signal is normal.'}`}
    else if(kind==='health'){title=`Health service ${String(Math.floor(i/4)+1).padStart(2,'0')}`;description=`A target grouping up to four workloads. ${stage>=3&&isAffected(i)?'Its health is degraded by the example rule.':'Its health is normal.'}`}
    else if(kind==='trial'){title=`Trial epoch ${String(Math.floor(i/8)+1).padStart(2,'0')}`;description='An epoch grouping up to eight workloads, using the supplied trial context and the projection.'}
    else{title=kind==='hcorr'?`Health correspondence ${num}`:`Trial correspondence ${num}`;description=`Links workload-${num} to ${kind==='hcorr'?'service '+String(Math.floor(i/4)+1).padStart(2,'0'):'epoch '+String(Math.floor(i/8)+1).padStart(2,'0')}.`}
    $('#node-inspector').innerHTML=`<span>${kind==='source'?'SOURCE':kind==='health'?'HEALTH TARGET':kind==='trial'?'TRIAL TARGET':'CORRESPONDENCE'}</span><b>${title}</b><p>${description}</p>`;draw();
  }
  function inspect(clientX,clientY){
    const rect=canvas.getBoundingClientRect(),x=(clientX-rect.left-panX)/zoom,y=(clientY-rect.top-panY)/zoom;const row=Math.floor((y-92)/gap());if(row<0)return;
    for(let col=0;col<cols();col++){const i=row*cols()+col;if(i>=n)continue;const ps=positions(i);for(const kind of nodeKinds()){if(kind==='health'&&i%4!==0)continue;if(kind==='trial'&&i%8!==0)continue;const p=ps[kind];if(x>=p.x&&x<=p.x+nodeWidth()&&y>=p.y&&y<=p.y+25){showSelection(kind,i);return}}}
  }
  function updateInspectChoices(){
    const select=$('#inspect-kind');for(const option of select.options)option.disabled=!nodeExists(option.value);
    if(!nodeExists(select.value))select.value='source';const kind=select.value;const max=kind==='health'?healthCount():kind==='trial'?trialCount():n;
    const input=$('#inspect-number');input.max=String(max);input.value=String(Math.min(Math.max(Number(input.value)||1,1),max));
  }
  $('#inspect-kind').addEventListener('change',updateInspectChoices);
  $('#inspect-form').addEventListener('submit',e=>{
    e.preventDefault();updateInspectChoices();const kind=$('#inspect-kind').value;const k=Number($('#inspect-number').value)-1;const i=kind==='health'?k*4:kind==='trial'?k*8:k;
    const p=positions(i)[kind];zoom=Math.max(zoom,.65);panX=cssW/2-(p.x+nodeWidth()/2)*zoom;panY=cssH/2-(p.y+12.5)*zoom;showSelection(kind,i);
  });
  canvas.addEventListener('pointerup',e=>{if(!pointer||pointer.id!==e.pointerId)return;if(Math.hypot(e.clientX-pointer.ox,e.clientY-pointer.oy)<5)inspect(e.clientX,e.clientY);pointer=null});canvas.addEventListener('pointercancel',()=>pointer=null);
  if('ResizeObserver'in window)new ResizeObserver(resize).observe(canvas);else window.addEventListener('resize',resize);
  updateText();updateInspectChoices();resize();
})();
