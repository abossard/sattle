'use strict';
(async()=>{
 const $=(q,p=document)=>p.querySelector(q),$$=(q,p=document)=>[...p.querySelectorAll(q)];
 const esc=s=>String(s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const [story,land]=await Promise.all(['story.json','landscape.json'].map(u=>fetch(u).then(r=>{if(!r.ok)throw Error(u);return r.json()})));
 let explored=[];try{explored=JSON.parse(localStorage.getItem('sattle-explored')||'[]');if(!Array.isArray(explored))explored=[]}catch{}
 function remember(n){if(!explored.includes(n))explored.push(n);try{localStorage.setItem('sattle-explored',JSON.stringify(explored))}catch{}}
 const sourceLinks=ids=>ids.map(id=>land.sources.find(s=>s.id===id)).filter(Boolean).map(s=>`<a href="${esc(s.url)}" target="_blank" rel="noopener noreferrer">${esc(s.title)} ↗</a>`).join('');
 if($('#landscape-map')){
  const map=$('#landscape-map');const nodes=$('#landscape-nodes');
  const mapRoles={impact:'projection',response:'projection',ontology:'rule',views:'projection',transforms:'rule',replay:'diff',stream:'input',ingest:'input',runtime:'master'};
  const labels={views:'Graph views',stream:'Stream processing',impact:'Understand impact',response:'Incident response',ingest:'Telemetry & logs'};
  nodes.innerHTML=land.nodes.map(n=>`<button class="map-node role-${mapRoles[n.id]}" data-capability="${esc(n.id)}" style="left:${10+n.x*.83}%;top:${4+n.y*.86}%" aria-pressed="false">${esc(labels[n.id]||n.label)}</button>`).join('');
  $('#capability-select').innerHTML=land.nodes.map(n=>`<option value="${esc(n.id)}">${esc(labels[n.id]||n.label)}</option>`).join('');
  let selected='views';
  function draw(){const rect=map.getBoundingClientRect(),svg=$('.map-lines');svg.setAttribute('viewBox',`0 0 ${rect.width} ${rect.height}`);svg.innerHTML=land.edges.map(([a,b])=>{
    const box=id=>$(`[data-capability="${id}"]`).getBoundingClientRect();const aa=box(a),bb=box(b),ax=aa.left+aa.width/2-rect.left,ay=aa.top+aa.height/2-rect.top,bx=bb.left+bb.width/2-rect.left,by=bb.top+bb.height/2-rect.top;
    const dx=bx-ax,dy=by-ay;const intersect=(w,h)=>1/Math.max(Math.abs(dx)/(w/2),Math.abs(dy)/(h/2));const t=intersect(aa.width,aa.height),u=intersect(bb.width,bb.height);
    return `<path class="${a===selected||b===selected?'active':''}" d="M${ax+dx*t},${ay+dy*t} L${bx-dx*u},${by-dy*u}"/>`}).join('')}
  function select(id){const n=land.nodes.find(x=>x.id===id);if(!n)return;selected=id;$$('.map-node').forEach(b=>b.setAttribute('aria-pressed',String(b.dataset.capability===id)));$('#capability-select').value=id;
   const steps=story.filter(s=>s.topic===id);
   const copy=id==='views'?'The master graph feeds projections that can be stacked or combined. Each output has its own graph identity, revisions and ontology.':n.copy;
   $('#map-detail').innerHTML=`<div><p class="eyebrow">Selected capability</p><h3>${esc(labels[id]||n.title)}</h3><p>${esc(copy)}</p></div><div><p class="eyebrow">Explore the concept</p>${steps.length?steps.map(s=>`<a class="concept-link" href="step-${String(s.number).padStart(2,'0')}.html">${String(s.number).padStart(2,'0')} · ${esc(s.short)} →</a>`).join(''):'<a class="concept-link" href="research.html">Browse the research →</a>'}</div><div><p class="eyebrow">Related research</p>${sourceLinks(n.sources.slice(0,3))}</div>`;draw()}
  nodes.addEventListener('click',e=>{const b=e.target.closest('[data-capability]');if(b){select(b.dataset.capability);history.replaceState(null,'',`#capability-${b.dataset.capability}`)}});
  $('#capability-select').addEventListener('change',e=>select(e.target.value));new ResizeObserver(draw).observe(map);
  select(location.hash.startsWith('#capability-')?location.hash.replace('#capability-',''):'views');
  $('#guide-progress').textContent=`${explored.filter(n=>n>=1&&n<=21).length} of 21 explored`;
  $$('[data-step]').forEach(a=>{if(explored.includes(Number(a.dataset.step))){a.classList.add('explored');$('.step-arrow',a).textContent='✓'}});
 }
 const article=$('[data-lesson]');if(!article)return;
 const lesson=story[Number(article.dataset.lesson)-1];remember(lesson.number);
 $('#step-select').addEventListener('change',e=>location.href=`step-${String(e.target.value).padStart(2,'0')}.html`);
 const refs=land.nodes.find(n=>n.id===lesson.topic)?.sources||[];$('#lesson-sources').innerHTML=sourceLinks(refs.slice(0,3));
 const colors={input:['#ffcf7c','#443a29'],master:['#83ead1','#1d3b3c'],projection:['#8ec8ff','#21374e'],rule:['#c2a5ff','#342d4c'],identity:['#f3a8d4','#443043'],document:['#d7e68f','#353e2d'],diff:['#ffb89c','#45362f']};
 const ns='http://www.w3.org/2000/svg';const svg=document.createElementNS(ns,'svg');svg.setAttribute('role','img');svg.setAttribute('aria-label',lesson.short);svg.innerHTML='<defs><marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="5" markerHeight="5" orient="auto"><path d="M0 0 L10 5 L0 10" fill="#91aab7"/></marker></defs><g class="edges"></g><g class="nodes"></g>';
 $('#scene').append(svg);
 let phase=0,playing=false,timer=null,scrollMode=false;const reduced=matchMedia('(prefers-reduced-motion: reduce)');let visible=true;
 const mobile=()=>false;
 function positions(){const width=$('#scene').clientWidth, simple=lesson.frames[phase].nodes.every(n=>n.pos<3);if(mobile())return simple?[[width/2,75],[width/2,245],[width/2,415],[width*.25,425],[width*.75,330]]:[[width*.25,85],[width*.25,245],[width*.75,85],[width*.25,425],[width*.75,330]];return simple?[[135,210],[450,210],[765,210],[215,310],[640,310]]:[[135,100],[450,100],[765,100],[215,310],[640,310]]}
 function render(){const f=lesson.frames[phase],small=mobile(),width=small?$('#scene').clientWidth:900,height=small?510:420,w=small?(f.nodes.every(n=>n.pos<3)?Math.min(240,width-40):Math.min(154,width/2-22)):226,h=92,pts=positions();svg.setAttribute('viewBox',`0 0 ${width} ${height}`);svg.setAttribute('aria-label',`${lesson.short}: ${f.title}. ${f.copy} Nodes: ${f.nodes.map(n=>n.label+', '+n.detail+(n.mark?', mark '+n.mark:'')).join('; ')}. Connections: ${f.edges.map(e=>f.nodes.find(n=>n.id===e.a)?.label+' to '+f.nodes.find(n=>n.id===e.b)?.label+(e.label?', '+e.label:'')).join('; ')}.`);
  const old=new Map($$('.node-group',svg).map(e=>[e.dataset.id,e]));
  const wrap=(text,max)=>{const words=text.split(' '),lines=[];let line='';for(const word of words){if(line&&(line+' '+word).length>max){lines.push(line);line=word}else line+=(line?' ':'')+word}if(line)lines.push(line);return lines};
  f.nodes.forEach(n=>{let g=old.get(n.id);old.delete(n.id);if(!g){g=document.createElementNS(ns,'g');g.classList.add('node-group','new');g.dataset.id=n.id;$('.nodes',svg).append(g)}const [x,y]=pts[n.pos],c=colors[n.role],active=f.active.includes(n.id),labels=wrap(n.label,small?Math.floor(w/7):27),details=wrap(n.detail,small?Math.floor(w/5):35);g.setAttribute('transform',`translate(${x},${y})`);g.innerHTML=`<rect x="${-w/2}" y="-46" width="${w}" height="${h}" fill="${c[1]}" stroke="${c[0]}"/>${active?`<rect class="activation-ring" x="${-w/2-5}" y="-51" width="${w+10}" height="${h+10}" rx="10"/>`:''}<circle class="node-dot" cx="${-w/2+14}" cy="-29" r="3" fill="${c[0]}"/><text class="node-label" text-anchor="middle" fill="${c[0]}">${labels.map((l,i)=>`<tspan x="0" y="${(labels.length>1?-12:-6)+i*17}">${esc(l)}</tspan>`).join('')}</text><text class="node-detail" text-anchor="middle">${details.map((l,i)=>`<tspan x="0" y="${(details.length>1?22:26)+i*12}">${esc(l)}</tspan>`).join('')}</text>${n.mark?`<circle class="mark-bg" cx="${w/2-7}" cy="-40" r="11"/><text class="change-mark" x="${w/2-7}" y="-35" text-anchor="middle">${esc(n.mark)}</text>`:''}`});old.forEach(n=>n.remove());
  $('.edges',svg).innerHTML=f.edges.map((e,i)=>{const a=f.nodes.find(n=>n.id===e.a),b=f.nodes.find(n=>n.id===e.b);if(!a||!b)return '';let [ax,ay]=pts[a.pos],[bx,by]=pts[b.pos],dx=bx-ax,dy=by-ay;const scale=1/Math.max(Math.abs(dx)/(w/2+5),Math.abs(dy)/(h/2+5));let x1=ax+dx*scale,y1=ay+dy*scale,x2=bx-dx*scale,y2=by-dy*scale;const extra=e.label==='derived';const path=extra&&!small?`M${x1},${y1} Q450,260 ${x2},${y2}`:`M${x1},${y1} L${x2},${y2}`;return `<path class="connector ${extra?'derived':''}" d="${path}"/>${f.active.includes(e.b)?`<path class="flow" d="${path}"/>`:""}${e.label?`<text class="edge-label" text-anchor="middle" x="${(x1+x2)/2}" y="${extra&&!small?190:(y1+y2)/2-9}">${esc(e.label)}</text>`:''}`}).join('');
  $('#formula').textContent=f.formula;$('#frame-label').textContent=`${phase+1} / ${lesson.frames.length}`;$('#timeline').value=phase;$('#timeline-position').textContent=`${phase+1} / ${lesson.frames.length}`;$('#frame-title').textContent=f.title;$('#frame-copy').textContent=f.copy;$$('[data-phase]').forEach(b=>b.setAttribute('aria-pressed',String(Number(b.dataset.phase)===phase)));
 }
 function stop(){playing=false;$('#scene').classList.add('paused');clearTimeout(timer);$('#play').textContent='▶ Play';$('#play').setAttribute('aria-pressed','false')}
 function tick(){timer=setTimeout(()=>{if(!playing||!visible)return;if(phase===lesson.frames.length-1){stop();return}phase++;render();tick()},3600)}
 function play(){if(scrollMode)setScroll(false);if(playing){stop();return}if(phase===lesson.frames.length-1)phase=0;playing=true;$('#scene').classList.remove('paused');render();$('#play').textContent='Ⅱ Pause';$('#play').setAttribute('aria-pressed','true');tick()}
 function setScroll(value){stop();scrollMode=value;$('.demonstration').classList.toggle('scroll-driven',value);$('#scroll-mode').setAttribute('aria-pressed',String(value));$('#scroll-mode').textContent=value?'Exit scroll':'Scroll';render();}
 $('#zoom-scene').addEventListener('click',()=>{const zoom=$('#scene').classList.toggle('zoomed');$('#zoom-scene').setAttribute('aria-pressed',String(zoom));$('#zoom-scene').textContent=zoom?'Fit':'Zoom'});
 $('#scroll-mode').addEventListener('click',()=>setScroll(!scrollMode));
 $('#timeline').addEventListener('input',e=>{stop();phase=Number(e.target.value);render()});
 window.addEventListener('scroll',()=>{if(!scrollMode)return;const box=$('.demonstration').getBoundingClientRect(),length=box.height-window.innerHeight;const p=Math.max(0,Math.min(3,Math.floor((-box.top+80)/Math.max(1,length)*4)));if(p!==phase){phase=p;render()}},{passive:true});
 $('#play').addEventListener('click',play);$('#restart').addEventListener('click',()=>{stop();phase=0;render()});$$('[data-phase]').forEach(b=>b.addEventListener('click',()=>{stop();phase=Number(b.dataset.phase);render()}));
 window.addEventListener('resize',render);document.addEventListener('visibilitychange',()=>{if(document.hidden)stop()});reduced.addEventListener('change',()=>{if(reduced.matches)stop()});
 new IntersectionObserver(entries=>{visible=entries[0].isIntersecting;if(!visible)stop()},{threshold:.1}).observe($('#scene'));render();stop();if(!reduced.matches)play();
})().catch(error=>{console.error(error);const panel=document.querySelector('#scene')||document.querySelector('#map-detail');if(panel)panel.innerHTML='<p class="scene-fallback">The illustration could not load. The contract and explanation remain available below.</p>'});
