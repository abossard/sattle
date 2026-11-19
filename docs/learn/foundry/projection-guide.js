// Every capability appears once in the tree. Relationships outside the
// spanning forest point back to that same capability, even across cycles.
function projectionExplorer(host) {
  const p=RetailProjection, groups=new Map(p.groups.map(group=>[group.id,group]));
  const nodes=new Map(p.groups.flatMap(group=>group.nodes.map(node=>[node.id,node])));
  const outgoing=new Map(p.groups.map(group=>[group.id,[]]));
  for(const link of p.links)outgoing.get(link.from).push(link);

  function add(parent,tag,words,cls) {
    const element=document.createElement(tag);
    if(cls)element.className=cls;
    if(words)element.textContent=words;
    parent.append(element);
    return element;
  }
  function disclosure(parent,words,cls) {
    const el=add(parent,"details","",cls);
    add(el,"summary",words);
    return el;
  }
  function quantity(count,noun,plural=noun+"s") {return count+" "+(count===1?noun:plural);}
  function evidence(parent,triples,sourceIndices) {
    const list=add(parent,"ul","","projection-evidence");
    triples.forEach(([a,b,kind],i)=>{
      const item=add(list,"li");
      item.dataset.sourceIndex=sourceIndices[i];
      add(item,"span",nodes.get(a).label+" ");
      add(item,"strong",kind);
      add(item,"span"," "+nodes.get(b).label+".");
      add(item,"small",p.roleOf[kind]+" · Source link "+(sourceIndices[i]+1));
    });
  }
  function forest(first) {
    const roots=[],parent=new Map();
    for(const start of [first,...groups.keys()]) {
      if(parent.has(start))continue;
      parent.set(start,null);
      roots.push(start);
      const queue=[start];
      for(let i=0;i<queue.length;i++) {
        for(const link of outgoing.get(queue[i])) {
          if(parent.has(link.to))continue;
          parent.set(link.to,link.from);
          queue.push(link.to);
        }
      }
    }
    return {roots,parent};
  }
  const root=document.createElement("section");
  root.className="projection-browser case-inspect";
  add(root,"h2","Explore the capability map");
  const nSources=p.internal.length+p.links.reduce((sum,link)=>sum+link.evidence.length,0);
  add(root,"p",nodes.size+" services, tools and records form "+groups.size+" software capabilities. The "+nSources+
    " original links support "+p.links.length+" connections between capabilities and "+
    p.internal.length+" relationships inside them.");
  const menu=add(root,"label","","projection-start");
  add(menu,"span","Start the tree at");
  const select=add(menu,"select");
  for(const {id,label} of p.groups)select.add(new Option(label,id));
  select.value="payout";
  add(root,"p","Open an arrow to see its original relationships. A shared capability appears once in the tree. Cross-references take you to it.","projection-hint");
  const semantics=disclosure(root,"What do the arrows mean?","projection-hint");
  add(semantics,"p","Read from the source to the destination. A job can read a case, require approval or publish an event. Each verb stays with its source record. A health rule decides which failures affect a capability.");
  const tree=add(root,"div","","projection-tree");

  function render() {
    const start=select.value,{roots,parent}=forest(start),locations=new Map();
    tree.replaceChildren();

    function follow(groupId) {
      const target=locations.get(groupId);
      for(let ancestor=target;ancestor&&ancestor!==root;ancestor=ancestor.parentElement) {
        if(ancestor.tagName==="DETAILS")ancestor.open=true;
      }
      target.firstElementChild.focus({preventScroll:true});
      target.scrollIntoView({block:"nearest"});
    }

    function branch(container,id) {
      const group=groups.get(id);
      const entry=disclosure(container,group.label,"projection-group");
      entry.dataset.capability=id;
      locations.set(id,entry);
      if(id===start)entry.open=true;
      const local=disclosure(entry,"Inside "+group.label+" · "+quantity(group.nodes.length,"entity","entities"),"projection-inside");
      const members=add(local,"ul","","projection-members");
      for(const node of group.nodes)add(members,"li",node.label);
      if(group.internal.length) {
        const within=disclosure(local,quantity(group.internal.length,"internal relationship"),"projection-internal");
        evidence(within,group.internal,group.internalSourceIndices);
      } else add(local,"p","This capability has no internal links in this graph.");
      const links=outgoing.get(id);
      if(!links.length)add(entry,"p","This capability has no outgoing links in this graph.","projection-hint");
      for(const link of links) {
        const target=groups.get(link.to),isTreeLink=parent.get(target.id)===id;
        const row=disclosure(entry,"→ "+target.label+" · "+quantity(link.evidence.length,"source link"),"projection-link");
        row.dataset.from=id;
        row.dataset.to=target.id;
        if(id===start&&id==="payout"&&target.id==="intake")row.open=true;
        add(row,"p",group.label+" → "+target.label,"projection-direction");
        evidence(row,link.evidence,link.sourceIndices);
        if(isTreeLink)branch(row,target.id);
        else {
          const jump=add(row,"button","Show "+target.label+" in the tree","projection-crossref");
          jump.type="button";
          jump.dataset.crossReference=target.id;
          jump.addEventListener("click",()=>follow(target.id));
        }
      }
    }

    branch(tree,roots[0]);
    if(roots.length>1) {
      const other=disclosure(tree,"Other starting points · "+(roots.length-1),"projection-other");
      for(const id of roots.slice(1))branch(other,id);
    }
  }
  select.addEventListener("change",render);
  render();
  if(host)host.append(root);
  return root;
}
