'use strict';
(async()=>{
 const mount=document.querySelector('#content-graph-app');if(!mount)return;
 const el=(tag,text,cls)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=text;if(cls)e.className=cls;return e};
 const href=path=>'#explore/'+path.map(encodeURIComponent).join('/');
 try{
 const [sections,adrs]=await Promise.all(['atlas-content.json','adrs.json'].map(async url=>{const r=await fetch(url);if(!r.ok)throw Error(url);return r.json()}));
 const {mapNodes,sources,guideSteps}=window.atlasContentModel;const model=AtlasContent.build(mapNodes,sources,sections,adrs,window.atlasFoundations);
 const parents=new Map();for(const n of model.values())for(const id of n.children||[]){if(!parents.has(id))parents.set(id,[]);parents.get(id).push(n.id)}
 function pathFor(id,preferred){if(id==='atlas')return [];const parent=parents.get(id)?.find(p=>p.startsWith(preferred+':'))||parents.get(id)?.[0];return parent?[...pathFor(parent,preferred),id]:[id]}
 function inline(parent,text,topic){
  for(const part of text.split(/(\*\*.*?\*\*|`[^`]+`|ADR-\d{3})/g)){
   if(part.startsWith('**')&&part.endsWith('**'))parent.append(el('strong',part.slice(2,-2)));
   else if(part.startsWith('`')&&part.endsWith('`'))parent.append(el('code',part.slice(1,-1)));
   else if(/^ADR-\d{3}$/.test(part)&&model.has(part)){const a=el('a',part);a.href=href(pathFor(part,topic));parent.append(a)}
   else parent.append(document.createTextNode(part));
  }
 }
 function reading(text,topic){const article=el('article');let list=null;for(const raw of text.split('\n')){const line=raw.trim();if(!line){list=null;continue}const heading=line.match(/^#{2,3}\s+(.*)/),bullet=line.match(/^[-*]\s+(.*)/),number=line.match(/^\d+\.\s+(.*)/);let p;if(heading){list=null;p=el('h2');inline(p,heading[1],topic);article.append(p)}else if(bullet||number){const tag=number?'OL':'UL';if(!list||list.tagName!==tag){list=el(tag.toLowerCase());article.append(list)}p=el('li');inline(p,(bullet||number)[1],topic);list.append(p)}else{list=null;p=el('p');inline(p,line,topic);article.append(p)}}return article}
 let current={nodes:new Map(),edges:new Map()},lastPath=null;const history=[];
 const header=el('header',undefined,'content-heading');const depth=el('p',undefined,'depth-label'),title=el('h1'),description=el('p');title.id='content-graph-title';title.tabIndex=-1;header.append(depth,title,description);
 const list=el('ul',undefined,'level-graph');list.setAttribute('aria-label','Choose what to explore next');
 const chapters=el('div',undefined,'topic-chapters');
 const body=el('div',undefined,'record-reading');const sideways=el('nav',undefined,'sideways-links');sideways.setAttribute('aria-label','Related content');
 const diagnostics=el('details',undefined,'projection-history');diagnostics.append(el('summary','See how this projection changed'));const stats=el('p');stats.id='projection-summary';stats.setAttribute('role','status');const events=el('div');const repeat=el('button','Reapply this view');repeat.type='button';repeat.onclick=()=>render(true);diagnostics.append(stats,repeat,events);
 mount.replaceChildren(header,chapters,list,body,sideways,diagnostics);
 function addLink(parent,label,url){const a=el('a',label);a.href=url;parent.append(a);return a}
 function remember(n,path,returnPath=path){const p=model.get(path.at(-1)||'atlas'),parent=model.get(returnPath.at(-1)||'atlas');try{sessionStorage.setItem('atlas-reading-context',JSON.stringify({parentHref:'index.html'+href(returnPath),parentTitle:parent.title,target:n.url,siblings:(p.children||[]).map(id=>model.get(id)).filter(x=>x.url&&x.kind==='section').map(x=>({title:x.title,url:x.url}))}))}catch{}}
 function chapter(n,path,index){
  const section=el('section',undefined,'topic-chapter'),heading=el('h2',n.title),items=el('ul',undefined,'chapter-titles');heading.id='chapter-'+n.id;section.setAttribute('aria-labelledby',heading.id);
  section.append(el('p',`Chapter ${index+1} · ${n.children.length} ${n.children.length===1?'item':'items'}`,'chapter-label'),heading,items);
  for(const id of n.children){
   const item=model.get(id),li=el('li'),a=el('a',undefined,'chapter-link');a.dataset.node=id;a.href=item.kind==='section'?item.url:href([...path,n.id,id]);
   a.append(el('span',item.title));const arrow=el('span','→','chapter-arrow');arrow.setAttribute('aria-hidden','true');a.append(arrow);
   a.addEventListener('click',()=>{try{sessionStorage.setItem('atlas-topic-return',JSON.stringify({topic:path[0],item:id}))}catch{}if(item.kind==='section')remember(item,[...path,n.id],path)});
   li.append(a);items.append(li);
  }
  return section;
 }
 function card(n,path){const li=el('li');li.dataset.node=n.id;const a=el('a',undefined,'level-card');a.href=n.kind==='section'?n.url:href([...path,n.id]);a.append(el('strong',n.title),el('span',n.children?`${n.children.length} ${n.kind==='topic'?'ways to explore':'items'} →`:'Read →','node-action'));if(n.kind==='section')a.addEventListener('click',()=>remember(n,path));li.append(a);return li}
 function render(repeat=false){
  if(!location.hash.startsWith('#explore/')){lastPath=null;current=AtlasProjection.focus(model,[]);return}
  let path;try{path=location.hash.slice(9).split('/').filter(Boolean).map(decodeURIComponent);AtlasProjection.focus(model,path)}catch{path=[]}
  const key=path.join('/');if(key===lastPath&&!repeat)return;lastPath=key;
  const n=model.get(path.at(-1)||'atlas');const next=AtlasProjection.focus(model,path);const delta=AtlasProjection.diff(current,next);current=next;
  depth.textContent=n.kind==='root'?'Topics':n.kind==='topic'?'Topic':n.kind==='collection'?'Choose one item':n.kind==='decision'?'Architecture decision':n.kind==='foundation'?'Technical foundation':'Research source';
  title.textContent=n.title;description.textContent=n.copy|| (n.kind==='root'?'Choose a topic. Each step opens one level of detail.':n.kind==='collection'?'Choose an item to read it.':'');description.hidden=!description.textContent;
  // Keep shared cards by identity when moving sideways between collections.
  chapters.hidden=n.kind!=='topic';chapters.replaceChildren();if(n.kind==='topic')n.children.forEach((id,i)=>chapters.append(chapter(model.get(id),path,i)));
  const existing=new Map([...list.children].map(li=>[li.dataset.node,li]));const wanted=new Set(n.kind==='topic'?[]:n.children||[]);let position=0;
  for(const id of wanted){let li=existing.get(id);if(!li)li=card(model.get(id),path);else{const a=li.firstElementChild;a.href=model.get(id).kind==='section'?model.get(id).url:href([...path,id]);if(model.get(id).kind==='section')a.onclick=()=>remember(model.get(id),path)}if(list.children[position]!==li)list.insertBefore(li,list.children[position]||null);position++}
  for(const [id,li]of existing)if(!wanted.has(id))li.remove();list.hidden=!wanted.size;
  body.replaceChildren();if(n.meta)body.append(el('p',n.meta,'record-meta'));if(n.body)body.append(reading(n.body,path[0]));if(n.url){const a=addLink(body,'Read the original source ↗',n.url);a.target='_blank';a.rel='noopener noreferrer'}body.hidden=!body.childNodes.length;
  sideways.replaceChildren();
  if(n.references?.length){sideways.append(el('h2','Related research'));for(const id of n.references){const source=model.get('source:'+id);if(source)addLink(sideways,source.title+' →',href(pathFor(source.id,path[0])))}}
  if(n.kind==='topic'){
   const related=n.related||[];if(related.length){sideways.append(el('h2','Connected on the map'));for(const id of related)addLink(sideways,model.get(id).title+' →',href([id]))}
   const steps=guideSteps.map((s,i)=>({...s,index:i})).filter(s=>s.node===n.id);if(steps.length){sideways.append(el('h2','Follow this in the guide'));for(const s of steps){const a=addLink(sideways,`${s.index+1}. ${s.title} →`,'#engine-guide');a.onclick=()=>document.querySelector(`[data-guide-step="${s.index}"]`).click()}}
  }else if(n.kind==='collection'){
   const topic=model.get(path[0]);sideways.append(el('h2','Also about '+topic.title.toLowerCase()));for(const id of topic.children.filter(id=>id!==n.id))addLink(sideways,model.get(id).title+' →',href([topic.id,id]));
  }else if(!n.children){
   const refs=[...new Set(n.body?.match(/ADR-\d{3}/g)||[])].filter(id=>id!==n.id&&model.has(id));if(refs.length){sideways.append(el('h2','Referenced decisions'));for(const id of refs)addLink(sideways,model.get(id).title+' →',href(pathFor(id,path[0])))}
   const other=(parents.get(n.id)||[]).filter(id=>id!==path.at(-2));if(other.length){sideways.append(el('h2','Also included in'));for(const id of other){const t=model.get((parents.get(id)||[])[0]);addLink(sideways,t.title+' →',href(pathFor(id,t.id)))}}
   const siblings=model.get(path.at(-2))?.children||[],i=siblings.indexOf(n.id);if(i>=0&&siblings.length>1){sideways.append(el('h2','Continue in this collection'));if(i>0)addLink(sideways,'← '+model.get(siblings[i-1]).title,href([...path.slice(0,-1),siblings[i-1]]));if(i<siblings.length-1)addLink(sideways,model.get(siblings[i+1]).title+' →',href([...path.slice(0,-1),siblings[i+1]]))}
  }
  sideways.hidden=!sideways.childNodes.length;
  stats.textContent=`${current.nodes.size} nodes and ${current.edges.size} connections in this view. ${delta.nodes.added.length} nodes added, ${delta.nodes.removed.length} removed, ${delta.nodes.unchanged.length} unchanged.`;
  history.unshift({title:n.title,delta});history.splice(5);events.replaceChildren();for(const h of history){const a=el('article');a.append(el('strong',h.title));for(const [label,part]of [['Nodes',h.delta.nodes],['Connections',h.delta.edges]]){a.append(el('p',`${label}: +${part.added.length} / −${part.removed.length} / ${part.unchanged.length} unchanged`));for(const [label,ids]of [['Added',part.added],['Removed',part.removed]])if(ids.length)a.append(el('p',label+': '+ids.map(id=>model.get(id)?.title||id).join('; ')))}events.append(a)}
  const crumbs=[{title:'Wardley map',url:'#landscape'},...path.map((id,i)=>({title:model.get(id).title,url:href(path.slice(0,i+1))}))];window.atlasDepth?.setTrail(crumbs,path.length>1?crumbs[1]:crumbs[0]);if(!repeat){title.focus({preventScroll:true});window.scrollTo({top:0,behavior:'instant'});if(n.kind==='topic')try{const last=JSON.parse(sessionStorage.getItem('atlas-topic-return'));if(last?.topic===n.id){sessionStorage.removeItem('atlas-topic-return');const a=[...chapters.querySelectorAll('a')].find(a=>a.dataset.node===last.item);if(a){a.focus({preventScroll:true});a.scrollIntoView({block:'center',behavior:'instant'})}}}catch{}}
 }
 window.addEventListener('hashchange',()=>render());document.addEventListener('atlas-expand-topic',e=>{location.hash='explore/'+e.detail});render();
 }catch(error){mount.replaceChildren(el('p','The content could not load. Reload this page or return to the map.'));const a=el('a','Back to the map');a.href='#landscape';mount.append(a)}
})();
