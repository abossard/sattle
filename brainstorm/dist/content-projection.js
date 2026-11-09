/* Pure projection of the atlas content. This is a navigation demonstration. */
(function(root){
 function project(model, expanded, enabled){
  const nodes=new Map(),edges=new Map();
  function visit(id){const n=model.get(id);if(!n||nodes.has(id))return;nodes.set(id,n);if(!expanded.has(id))return;for(const child of n.children||[]){const c=model.get(child);if(c.kind==='collection'&&!enabled.has(c.collection))continue;edges.set(id+'>'+child,{id:id+'>'+child,source:id,target:child});visit(child)}}
  visit('atlas');return {nodes,edges};
 }
 function diff(before,after){const compare=(a,b)=>({added:[...b.keys()].filter(id=>!a.has(id)),removed:[...a.keys()].filter(id=>!b.has(id)),unchanged:[...b.keys()].filter(id=>a.has(id))});return {nodes:compare(before.nodes,after.nodes),edges:compare(before.edges,after.edges)}}
 function focus(model,path){
  const nodes=new Map(),edges=new Map();
  const chain=['atlas',...path];
  for(let i=0;i<chain.length;i++){
   const n=model.get(chain[i]);if(!n)throw Error('Unknown content node');
   if(i&&!model.get(chain[i-1]).children?.includes(n.id))throw Error('Invalid content path');
   nodes.set(n.id,n);if(i)edges.set(chain[i-1]+'>'+n.id,{id:chain[i-1]+'>'+n.id,source:chain[i-1],target:n.id});
  }
  const parent=model.get(chain.at(-1));
  for(const id of parent.children||[]){
   const child=model.get(id);nodes.set(id,child);edges.set(parent.id+'>'+id,{id:parent.id+'>'+id,source:parent.id,target:id});
   // A topic shows its chapter headings and all item titles, without opening their contents.
   if(parent.kind==='topic'&&child.kind==='collection')for(const item of child.children||[]){nodes.set(item,model.get(item));edges.set(id+'>'+item,{id:id+'>'+item,source:id,target:item})}
  }
  return {nodes,edges};
 }
 root.AtlasProjection={project,diff,focus};if(typeof module!=='undefined')module.exports=root.AtlasProjection;
})(typeof window==='undefined'?globalThis:window);
