/* Index the existing atlas records without changing their contents. */
(function(root){
 function build(mapNodes,sources,sections,adrs,foundations=[]){
const model=new Map();
 const add=n=>model.set(n.id,n);
 add({id:'atlas',title:'Graph Projection Atlas',kind:'root',children:mapNodes.map(n=>n.id)});
 const adrGroups={ontology:[1,3,49,50,51,52,53,54,55,56,83,84,85,86,87,88,90,91,94,11],transforms:[25,29,30,31,32,42,43,57,58,59,60,62,63],views:[2,7,8,22,23,24,26,27,28,65],replay:[4,12,13,14,15,16,19,20,45,46,61,64,66,67,68,69,70,71,72,73,74,75,76,77,78,79,80,81,82],ingest:[5,6,9,10,17,18,21,33,34,48,89],runtime:[35,36,37,38,39,40,41,44,47,93],impact:[92]};
 const sectionGroups={
 impact:['payment-path','detection'],response:['actionnex'],
 ontology:['model','identity','explorer'],views:['section-1','section-6','explorer','topics','priorities'],
 transforms:['how-it-works','propagation','transformation'],replay:['replay','memory'],
 stream:['transformation','simulation'],ingest:['how-it-works'],
 runtime:['section-9','plan','simulation','decisions','section-18']
 };
 // These are the same connections drawn on the existing Wardley map.
 const connections=[['impact','ontology'],['impact','views'],['ontology','transforms'],['views','transforms'],['transforms','replay'],['transforms','stream'],['replay','stream'],['stream','ingest'],['ingest','runtime'],['impact','response'],['response','ingest']];
 const assigned=new Set();
 for(const n of mapNodes){
  const secIds=[];for(const [i,s]of sections.entries()){if((sectionGroups[n.id]||[]).includes(s.url.split('#')[1])){secIds.push('section:'+i);assigned.add(i)}}
  const groups=[['sections','Explanations & examples',secIds],['decisions','Decisions',adrs.filter(a=>(adrGroups[n.id]||[]).includes(Number(a.id.slice(4)))).map(a=>a.id)],['sources','Research and sources',n.sources.map(s=>'source:'+s)]];
  add({id:n.id,title:n.label,copy:n.copy,kind:'topic',related:connections.filter(pair=>pair.includes(n.id)).map(pair=>pair.find(id=>id!==n.id)),children:groups.filter(g=>g[2].length).map(g=>n.id+':'+g[0])});
  for(const [kind,title,children]of groups)if(children.length)add({id:n.id+':'+kind,title,kind:'collection',collection:kind,children});
 }
 sections.forEach((s,i)=>{add({id:'section:'+i,title:s.title,kind:'section',url:s.url});if(!assigned.has(i))model.get('views:sections').children.push('section:'+i)});
 sources.forEach(s=>add({id:'source:'+s.id,title:s.title,kind:'source',copy:s.note,url:s.url,meta:s.type+' · '+s.year}));
 adrs.forEach(a=>add({id:a.id,title:a.id+' · '+a.title,kind:'decision',copy:a.status,body:a.body}));
 for(const f of foundations){add(f);for(const topic of f.topics){const chapter=model.get(topic+':sections');if(chapter)chapter.children.push(f.id)}}
 // Include every existing record even if the catalogue gains a new ADR later.
 const grouped=new Set([...model.values()].flatMap(n=>n.children||[]));for(const a of adrs)if(!grouped.has(a.id))model.get('runtime:decisions').children.push(a.id);

return model;
 }
 root.AtlasContent={build};if(typeof module!=='undefined')module.exports=root.AtlasContent;
})(typeof window==='undefined'?globalThis:window);
