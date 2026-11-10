const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const dist=path.resolve(__dirname,'../dist'),story=JSON.parse(fs.readFileSync(path.join(dist,'story.json'))),symbols=JSON.parse(fs.readFileSync(path.join(dist,'code-index.json')));
test('21 lessons with four distinct, linked visual phases',()=>{assert.equal(story.length,21);story.forEach((s,i)=>{assert.equal(s.number,i+1);assert.equal(s.frames.length,4);assert.equal(new Set(s.frames.map(f=>f.title)).size,4);for(const f of s.frames){const ids=f.nodes.map(n=>n.id);assert.equal(new Set(ids).size,ids.length);assert.equal(new Set(f.nodes.map(n=>n.pos)).size,ids.length);f.edges.forEach(e=>{assert.ok(ids.includes(e.a));assert.ok(ids.includes(e.b))});f.active.forEach(id=>assert.ok(ids.includes(id)));f.nodes.forEach(n=>{assert.ok(s.roles.some(r=>r.id===n.role));assert.ok(['','+','−','Δ','×','0','…'].includes(n.mark))})}})});
test('Every cited contract and excerpt matches the checked-in C# source',()=>{for(const s of story)for(const symbol of s.refs){const ref=symbols[symbol];assert.ok(ref,symbol);const raw=fs.readFileSync(path.join(dist,'code',ref.file),'utf8').split('\n');assert.equal(raw.slice(ref.line-1,ref.line-1+ref.text.split('\n').length).join('\n'),ref.text)}});
test('Guide pages have controls, semantic legends and direct source links',()=>{for(const s of story){const html=fs.readFileSync(path.join(dist,`step-${String(s.number).padStart(2,'0')}.html`),'utf8');for(const id of ['timeline','scroll-mode','play','restart','scene'])assert.ok(html.includes(`id="${id}"`));assert.ok(html.includes('aria-label="Color roles"'));assert.equal((html.match(/data-phase=/g)||[]).length,4);assert.ok(!html.includes('<details'))}});
test('Generated pages link to existing local files and code line anchors',()=>{const names=['index.html','reference.html','research.html',...story.map(s=>`step-${String(s.number).padStart(2,'0')}.html`),...Object.values(symbols).map(s=>`code-${s.file}.html`)];for(const name of new Set(names)){const html=fs.readFileSync(path.join(dist,name),'utf8');for(const m of html.matchAll(/(?:href|src)="([^"]+)"/g)){const url=m[1];if(/^(https?:|#|data:)/.test(url))continue;const [file,hash]=url.split('#');assert.ok(fs.existsSync(path.join(dist,file)),`${name}: ${url}`);if(hash&&file.startsWith('code-'))assert.ok(fs.readFileSync(path.join(dist,file),'utf8').includes(`id="${hash}"`),url)}}});
test('Arithmetic and document counterexamples have their required context',()=>{assert.ok(story[13].frames[3].nodes.some(n=>n.detail==='base(F) = 0'));assert.ok(story[19].frames[2].formula.includes('| --- | --- |'));assert.ok(story[9].frames[1].copy.includes('row schema'))});
test('Motion can pause and respects reduced-motion preference',()=>{const js=fs.readFileSync(path.join(dist,'atlas.js'),'utf8'),css=fs.readFileSync(path.join(dist,'atlas-next.css'),'utf8');assert.ok(js.includes('prefers-reduced-motion: reduce'));assert.ok(js.includes('clearTimeout(timer)'));assert.ok(css.includes('animation-play-state:paused'));assert.ok(css.includes('@media(prefers-reduced-motion:reduce)'))});

test('All lessons include an example extracted from the compilable project',()=>{
 const samples=JSON.parse(fs.readFileSync(path.join(dist,'usage-examples.json')));
 const raw=fs.readFileSync(path.join(dist,'code/UsageExamples.cs'),'utf8').split('\n');
 assert.equal(samples.filter(x=>x.language==='csharp').length,19);
 for(const x of samples.filter(x=>x.line)){
  const excerpt=raw.slice(x.line-1,x.line-1+x.text.split('\n').length).map(line=>line.slice(4)).join('\n');
  assert.equal(excerpt,x.text);
 }
 for(let n=1;n<=21;n++){
  const sample=samples.find(x=>x.steps.includes(n));assert.ok(sample);
  const page=fs.readFileSync(path.join(dist,`step-${String(n).padStart(2,'0')}.html`),'utf8');
  assert.ok(page.includes(`id="sample-${sample.id}"`));
 }
});
test('Example hub, portable fixtures and source anchors resolve locally',()=>{
 const names=fs.readdirSync(dist).filter(x=>x==='code-examples.html'||x==='code-UsageExamples.cs.html'||x.startsWith('example-'));
 for(const name of names){
  const page=fs.readFileSync(path.join(dist,name),'utf8');
  for(const [,href] of page.matchAll(/(?:href|src)="([^"]+)"/g)){
   if(/^(https?:|data:)/.test(href))continue;
   const [file,anchor]=href.split('#'),target=path.join(dist,file||name);
   assert.ok(fs.existsSync(target),`${name}: ${href}`);
   if(anchor)assert.ok(fs.readFileSync(target,'utf8').includes(`id="${anchor}"`),`${name}: ${href}`);
  }
 }
 assert.ok(!Object.values(symbols).some(x=>/AssemblyInfo|GlobalUsings|AssemblyAttributes/.test(x.file)));
});
