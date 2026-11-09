/* Small test model for the page's fixtures. The production engine can use a different implementation. */
(function(root){'use strict';
 const clone=x=>JSON.parse(JSON.stringify(x));
 const canon=v=>Array.isArray(v)?v.map(canon):v&&typeof v==='object'?Object.fromEntries(Object.keys(v).sort().map(k=>[k,canon(v[k])])):v;
 const same=(a,b)=>JSON.stringify(canon(a))===JSON.stringify(canon(b));
 function operations(r){
  if(r.kind==='flow')return [{op:'entity',id:r.from,type:r.fromType||'Workload'},{op:'entity',id:r.to,type:r.toType||'Workload'},{op:'edge',id:r.edge,from:r.from,to:r.to,type:r.edgeType||'calls'}];
  if(r.kind==='http')return [{op:'entity',id:r.service,...(r.type?{type:r.type}:{}),health:r.health}];
  if(r.kind==='ml')return [{op:'entity',id:r.job,type:'Job',health:r.health}];
  if(r.kind==='ops')return r.ops;
  throw Error('Unsupported fixture format: '+r.kind);
 }
 class Model{
  constructor(config,options={}){this.config=clone(config);this.options=options;this.ps={};this.order=[];for(const p of config.projections){const entities=Object.fromEntries((p.initial?.entities||[]).map(x=>[x.id,clone(x)]));const edges=Object.fromEntries((p.initial?.edges||[]).map(x=>[x.id,clone(x)]));this.ps[p.id]={entities,edges,diffs:[],telemetry:[],history:{}}}}
  ingest(record){this.order.push(record.id);for(const p of this.config.projections){const st=this.ps[p.id];if(p.accept&&!p.accept.includes(record.kind))continue;st.telemetry.push({input:record.id,kind:record.kind});const changes=[],affected=new Set();for(const op of operations(record)){if(op.op==='removeEntity'){const before=st.entities[op.id];if(before){delete st.entities[op.id];delete st.history['entity:'+op.id];changes.push({op:'entity.remove',id:op.id})}continue}const isEntity=op.op==='entity';if(!isEntity&&op.op!=='edge')throw Error('Unknown operation');if(!isEntity&&p.dropEdges)continue;let id=isEntity?op.id:`${p.id}/${op.id}`;const store=isEntity?st.entities:st.edges;if(!isEntity&&op.from!==undefined&&op.to!==undefined&&op.type!==undefined){const match=Object.values(store).find(e=>e.from===op.from&&e.to===op.to&&e.type===op.type);if(match)id=match.id}const key=`${isEntity?'entity':'edge'}:${id}`;const patch={id};for(const field of ['from','to','type','health'])if(op[field]!==undefined)patch[field]=field==='type'&&p.typeMap?.[op[field]]?p.typeMap[op[field]]:op[field];const before=store[id];if(isEntity&&!before&&patch.type===undefined)patch.type='NoType';const after=Object.assign({},before||{},patch);if(!before||!same(before,after)||this.options.repeatDefect){store[id]=after;changes.push({op:`${isEntity?'entity':'edge'}.${before?'update':'add'}`,id,after:clone(after)});affected.add(key)}}
   if(changes.length){st.diffs.push({input:record.id,changes});for(const key of affected)st.history[key]=[...(st.history[key]||[]),record.id].slice(-(p.historyLimit??2))}
  }}
  ingestBatch(records){for(const r of records)this.ingest(r)}
  advanceTime(t){this.now=t}
  snapshot(id){const s=this.ps[id];return {entities:Object.values(s.entities).sort((a,b)=>a.id.localeCompare(b.id)).map(clone),edges:Object.values(s.edges).sort((a,b)=>a.id.localeCompare(b.id)).map(clone)}}
  diffs(id){return clone(this.ps[id].diffs)} history(id){return clone(this.ps[id].history)} telemetry(id){return clone(this.ps[id].telemetry)}
 }
 function applyDiffs(snapshot,diffs){const n=Object.fromEntries(snapshot.entities.map(x=>[x.id,clone(x)])),e=Object.fromEntries(snapshot.edges.map(x=>[x.id,clone(x)]));for(const diff of diffs)for(const c of diff.changes){let target=c.op.startsWith('entity.')?n:e;if(c.op.endsWith('.remove'))delete target[c.id];else target[c.id]=clone(c.after)}return {entities:Object.values(n).sort((a,b)=>a.id.localeCompare(b.id)),edges:Object.values(e).sort((a,b)=>a.id.localeCompare(b.id))}}
 function summary(inputs,config){const members=inputs.entities.filter(e=>config.memberIds.includes(e.id)).map(e=>e.id).sort();return {id:config.id,members}}
 function trial(mlGraph,context){const workload=mlGraph.entities.find(e=>e.id===context.workload);return {phase:context.phase,service:context.service,health:workload?workload.health:'unknown'}}
 function uniqueRetained(history,order){const s=new Set(Object.values(history).flat());return order.filter((id,i)=>s.has(id)&&order.indexOf(id)===i)}
 function edit(previous,retained,allowedKinds,allowedInputs){let edges=[];for(const record of retained)if(allowedKinds.includes(record.kind)&&(!allowedInputs||allowedInputs.includes(record.id))&&record.edge)edges.push(`p/${record.edge}`);edges=[...new Set(edges)].sort();const old=previous.edges.map(e=>e.id).sort();return {firstInput:'previous graph',replayed:retained.map(x=>x.id),graphEdges:edges,finalDiff:{added:edges.filter(x=>!old.includes(x)),removed:old.filter(x=>!edges.includes(x))}}}
 const api={Model,applyDiffs,summary,trial,uniqueRetained,edit,same,canon};if(typeof module!=='undefined'&&module.exports)module.exports=api;root.RefEngine=api;
})(typeof globalThis!=='undefined'?globalThis:this);
