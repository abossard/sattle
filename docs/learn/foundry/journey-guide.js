function exploreJourney() {
  const graph=ShopGraphJourney, byId=new Map(graph.nodes.map(node=>[node.id,node]));
  const host=document.querySelector("#activity");
  host.replaceChildren();
  function add(parent,tag,text) {
    const el=document.createElement(tag);
    if(text!==undefined)el.textContent=text;
    parent.append(el);
    return el;
  }
  const outputs=add(host,"section");
  outputs.className="case-inspect journey-inspector";
  add(outputs,"h2","Read the evidence behind a view");
  const choices=add(outputs,"fieldset");
  add(choices,"legend","Choose a view of the sample window");
  const summary=add(outputs,"p");
  const table=add(outputs,"table");
  const header=add(add(table,"thead"),"tr");
  for(const name of ["Signal or record","Result","Source records"])add(header,"th",name).scope="col";
  const body=add(table,"tbody");
  const inputs=[];
  let displayed=-1;
  function selectView(index) {
    if(displayed===index)return;
    displayed=index;
    inputs[index].checked=true;
    const view=graph.views[index];
    summary.textContent=view.summary;
    body.replaceChildren();
    for(const item of view.rows) {
      const row=add(body,"tr");
      add(row,"th",item.label).scope="row";
      const result=add(row,"td");
      if(item.state) {
        const mark=add(result,"span",item.value);
        mark.dataset.state=item.state;
        mark.title=item.state;
      } else {
        result.textContent=item.value;
      }
      const records=add(row,"td");
      for(const id of item.evidence) {
        const button=add(records,"button",byId.get(id).label);
        button.type="button";
        button.onclick=()=>{picker.value=id;inspect();picker.focus();};
      }
    }
  }
  graph.views.forEach((view,i)=>{
    const label=add(choices,"label"), input=add(label,"input");
    input.type="radio";input.name="shop-view";input.value=view.id;
    label.append(" "+view.label);
    inputs.push(input);
    input.onclick=()=>{player.pause();player.jump(4,graph.viewMoment(i));};
  });
  const evidence=add(host,"section");
  evidence.className="case-inspect journey-inspector";
  add(evidence,"h2","Trace one source record");
  const label=add(evidence,"label","Source record");
  label.htmlFor="source-record";
  const picker=add(evidence,"select");
  picker.id=label.htmlFor;
  const sources=new Map();
  for(const node of graph.nodes) {
    if(!sources.has(node.source)){
      const group=add(picker,"optgroup");group.label=node.source;sources.set(node.source,group);
    }
    sources.get(node.source).append(new Option(node.label,node.id));
  }
  const explanation=add(evidence,"div");
  explanation.setAttribute("aria-live","polite");
  function inspect() {
    const node=byId.get(picker.value);
    explanation.replaceChildren();
    add(explanation,"p",node.source+" · "+node.kind);
    add(explanation,"p",node.detail);
    for(const [heading,edges] of [["Original links",graph.rawEdges],["Identity matches",graph.identityEdges],["Links from joined records",graph.joinedEdges]]) {
      const incident=edges.filter(e=>e.from===node.id||e.to===node.id);
      if(!incident.length)continue;
      add(explanation,"h3",heading);
      const list=add(explanation,"ul");
      for(const e of incident) {
        const li=add(list,"li",byId.get(e.from).label+" → "+byId.get(e.to).label+" · "+e.kind);
        add(li,"small",e.field||e.key);
      }
    }
    for(const missing of graph.unresolved.filter(e=>e.from===node.id)) {
      add(explanation,"p","Unmatched "+missing.field+". No target in this capture has that key.");
    }
  }
  picker.value=graph.example.record;picker.onchange=inspect;inspect();
  selectView(0);
  return (beat,fraction)=>{
    if(beat===4)selectView(graph.viewAt(fraction));
  };
}
