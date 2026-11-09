'use strict';
(()=>{
 const data=window.atlasFoundations||[];if(!data.length)return;
 const el=(tag,text,cls)=>{const node=document.createElement(tag);if(text!==undefined)node.textContent=text;if(cls)node.className=cls;return node};
 const linkFor=f=>'index.html#explore/'+[f.topic,f.topic+':sections',f.id].map(encodeURIComponent).join('/');
 const map=document.querySelector('#landscape>.map-shell');
 if(map){
  const layout=el('div',undefined,'map-with-foundations');map.before(layout);layout.append(map);
  const aside=el('aside',undefined,'foundation-sidebar');aside.setAttribute('aria-label','Technical foundations');
  const details=el('details');details.open=window.matchMedia('(min-width:1200px)').matches;const summary=el('summary');summary.append(el('h2','Technical foundations'));details.append(summary);
  const content=el('div',undefined,'foundation-sidebar-content');content.append(el('p','The engine’s design builds on these four foundations. Open a topic for its explanation and research.'));
  for(const f of data){const item=el('section'),h=el('h3'),a=el('a',f.title);a.href=linkFor(f);h.append(a);item.append(h,el('p',f.copy));content.append(item)}
  details.append(content);aside.append(details);layout.append(aside);
 }
 function note(target,heading,text,ids){
  const anchor=document.querySelector(target);if(!anchor)return;
  const aside=el('aside',undefined,'foundation-note');aside.append(el('h3',heading),el('p',text));const links=el('nav');links.setAttribute('aria-label',heading);
  for(const id of ids){const f=data.find(f=>f.id==='foundation:'+id),a=el('a',f.title+' →');a.href=linkFor(f);links.append(a)}aside.append(links);anchor.after(aside);
 }
 note('#transformation .section-title','The foundations of these transformations','This example uses Triple Graph Grammars to relate source and target graphs. Graph lenses explain updates between views; bijective transformations describe mappings with an exact inverse.',['tgg','lenses','bijective']);
 note('#how-it-works .paper-flow','Graph projections drive the engine','Configured rules turn inputs into graphs and graph-change events. Projections can consume other projections’ outputs, and different ontologies can interpret the same inputs.',['projections']);
 note('#model .paper-heading','Ontologies give graph projections meaning','An ontology defines types, relationships and properties. Projections apply the rules that produce each view. Health modelling is one possible ontology.',['projections','lenses']);
 note('#explorer .section-heading','Understand how the views relate','Graph projections produce the different views. Graph lenses describe how supported view edits can be reflected back while keeping the source and view consistent.',['projections','lenses']);
})();
