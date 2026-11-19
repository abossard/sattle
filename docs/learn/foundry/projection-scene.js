// One geometry per real entity and one path per real relationship. During the
// fold, parallel edges move onto the same curve before the bundle replaces them.
function dependencyProjectionScene(ctx) {
  const {g,el,txt,box,line,dot,stage,W,H,mobile:m,beat}=ctx;
  const {groups,links,typedLinks}=RetailProjection;
  const source=FoundryCase;
  const paper="#eef3ed",ink="#18333b",quiet="#445e63",navy="#255675",violet="#67458c",rust="#944f34",teal="#086959";
  const palette=["#246a83","#79528b","#147569","#936039","#9d4560","#516da3","#726b23","#477a49","#815b42"];
  const margin=m?12:20, header=m?59:64, graphW=m?W:736;
  const graphTop=m?83:95, graphBottom=m?302:350;
  const midX=graphW/2, midY=(graphTop+graphBottom)/2;
  const plotCircle={x:midX,y:midY,rx:m?136:201,ry:m?90:108};
  const groupById=new Map(groups.map(c=>[c.id,c]));
  const nodeById=new Map(source.nodes.map(n=>[n.id,n]));
  const image=new Map(groups.flatMap(c=>c.nodes.map(n=>[n.id,c.id])));
  const selection=links.find(e=>e.from==="payout"&&e.to==="intake");
  // Show a second parent from the actual projected graph.
  const incoming=links.filter(e=>e.to===selection.to&&e.from!==selection.from);
  const shared=incoming.find(e=>e.from==="support") || incoming[0];
  const internal=groups.reduce((n,c)=>n+c.internal.length,0);
  const external=links.reduce((n,e)=>n+e.evidence.length,0);
  const captions={customer:"Client & chat",agents:"Microsoft Foundry",tools:"Tools & sandbox",systems:"External systems",jobs:"Background jobs",data:"Stored records",commerce:"Order services",fulfillment:"Logistics APIs",engagement:"CRM & analytics"};
  const draw=(p,x,y,s,size=18,color=ink,weight=550,anchor="start")=>txt(p,x,y,s,{fill:color,"font-size":size,"font-weight":weight,"text-anchor":anchor});
  const group=(p=g,extra={})=>el(p,"g",extra);
  const path=(p,d,attrs={})=>el(p,"path",{d,fill:"none","stroke-linecap":"round","stroke-linejoin":"round",...attrs});
  const stroke=(p,x1,y1,x2,y2,c=quiet,width=1)=>line(p,x1,y1,x2,y2,{stroke:c,"stroke-width":width});
  const circle=(p,x,y,r,fill,stroke="none",w=1)=>dot(p,x,y,r,{fill,stroke,"stroke-width":w});
  const opacity=(p,v)=>p.setAttribute("opacity",v);
  const lerp=(a,b,t)=>a+(b-a)*t;
  const ease=t=>t*t*(3-2*t);
  const point=(angle,k=1)=>({x:plotCircle.x+plotCircle.rx*k*Math.cos(angle),y:plotCircle.y+plotCircle.ry*k*Math.sin(angle)});
  const focusFrom=selection.from,focusTo=selection.to;
  const selectedColor=id=>id===focusFrom?rust:id===focusTo?teal:beat===2&&shared&&id===shared.from?violet:navy;
  function arc(start,end,r=1) {
    const a=point(start,r),b=point(end,r);
    return `M${a.x},${a.y} A${plotCircle.rx*r},${plotCircle.ry*r} 0 ${end-start>Math.PI?1:0} 1 ${b.x},${b.y}`;
  }
  const branchPath=d=>`M${d.ax},${d.ay} Q${d.cx},${d.cy} ${d.bx},${d.by}`;
  const bg=box(g,0,0,W,H,{rx:11,fill:paper});
  el(bg,"title",{},"Illustrative retail master graph and the capability graph computed from it");
  draw(g,margin,m?28:34,["The source graph","Folding the graph","The capability graph"][beat],m?23:29,ink,720);
  const countLine=beat===0?source.nodes.length+" entities  ·  "+source.edges.length+" typed links":
    beat===1?source.nodes.length+" entities → "+groups.length+" capabilities":
    groups.length+" capabilities  ·  "+typedLinks.length+" typed links  ·  "+links.length+" bundles";
  draw(g,m?margin:W-margin,m?51:34,countLine,m?15:19,quiet,600,m?"start":"end");
  stroke(g,margin,header,W-margin,header,"#b0c3b6");
  if(!m)stroke(g,graphW+10,header+9,graphW+10,H-9,"#a6bcb0");
  else stroke(g,margin,graphBottom+18,W-margin,graphBottom+18,"#a6bcb0");
  const ring=group(), wire=group(),out=group(),marks=group(),tags=group(),capLabels=group();
  const sourceGroups=[],rawPoints=new Map();
  let start=-Math.PI/2;
  for(const [i,bucket] of source.groups.entries()) {
    const members=source.nodes.filter(n=>n.group===bucket.id),span=2*Math.PI*members.length/source.nodes.length;
    const col=palette[i%palette.length],gap=.047;
    path(ring,arc(start+gap,start+span-gap,1.09),{stroke:col,"stroke-width":m?4:6});
    const pos=point(start+span/2,1.19);
    draw(tags,pos.x,pos.y+5,String(i+1),m?14:16,col,750,"middle");
    members.forEach((node,j)=>{
      const theta=start+(j+.5)*span/members.length;
      rawPoints.set(node.id,{...point(theta),hue:col});
    });
    sourceGroups.push({members,bucket,col});
    start+=span;
  }

  // Two columns leave all capability names on screen at phone size. A line
  // can cross the gutter in either direction; within-column links bend inward.
  const leftIds=["checkout","fulfillment","logistics","support","intake","payout","crm"];
  const rightIds=groups.map(c=>c.id).filter(id=>!leftIds.includes(id));
  const centers=new Map();
  for(const [side,bank] of [leftIds,rightIds].entries())bank.forEach((id,i)=>{
    const y=lerp(graphTop,graphBottom,i/Math.max(1,bank.length-1)),x=side?graphW-(m?145:185):(m?143:185);
    centers.set(id,{x,y,side});
  });
  function lifted(from,to) {
    const a=centers.get(from),b=centers.get(to);
    const same=a.side===b.side, inset=a.side?-1:1;
    const cx=same?a.x+inset*(m?40:68)+inset*Math.abs(a.y-b.y)*.33:(a.x+b.x)/2;
    const cy=same?(a.y+b.y)/2:(a.y+b.y)/2+(from<to?13:-13);
    return {ax:a.x,ay:a.y,cx,cy,bx:b.x,by:b.y};
  }
  function destination(from,to) {
    if(from!==to)return lifted(from,to);
    const a=centers.get(from);
    return {ax:a.x,ay:a.y,cx:a.x,cy:a.y,bx:a.x,by:a.y};
  }
  const evidenceKey=(a,b)=>a+"@"+b;
  const supportSet=new Set(selection.evidence.map(([a,b])=>evidenceKey(a,b)));
  const rawEdges=source.edges.map(([aId,bId,verb])=>{
    const a=rawPoints.get(aId),b=rawPoints.get(bId);
    const initial={ax:a.x,ay:a.y,cx:midX+(a.x+b.x-2*midX)*.28,cy:midY+(a.y+b.y-2*midY)*.28,bx:b.x,by:b.y};
    const final=destination(image.get(aId),image.get(bId)),active=supportSet.has(evidenceKey(aId,bId));
    const p=path(wire,branchPath(initial),{stroke:active?rust:"#62858b","stroke-width":active?2.8:m?.85:1.3,"stroke-opacity":active?1:.47,"data-raw-link":verb});
    el(p,"title",{},nodeById.get(aId).label+" "+verb+" "+nodeById.get(bId).label);
    return {p,initial,final,aId,bId,verb};
  });
  const bundleShapes=links.map(bundle=>{
    const selected=bundle===selection,second=beat===2&&bundle===shared;
    const col=selected?rust:second?violet:"#587882";
    const coords=lifted(bundle.from,bundle.to);
    return {bundle,coords,selected,second,col};
  });
  // Draw the chosen links last so a shared path stays visible in a dense graph.
  bundleShapes.sort((a,b)=>(a.selected?2:a.second?1:0)-(b.selected?2:b.second?1:0));
  for(const b of bundleShapes) {
    const {bundle,coords,selected,second,col}=b;
    const edge=path(out,branchPath(coords),{stroke:col,"stroke-width":selected?5:second?3.5:1.5,"stroke-opacity":selected||second?1:.61,"data-capability-link":bundle.from+"→"+bundle.to});
    b.edge=edge;
    el(edge,"title",{},groupById.get(bundle.from).label+" → "+groupById.get(bundle.to).label+" · "+bundle.evidence.length+" source links: "+bundle.kinds.join(", "));
    if(selected||second) {
      // A directional arrow in the body of the curve is visible past the node.
      const length=edge.getTotalLength(),p=edge.getPointAtLength(length*.71),q=edge.getPointAtLength(length*.71-2);
      path(out,"M-6,-4 L0,0 -6,4",{stroke:col,"stroke-width":selected?3:2.5,transform:`translate(${p.x},${p.y}) rotate(${Math.atan2(p.y-q.y,p.x-q.x)*180/Math.PI})`});
    }
  }
  const rawMarks=source.nodes.map(n=>{
    const a=rawPoints.get(n.id),b=centers.get(image.get(n.id));
    const mark=circle(marks,a.x,a.y,m?4:5,a.hue,paper,1);
    mark.setAttribute("data-raw-entity",n.id);el(mark,"title",{},n.label+" → "+groupById.get(image.get(n.id)).label);
    return {mark,a,b};
  });
  for(const cap of groups) {
    const c=centers.get(cap.id),col=selectedColor(cap.id);
    const node=group(capLabels,{"data-capability":cap.id});
    const radius=(m?3.9:5)+Math.sqrt(cap.nodes.length)*1.1;
    circle(node,c.x,c.y,radius,col,paper,1.3);
    el(node,"title",{},cap.label+" · "+cap.nodes.length+" entities · "+cap.internal.length+" links inside");
    if(cap.id===focusFrom||cap.id===focusTo||beat===2&&shared&&cap.id===shared.from)circle(node,c.x,c.y,radius+3,"none",col,1.3);
    draw(node,c.x+(c.side?12:-12),c.y+(m?4:6),cap.label,m?13.5:17,col,cap.id===focusFrom||cap.id===focusTo?740:580,c.side?"start":"end");
  }
  const panel=group(), lx=m?15:graphW+30, top=m?347:101, panelW=W-lx-margin;
  let sourceNote,sharedNote;
  const records=[];
  if(beat===0) {
    draw(panel,lx,top,"Source groups",m?19:23,ink,720);
    sourceGroups.forEach(({bucket,members,col},i)=>{
      const colIndex=m?i%3:0,row=m?Math.floor(i/3):i;
      const cellW=m?(W-24)/3:panelW,px=lx+colIndex*cellW,py=top+(m?24:30)+row*(m?36:27);
      circle(panel,px+8,py-3,m?8:9,col);
      draw(panel,px+8,py+1,String(i+1),m?11:12,"white",750,"middle");
      draw(panel,px+21,py+2,m?(captions[bucket.id]||bucket.label):bucket.label,m?12.8:16.5,ink,600);
      if(!m)draw(panel,W-margin,py+2,String(members.length),16.5,quiet,600,"end");
    });
    sourceNote=draw(panel,lx,H-12,"",m?15:15.5,rust,650);
  }else if(beat===1) {
    draw(panel,lx,top,"What a capability contains",m?19:22,ink,720);
    const lanes=[{id:"payout",members:["refund-job","refund-id"]},{id:"intake",members:["case-store","approval"]}];
    lanes.forEach(({id,members},i)=>{
      const x=m?lx+i*(panelW/2):lx,y=top+(m?23:42)+i*(m?0:68),col=selectedColor(id);
      stroke(panel,x,y,x+(m?panelW/2-12:panelW),y,"#9aaea4");
      draw(panel,x,y+(m?19:23),groupById.get(id).label,m?16:20,col,710);
      draw(panel,x,y+(m?40:47),members.map(key=>nodeById.get(key).label).join(" · "),m?13:16,ink,560);
    });
    const y=m?top+98:top+200;
    draw(panel,lx,y,internal+" links stay inside their capability.",m?15:18,quiet,600);
    draw(panel,lx,y+(m?24:30),external+" cross a group boundary.",m?15:18,quiet,600);
    draw(panel,lx,y+(m?46:59),typedLinks.length+" typed links in "+links.length+" directed bundles.",m?15:17,quiet,600);
  }else {
    draw(panel,lx,top,"Inside one link",m?19:23,ink,740);
    const x=lx+7,yy=top+(m?25:36),bend=15;
    stroke(panel,x,yy+4,x,yy+(m?19:28),rust,2.4);
    stroke(panel,x,yy+(m?19:28),x+bend,yy+(m?19:28),rust,2.4);
    circle(panel,x,yy-4,4,rust);
    draw(panel,x+11,yy,groupById.get(selection.from).label,m?16:19,rust,720);
    const ty=yy+(m?23:33), tx=x+bend+4;
    draw(panel,tx,ty,groupById.get(selection.to).label,m?16:19,teal,720);
    const detailX=tx+6,end=ty+(m?45:80),step=m?20:34;
    stroke(panel,detailX,ty+5,detailX,end-4,"#8eaaa0",2);
    selection.evidence.slice(0,2).forEach(([from,to,kind],i)=>{
      const y=ty+(m?23:40)+i*step;
      const record=group(panel);
      stroke(record,detailX,y-4,detailX+13,y-4,"#8eaaa0",2);
      draw(record,detailX+17,y,nodeById.get(from).label+" "+kind+" "+nodeById.get(to).label,m?14:15,ink,600);
      records.push(record);
    });
    const sy=m?H-26:top+197;
    if(shared) {
      const label=groupById.get(shared.from).label;
      sharedNote=group(panel);
      draw(sharedNote,lx,sy,m?label+" also links to Returns intake.":"Another parent",m?15:18,violet,690);
      if(!m) {
        draw(sharedNote,lx,sy+29,label+" → "+groupById.get(shared.to).label,17,violet,580);
        draw(sharedNote,lx,sy+66,"One capability can appear under several parents.",14.5,quiet,590);
      }
    }
  }
  let previous=-1, previousExample=-1;
  return p=>{
    const f=beat===0?0:beat===2?1:ease(stage(p,.10,.77));
    if(f!==previous) {
      for(const {p:e,initial,final} of rawEdges) {
        const curve={};
        for(const k of Object.keys(initial))curve[k]=lerp(initial[k],final[k],f);
        e.setAttribute("d",branchPath(curve));
      }
      rawMarks.forEach(({mark,a,b})=>{mark.setAttribute("cx",lerp(a.x,b.x,f));mark.setAttribute("cy",lerp(a.y,b.y,f));});
      opacity(tags,1-stage(f,0,.16));opacity(ring,1-stage(f,0,.55));
      opacity(marks,1-stage(f,.60,.97));opacity(wire,1-stage(f,.80,1));
      opacity(out,stage(f,.60,.94));opacity(capLabels,stage(f,.60,.91));
      previous=f;
    }
    if(beat===0) {
      const example=p<.43?0:p<.80?1:2;
      if(example!==previousExample) {
        const selected=[["warehouse-api","carrier-api"],["refund-job","case-store"]][example];
        rawEdges.forEach(({p:e,aId,bId})=>{
          const focus=selected&&aId===selected[0]&&bId===selected[1];
          e.setAttribute("stroke",focus?rust:"#62858b");
          e.setAttribute("stroke-width",focus?4:m?.9:1.4);
          e.setAttribute("stroke-opacity",focus?1:selected?.36:.63);
        });
        rawMarks.forEach(({mark},i)=>{
          const focus=selected&&selected.includes(source.nodes[i].id);
          mark.setAttribute("r",focus?(m?8:9):m?4:5);
          mark.setAttribute("stroke",focus?ink:paper);
        });
        sourceNote.textContent=["Warehouse API → Carrier API: shipment request","The refund job reads the case store.","Dots are system entities. Curves are typed links."][example];
        previousExample=example;
      }
    }else if(beat===2) {
      records.forEach((r,i)=>opacity(r,.12+.88*stage(p,.10+i*.24,.28+i*.24)));
      if(sharedNote)opacity(sharedNote,.12+.88*stage(p,.55,.70));
      const extra=bundleShapes.find(b=>b.second);
      if(extra)extra.edge.setAttribute("stroke-opacity",.22+.78*stage(p,.55,.70));
    }
  };
}
