const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const {project,diff}=require('../dist/content-projection.js');
const {build}=require('../dist/content-model.js');
const context={window:{}};
const source=fs.readFileSync(new URL('../dist/roadmap.js','file://'+__filename),'utf8');
vm.runInNewContext(source.slice(0,source.indexOf('  const guideSteps'))+'})();',context);
const {mapNodes,sources}=context.window.atlasContentModel;
const sections=JSON.parse(fs.readFileSync(__dirname+'/../dist/atlas-content.json'));
const adrs=JSON.parse(fs.readFileSync(__dirname+'/../dist/adrs.json'));
const foundations=require('../dist/foundations.js');
const model=build(mapNodes,sources,sections,adrs,foundations);
const all=new Set(['sections','decisions','sources']);

test('every existing section, decision and source is reachable with its original content',()=>{
  const full=project(model,new Set(model.keys()),all);
  assert.equal(full.nodes.size,model.size);
  for(const [i,s] of sections.entries())assert.equal(full.nodes.get('section:'+i).url,s.url);
  for(const a of adrs){assert.equal(full.nodes.get(a.id).body,a.body);assert.equal(full.nodes.get(a.id).copy,a.status);}
  for(const s of sources)assert.equal(full.nodes.get('source:'+s.id).copy,s.note);
  for(const f of foundations){assert.equal(full.nodes.get(f.id).body,f.body);for(const id of f.references)assert.ok(model.has('source:'+id));}
  for(const e of full.edges.values()){assert.ok(full.nodes.has(e.source));assert.ok(full.nodes.has(e.target));}
});
test('nested expansion adds only the selected branch and collapse removes its descendants',()=>{
  const top=project(model,new Set(['atlas']),all);
  const expanded=new Set(['atlas','views']);
  const topic=project(model,expanded,all);
  assert.equal(diff(top,topic).nodes.removed.length,0);
  assert.deepEqual(diff(top,topic).nodes.added,['views:sections','views:decisions','views:sources']);
  expanded.add('views:decisions');
  const records=project(model,expanded,all);
  assert.equal(diff(topic,records).nodes.added.length,model.get('views:decisions').children.length);
  expanded.delete('views');
  const collapsed=project(model,expanded,all);
  assert.deepEqual([...collapsed.nodes.keys()],[...top.nodes.keys()]);
});
test('filtering keeps other branches and reapplying a view emits no graph changes',()=>{
  const expanded=new Set(['atlas','views','views:decisions','views:sources']);
  const before=project(model,expanded,all);
  const filtered=project(model,expanded,new Set(['sections','decisions']));
  const d=diff(before,filtered);
  assert.equal(d.nodes.added.length,0);
  assert.ok(filtered.nodes.has('ADR-002'));
  assert.ok(!filtered.nodes.has('views:sources'));
  const repeat=diff(filtered,project(model,expanded,new Set(['sections','decisions'])));
  for(const part of [repeat.nodes,repeat.edges]){assert.equal(part.added.length,0);assert.equal(part.removed.length,0);}
});
test('shared sources keep one identity and survive removal of one parent branch',()=>{
  const expanded=new Set(['atlas','impact','impact:sources','response','response:sources']);
  const before=project(model,expanded,all);
  assert.ok(before.edges.has('impact:sources>source:actionnex'));
  assert.ok(before.edges.has('response:sources>source:actionnex'));
  expanded.delete('impact');
  const after=project(model,expanded,all);
  assert.ok(after.nodes.has('source:actionnex'));
  assert.ok(!diff(before,after).nodes.removed.includes('source:actionnex'));
});

test('a topic lists all chapter contents while record and sideways views stay focused',()=>{
 const {focus}=require('../dist/content-projection.js');
 const topic=focus(model,['views']);
 assert.ok(topic.nodes.has('views:decisions'));
 assert.ok(topic.nodes.has('ADR-002'));
 for(const chapter of model.get('views').children)for(const id of model.get(chapter).children){assert.ok(topic.nodes.has(id));assert.ok(topic.edges.has(chapter+'>'+id));}
 assert.ok(!topic.nodes.has('impact'));
 const collection=focus(model,['views','views:decisions']);
 assert.ok(collection.nodes.has('ADR-002'));
 assert.ok(!collection.nodes.has('views:sources'));
 const record=focus(model,['views','views:decisions','ADR-002']);
 assert.deepEqual([...record.nodes.keys()],['atlas','views','views:decisions','ADR-002']);
 const sibling=focus(model,['views','views:decisions','ADR-007']);
 assert.deepEqual(diff(record,sibling).nodes.added,['ADR-007']);
 assert.deepEqual(diff(record,sibling).nodes.removed,['ADR-002']);
 assert.throws(()=>focus(model,['impact','views:decisions']));
});
