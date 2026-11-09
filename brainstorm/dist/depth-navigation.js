'use strict';
(()=>{
 const main=document.querySelector('main');if(!main)return;
 const isMap=!!document.querySelector('#wardley-map'),isExamples=!!document.querySelector('#how-it-works'),isEvaluation=!!document.querySelector('.eval-tabs');
 const el=(tag,text,cls)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(cls)n.className=cls;return n};
 const bar=el('nav',undefined,'depth-nav');bar.setAttribute('aria-label','Your position in the atlas');const up=el('a',undefined,'depth-up'),crumbs=el('ol',undefined,'depth-crumbs');bar.append(up,crumbs);document.querySelector('header.site-header').after(bar);
 function setTrail(items,parent){bar.hidden=!parent;up.textContent=parent?'↑ '+parent.title:'';up.href=parent?.url||'index.html';crumbs.replaceChildren();for(const [i,item]of items.entries()){const li=el('li');if(i===items.length-1){const s=el('span',item.title);s.setAttribute('aria-current','page');li.append(s)}else{const a=el('a',item.title);a.href=item.url;li.append(a)}crumbs.append(li)}}
 window.atlasDepth={setTrail};
 const mapLink={title:'Wardley map',url:isMap?'#landscape':'index.html#landscape'};
 const catalogue=el('section',undefined,'section-menu wrap');catalogue.id='example-index';
 let entries=[];const nestedParents={propagation:'how-it-works',identity:'model',memory:'replay'};
 if(isMap){
  const graph=document.querySelector('#content-graph');graph.classList.add('wrap');graph.querySelector('.section-title')?.remove();graph.querySelector('.graph-back')?.remove();main.append(graph);
  const guide=el('section',undefined,'guide-screen wrap');guide.id='guide-screen';guide.append(el('h1','Explore the engine, step by step.'));const panel=document.querySelector('#guide-panel');panel.open=true;guide.append(panel);main.append(guide);
  const action=document.querySelector('#actionnex');action.classList.add('wrap');main.append(action);
  document.querySelector('.rm-jump')?.remove();document.querySelector('.map-inspector').hidden=true;
  document.querySelector('.map-mode-actions a[href="#content-graph"]').href='#explore/';document.querySelector('.map-mode-actions a[href="#explore/"]').textContent='Browse topics';
  const searchLink=el('a','Search all sections');searchLink.href='#atlas-directory';document.querySelector('.map-mode-actions').append(searchLink);
  const option=el('option','Choose a topic');option.value='';document.querySelector('#map-topic').prepend(option);
  const directory=el('section',undefined,'atlas-directory wrap');directory.id='atlas-directory';const h=el('h1','Find a section');const label=el('label','Search titles');label.htmlFor='atlas-search';const input=el('input');input.type='search';input.id='atlas-search';input.placeholder='For example: identity or replay';const results=el('p');results.id='atlas-results';results.setAttribute('role','status');const links=el('ul',undefined,'section-links');directory.append(h,label,input,results,links);main.append(directory);
  input.oninput=()=>{const q=input.value.toLowerCase();links.replaceChildren();const found=entries.filter(x=>x.title.toLowerCase().includes(q));for(const x of found){const li=el('li'),a=el('a',x.title);a.href=x.url;a.onclick=()=>remember({parentTitle:'Search all sections',parentHref:'index.html#atlas-directory',target:x.url,siblings:found});li.append(a);links.append(li)}results.textContent=`${found.length} sections`};
 }else if(isExamples){
  for(const [id,parentId]of Object.entries(nestedParents)){
   const child=document.getElementById(id);if(!child)continue;const parent=document.getElementById(parentId);const section=el('section',undefined,'detail-screen wrap');section.id=id+'-screen';section.append(child);main.append(section);
   const nav=el('nav',undefined,'deeper-links');nav.setAttribute('aria-label','Explore this in detail');nav.append(el('h3','Explore this in detail'));const a=el('a',child.querySelector('h2,h3')?.textContent||entries.find(x=>x.url.endsWith('#'+id))?.title||({propagation:'Propagate attributes',identity:'Choose identity fields',memory:'Inspect retained inputs'})[id]);a.href='#'+id;nav.append(a);parent.append(nav);
  }
  main.append(catalogue);
  document.querySelectorAll('[data-go-layer]').forEach(b=>b.addEventListener('click',()=>location.hash='explorer'));
 }
 const panels=[...main.children];panels.forEach(n=>n.dataset.depthPanel='');
 const lateral=el('nav',undefined,'reading-links wrap');lateral.setAttribute('aria-label','Related sections');main.after(lateral);
 function remember(value){try{sessionStorage.setItem('atlas-reading-context',JSON.stringify(value))}catch{}}
 function context(){try{return JSON.parse(sessionStorage.getItem('atlas-reading-context'))}catch{return null}}
 function rootPanel(node){let p=node;while(p&&p.parentElement!==main)p=p.parentElement;return p}
 function drawLateral(current,items,parent){lateral.replaceChildren();const others=items.filter(x=>!x.url.endsWith('#'+current));if(!others.length){lateral.hidden=true;return}lateral.hidden=false;lateral.append(el('h2','Related sections'));for(const x of others){const a=el('a',x.title+' →');a.href=x.url;a.onclick=()=>remember({...parent,target:x.url,siblings:items});lateral.append(a)}}
 function route(initial=false){
  const hash=decodeURIComponent(location.hash.slice(1));lateral.hidden=true;panels.forEach(p=>p.hidden=true);
  if(isMap&&hash.startsWith('explore/')){document.querySelector('#content-graph').hidden=false;return}
  if(isMap&&hash==='content-graph'){location.replace('#explore/');return}
  if(isMap&&['','top','landscape','wardley-map'].includes(hash)){
   document.querySelector('#landscape').hidden=false;document.querySelector('#wardley-detail').hidden=true;document.querySelectorAll('.map-node').forEach(b=>{b.classList.remove('selected');b.setAttribute('aria-pressed','false')});document.querySelector('#map-topic').value='';setTrail([],null);
  }else if(isMap&&['guide-panel','engine-guide','guide-screen'].includes(hash)){
   document.querySelector('#guide-screen').hidden=false;document.querySelector('#guide-panel').open=true;setTrail([mapLink,{title:'Numbered guide',url:'#engine-guide'}],mapLink);
  }else if(isExamples&&['','top','section-1','example-index'].includes(hash)){
   document.querySelector('#section-1').hidden=false;catalogue.hidden=false;setTrail([mapLink,{title:'Examples',url:'examples.html'}],mapLink);
  }else if(isEvaluation){
   const tab=['plan','simulation','decisions'].includes(hash)?hash:null;
   for(const p of panels)if(!p.classList.contains('panel'))p.hidden=!!tab&&!p.classList.contains('eval-tabs');
   document.querySelectorAll('.panel').forEach(p=>p.hidden=p.id!==(tab||'plan'));
   document.querySelectorAll('.eval-tabs button').forEach(b=>b.setAttribute('aria-selected',String(b.dataset.panel===(tab||'plan'))));
   const c=context(),target='evaluation.html'+location.hash,parent=c&&(c.target===target||c.siblings?.some(x=>x.url===target))?{title:c.parentTitle,url:c.parentHref}:mapLink;
   setTrail([mapLink,{title:tab==='decisions'?'Decisions':tab==='simulation'?'Run tests':'Test design',url:location.hash||'#plan'}],parent);
  }else{
   const target=document.getElementById(hash),panel=rootPanel(target);if(panel&&panels.includes(panel)){panel.hidden=false;const current=entries.find(x=>x.url.endsWith('#'+hash));const title=current?.title||target.querySelector('h1,h2,h3')?.textContent||'Section';let parent=mapLink;const c=context();const file=isMap?'index.html':'examples.html';
    if(nestedParents[hash]){const id=nestedParents[hash];parent={title:entries.find(x=>x.url.endsWith('#'+id))?.title||'Explanation',url:'#'+id}}
    else if(c&&(c.target===file+'#'+hash||c.siblings?.some(x=>x.url===file+'#'+hash)))parent={title:c.parentTitle,url:c.parentHref};
    else if(isExamples)parent={title:'Examples',url:'examples.html'};
    setTrail([mapLink,...(parent.url!==mapLink.url?[parent]:[]),{title,url:location.hash}],parent);
    const siblings=c&&(c.target===file+'#'+hash||c.siblings?.some(x=>x.url===file+'#'+hash))?c.siblings||[]:entries.filter(x=>x.page===file&&x.url!==file+'#landscape'&&x.url!==file+'#section-1');
    drawLateral(hash,siblings,{parentHref:parent.url,parentTitle:parent.title});
   }else{const fallback=isMap?document.querySelector('#landscape'):document.querySelector('#section-1');if(fallback)fallback.hidden=false;setTrail([],null)}
  }
  if(!initial){const h=panels.find(p=>!p.hidden)?.querySelector('h1,h2,h3');if(h){h.tabIndex=-1;h.focus({preventScroll:true})}window.scrollTo({top:0,behavior:'instant'});window.dispatchEvent(new Event('resize'))}
 }
 fetch('atlas-content.json').then(r=>r.json()).then(data=>{entries=data;if(isMap)document.querySelector('#atlas-search').oninput();if(isExamples){catalogue.replaceChildren(el('h2','Choose an explanation or example'));const ul=el('ul',undefined,'section-links');for(const x of entries.filter(x=>x.page==='examples.html'&&!x.url.endsWith('#section-1'))){const li=el('li'),a=el('a',x.title);a.href=x.url;a.onclick=()=>remember({parentHref:'examples.html',parentTitle:'Examples',target:x.url,siblings:entries.filter(x=>x.page==='examples.html')});li.append(a);ul.append(li)}catalogue.append(ul)}route(true)}).catch(()=>{});
 window.addEventListener('hashchange',()=>route());route(true);
})();
