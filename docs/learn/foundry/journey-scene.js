// All five diagrams use the same records. Seeking a beat sets its geometry directly,
// so scrubbing in the guide and rendering frames out of order give the same picture.
function ShopJourneyScene({g, el, txt, box, dot, line, stage, spokenAt, mobile, W, H, beat}) {
  const graph = ShopGraphJourney;
  const background="var(--page)", ink="var(--ink)", muted="var(--muted)";
  const edge="var(--wire)", hairline="var(--line)", blue="var(--blue)";
  const identity="var(--identity)", master="var(--master)";
  const violet="var(--rule)", gold="var(--input)", cyan="var(--cyan)", orange="var(--orange)", indigo="var(--indigo)";
  const technicalColors={azure:blue,kubernetes:violet,network:cyan,dns:master,telemetry:gold,applications:orange};
  const capabilityColors=[cyan,indigo,orange,master,violet,identity,"var(--document)",violet,blue,gold,orange,identity,master,gold,indigo];
  const viewColors={dashboard:gold,health:cyan,kubernetes:violet,azure:blue,dns:master};
  const states={Healthy:"var(--healthy)",Degraded:"var(--degraded)",Unhealthy:"var(--unhealthy)",Unknown:"var(--unknown)"};
  const symbols={Healthy:"✓",Degraded:"▲",Unhealthy:"×",Unknown:"?"};
  const margin = mobile ? 13 : 25;
  const nodes = graph.nodes;
  const byId = new Map(nodes.map(n=>[n.id,n]));
  const allEdges = [...graph.rawEdges,...graph.identityEdges,...graph.joinedEdges];
  const group = (parent, attrs={})=>el(parent,"g",attrs);
  const text = (parent,x,y,s,size=17,color=ink,weight=550,anchor="start")=>
    txt(parent,x,y,s,{"font-size":size,fill:color,"font-weight":weight,"text-anchor":anchor});
  const wire = (parent,attrs={})=>el(parent,"path",{fill:"none","stroke-linecap":"round","stroke-linejoin":"round",...attrs});
  const ease = value=>value*value*(3-2*value);
  const lerp = (a,b,p)=>a+(b-a)*p;
  const shift = (a,b,p)=>({x:lerp(a.x,b.x,p),y:lerp(a.y,b.y,p)});
  const segment = (a,b)=>"M"+a.x.toFixed(1)+" "+a.y.toFixed(1)+"L"+b.x.toFixed(1)+" "+b.y.toFixed(1);
  const graphPath = (edges,positions)=>edges.map(e=>segment(positions.get(e.from),positions.get(e.to))).join(" ");
  const title = (node,content)=>el(node,"title",{},content);
  const opacity = (node,alpha)=>node.setAttribute("opacity",alpha);
  const setD = (node,d)=>node.setAttribute("d",d);
  const setXY = (node,p)=>{node.setAttribute("cx",p.x);node.setAttribute("cy",p.y);};
  const fit = (node,available)=>{
    // Compress only labels that actually exceed their column. SVG textLength
    // remains readable when the parent SVG scales or is cloned for enlargement.
    if(node.getComputedTextLength()>available) {
      node.setAttribute("textLength",available);
      node.setAttribute("lengthAdjust","spacingAndGlyphs");
    }
    return node;
  };
  function header(label,count) {
    text(g,margin,mobile?27:32,label,mobile?23:29,ink,650);
    const source=text(g,mobile?margin:W-margin,mobile?51:31,count,mobile?15.5:17,muted,550,mobile?"start":"end");
    line(g,margin,mobile?63:48,W-margin,mobile?63:48,{stroke:hairline,"stroke-width":1});
    return source;
  }
  function hash(id) {
    let value=2166136261;
    for(const c of id)value=Math.imul(value^c.charCodeAt(0),16777619);
    value^=value>>>16;
    return value>>>0;
  }
  const rawArea = {x:margin+9,y:mobile?71:58,w:mobile?W-2*margin-18:W*.66,h:mobile?226:H-91};
  // Record order in a collector is arbitrary. A fixed shuffle keeps each record
  // in place when the identity rules add links, independent of its source.
  function scattered() {
    const indexed=nodes.map(n=>({n,h:hash(n.id)})).sort((a,b)=>a.h-b.h);
    const cols=Math.ceil(Math.sqrt(nodes.length*rawArea.w/rawArea.h)),rows=Math.ceil(nodes.length/cols);
    return new Map(indexed.map(({n,h},i)=>{
      const dx=((h&255)/255-.5)*.7,dy=(((h>>>8)&255)/255-.5)*.7;
      return [n.id,{x:rawArea.x+(i%cols+.5+dx)*rawArea.w/cols,y:rawArea.y+(Math.floor(i/cols)+.5+dy)*rawArea.h/rows}];
    }));
  }
  function partition(model,cols,compact) {
    const top=mobile?69:54,bottom=H-(mobile?42:39),gap=mobile?7:11;
    const rows=Math.ceil(model.groups.length/cols),width=(W-2*margin-(cols-1)*gap)/cols,height=(bottom-top-(rows-1)*gap)/rows;
    const positions=new Map(), centers=new Map(),colors=new Map(),cells=[];
    model.groups.forEach((bucket,i)=>{
      const x=margin+(i%cols)*(width+gap),y=top+Math.floor(i/cols)*(height+gap);
      const headerH=compact?(mobile?23:26):(mobile?30:33);
      const center={x:x+width*.5,y:y+headerH+(height-headerH)*.47};
      const color=compact?capabilityColors[i%capabilityColors.length]:technicalColors[bucket.id];
      const n=bucket.members.length,rw=width*.42,rh=Math.max(5,(height-headerH)*.39);
      bucket.members.forEach((id,j)=>{
        // Vogel's spiral spreads members evenly inside an ellipse, including the center.
        const radius=n===1?0:Math.sqrt((j+.4)/n),angle=j*2.39996;
        positions.set(id,{x:center.x+rw*radius*Math.cos(angle),y:center.y+rh*radius*Math.sin(angle)});
        colors.set(id,color);
      });
      centers.set(bucket.id,center);
      cells.push({bucket,x,y,width,height,center,color});
    });
    return {positions,centers,colors,cells};
  }
  function drawCells(layout,compact) {
    const panels=group(g),labels=group(g);
    layout.cells.forEach(({bucket,x,y,width,height,color})=>{
      box(panels,x,y,width,height,{fill:color,"fill-opacity":.17,stroke:color,"stroke-opacity":.8,rx:4});
      const bandH=compact?(mobile?23:26):(mobile?28:31);
      box(labels,x,y,width,bandH,{fill:color,"fill-opacity":.87,rx:3});
      const label=text(labels,x+(mobile?6:10),y+(mobile?18:21),bucket.label,
                       compact?(mobile?14:16):(mobile?15.5:19),background,700);
      fit(label,width-(mobile?11:20));
    });
    return {panels,labels};
  }
  box(g,0,0,W,H,{fill:background,rx:0});
  const raw=scattered();
  const {record:arg,peer:monitor,dns,address,endpoint}=graph.example;

  if(beat===0||beat===1) {
    const matched=beat===1, nRaw=graph.rawEdges.length, nLoose=graph.stats.isolated;
    const source=header(matched?"Resolve the record identities":"Source records",
      matched?"":nodes.length+" records   ·   "+nRaw+" supplied links");
    if(matched){
      el(source,"tspan",{fill:identity},graph.identityEdges.length+" identity links");
      el(source,"tspan",{},"   ·   ");
      el(source,"tspan",{fill:master},graph.joinedEdges.length+" joined links");
    }
    const pointInk=matched?ink:"rgb(from var(--ink) g g g)";
    const neutralWire=matched?edge:"rgb(from var(--wire) g g g)";
    const strokes=group(g), marks=group(g), annotation=group(g);
    wire(strokes,{d:graphPath(graph.rawEdges,raw),stroke:neutralWire,"stroke-opacity":matched?.4:.66,"stroke-width":1.1});
    const joinLayer=wire(strokes,{stroke:matched?master:edge,"stroke-width":mobile?1.35:1.6,"stroke-opacity":.60});
    const identityLayer=wire(strokes,{stroke:matched?identity:edge,"stroke-width":mobile?2.2:2.65,"stroke-opacity":.96});
    const circles=nodes.map(n=>{
      const point=raw.get(n.id);
      const c=dot(marks,point.x,point.y,mobile?2.8:3.5,{fill:pointInk,stroke:background,"stroke-width":.7});
      title(c,n.source+" · "+n.label);
      return c;
    });
    const activePath=wire(annotation,{stroke:matched?identity:pointInk,"stroke-width":2.8});
    const rings=[0,1,2,3].map(()=>dot(annotation,0,0,mobile?7:9,{stroke:matched?identity:pointInk,fill:background,"stroke-width":2}));
    const dividerX=W*.70, px=mobile?margin:dividerX+23,py=mobile?321:88;
    if(mobile)line(g,margin,py-13,W-margin,py-13,{stroke:hairline});
    else line(g,dividerX,62,dividerX,H-22,{stroke:hairline});
    const note=group(g);
    const roleFill=matched?box(note,px-8,py-21,W-margin-px+8,mobile?179:238,{rx:4,"fill-opacity":.13}):null;
    const label=text(note,px,py,matched?"Shared identifiers":"A closer look",mobile?16:17,muted,600);
    const record=text(note,px,py+(mobile?30:43),"",mobile?24:26,ink,700);
    const first=text(note,px,py+(mobile?59:80),"",mobile?17:18,muted);
    const second=text(note,px,py+(mobile?83:105),"",mobile?17:18,muted);
    const ruleY=py+(mobile?104:137);
    line(note,px,ruleY,W-margin,ruleY,{stroke:hairline});
    const evidence=text(note,px,ruleY+(mobile?24:34),"",mobile?16:17,matched?master:ink,650);
    const detail=text(note,px,ruleY+(mobile?47:61),"",mobile?16:17,muted);
    const indexFor=id=>nodes.findIndex(n=>n.id===id);
    const shownWidth=W-px-margin;
    const examples=matched ? [
      {start:0, ids:[arg,monitor], words:[
        "Shared identifiers","shopreceipts","Azure Resource Graph · …/shopreceipts","Azure Monitor · …/shopreceipts","Same Azure resource ID","Two descriptions identify one account."]},
      {start:spokenAt("Now follow the DNS name",.55), ids:[dns,address,endpoint,arg], words:[
        "DNS and private endpoint","receipts.shop.internal","A record   →   10.42.2.8","Private endpoint   →   shopreceipts","The DNS answer supplies the address.","The endpoint names the storage resource."]}
    ] : [
      {start:0, ids:["span-2"], detail:"10:12 UTC · Payment API timed out."},
      {start:spokenAt("In Kubernetes",.53), ids:["k8s-refund-job-rs","k8s-refund-job-deploy"], detail:"Its owner reference names Deployment refund."},
      {start:spokenAt("A network flow",.70), ids:["flow-tool-handler-orders-a"], detail:"A flow reports two network endpoints."},
      {start:spokenAt("We call a link",.84), ids:["k8s-refund-job-rs","k8s-refund-job-deploy"], detail:"Edge type · controlled by"}
    ];
    let previous=-1,previousIdentities=-1,previousJoins=-1;
    function changeNote(which) {
      const {ids,words,detail:recordDetail}=examples[which], selected=byId.get(ids[0]);
      const noteLines=words || [selected.source,selected.label,selected.kind,recordDetail,
                                nLoose+" records have no supplied link.","A dot represents one source record."];
      for(const [i,node] of [label,record,first,second,evidence,detail].entries()) {
        node.textContent=noteLines[i];node.removeAttribute("textLength");fit(node,shownWidth);
      }
      const color=matched?(which===0?identity:master):pointInk;
      if(roleFill){
        roleFill.setAttribute("fill",color);
        label.setAttribute("fill",color);
      }
      evidence.setAttribute("fill",color);
      activePath.setAttribute("stroke",color);
      setD(activePath,ids.slice(1).map((id,i)=>segment(raw.get(ids[i]),raw.get(id))).join(" "));
      rings.forEach((ring,i)=>{
        if(i<ids.length){setXY(ring,raw.get(ids[i]));ring.setAttribute("stroke",color);}
        opacity(ring,i<ids.length?1:0);
      });
      // Emphasise the anchored records without recoloring an entire source.
      circles.forEach(c=>c.setAttribute("fill",pointInk));
      ids.forEach(id=>circles[indexFor(id)].setAttribute("fill",color));
    }
    return progress=>{
      const which=examples.findLastIndex(example=>progress>=example.start);
      if(which!==previous){changeNote(which);previous=which;}
      if(matched) {
        const count=Math.round(stage(progress,0,.39)*graph.identityEdges.length);
        if(count!==previousIdentities){setD(identityLayer,graphPath(graph.identityEdges.slice(0,count),raw));previousIdentities=count;}
        const links=Math.round(stage(progress,.20,.76)*graph.joinedEdges.length);
        if(links!==previousJoins){setD(joinLayer,graphPath(graph.joinedEdges.slice(0,links),raw));previousJoins=links;}
      }
      const start=examples[which].start, end=examples[which+1]?.start ?? 1;
      const pulse=stage(progress,start,Math.min(start+.2,end));
      rings.forEach(ring=>ring.setAttribute("r",(mobile?7:9)+3*Math.sin(Math.PI*pulse)));
    };
  }

  const technical=partition(graph.technical,mobile?2:3,false);
  const capabilities=partition(graph.capabilities,mobile?3:5,true);
  if(beat===2||beat===3) {
    const isTechnical=beat===2,layout=isTechnical?technical:capabilities;
    const from=isTechnical?raw:technical.positions, to=layout.positions;
    const model=isTechnical?graph.technical:graph.capabilities;
    header(isTechnical?"Group by technical layer":"Group by capability",isTechnical?
      model.groups.length+" technical groups   ·   "+nodes.length+" records":
      model.groups.length+" groups   ·   "+model.edges.length+" group connections");
    const before=isTechnical?null:drawCells(technical,false);
    const {panels,labels}=drawCells(layout,!isTechnical);
    const wires=group(g), atoms=group(g),bundles=group(g),selected=group(g);
    const thin=wire(wires,{stroke:edge,"stroke-width":mobile?.8:1,"stroke-opacity":.48});
    const circles=nodes.map(n=>{
      const c=dot(atoms,0,0,isTechnical?(mobile?2.6:3.4):(mobile?2.1:3),{fill:layout.colors.get(n.id),stroke:background,"stroke-width":.7});
      title(c,n.label+" · "+n.source);return c;
    });
    g.append(labels);
    const routeColor=isTechnical?master:blue;
    const routes=isTechnical ? [
      {start:0,ids:[dns,address,endpoint,arg],label:"DNS → 10.42.2.8 → private endpoint → storage account"}
    ] : [
      {start:0,ids:["refund-job"],label:"Refund job · Payout"},
      {start:spokenAt("The job reads a case",.28),ids:["refund-job","case-store"],label:"Payout → Returns intake · The refund job reads the case store."},
      {start:spokenAt("Its call to the payment API",.49),ids:["refund-job","processor"],label:"Payout → Payment API · The refund job calls the payment API."}
    ];
    const routeOutline=wire(selected,{stroke:background,"stroke-width":6});
    const routePath=wire(selected,{stroke:routeColor,"stroke-width":3});
    const arrow=wire(selected,{d:"M-7 -4 L0 0 L-7 4",stroke:routeColor,"stroke-width":2.3});
    const highlights=Array.from({length:isTechnical?4:2},()=>dot(selected,0,0,mobile?5:6,{fill:background,stroke:routeColor,"stroke-width":2.6}));
    if(!isTechnical) {
      model.edges.forEach(e=>{
        const width=.75+Math.min(3,Math.sqrt(e.count)*.49);
        const color=layout.colors.get(e.evidence[0].from);
        const link=wire(bundles,{d:segment(layout.centers.get(e.from),layout.centers.get(e.to)),stroke:color,"stroke-opacity":.62,"stroke-width":width});
        title(link,e.count+" source relationship"+(e.count===1?"":"s"));
      });
      layout.cells.forEach(({bucket,center,color})=>{
        const node=dot(bundles,center.x,center.y,(mobile?4.1:5.2)+Math.sqrt(bucket.members.length),{fill:color,stroke:background,"stroke-width":2});
        title(node,bucket.label+" · "+bucket.members.length+" source records");
      });
    }
    const caption=text(g,margin,H-12,"",mobile?15.5:18,routeColor,600);
    const moveStart=isTechnical?spokenAt("It stays connected",.12):spokenAt("It belongs to Payout",.12);
    const moveEnd=moveStart+.5*(1-moveStart);
    let previousMove=-1,previousFold=-1,previousRoute=-1;
    return progress=>{
      const regroup=stage(progress,moveStart,moveEnd),move=ease(regroup);
      const fold=isTechnical?0:ease(stage(progress,moveEnd,moveEnd+.4*(1-moveEnd)));
      const index=routes.findLastIndex(route=>progress>=route.start),route=routes[index];
      if(move!==previousMove) {
        const points=new Map(nodes.map((n,i)=>{
          const pos=shift(from.get(n.id),to.get(n.id),move);
          setXY(circles[i],pos);return [n.id,pos];
        }));
        setD(thin,graphPath(allEdges,points));
      }
      if(index!==previousRoute) {
        caption.textContent=route.label;caption.removeAttribute("textLength");fit(caption,W-2*margin);
      }
      if(index!==previousRoute || move!==previousMove || fold!==previousFold) {
        // Follow the same source records as they move. In the capability graph
        // the selected relation finishes on the groups that own its endpoints.
        const points=route.ids.map(id=>{
          const p=shift(from.get(id),to.get(id),move);
          return fold?shift(p,layout.centers.get(byId.get(id).capability),fold):p;
        });
        const routeLine=points.slice(1).map((p,i)=>segment(points[i],p)).join(" ");
        setD(routePath,routeLine);setD(routeOutline,routeLine);
        highlights.forEach((ring,i)=>{
          if(points[i])setXY(ring,points[i]);
          opacity(ring,points[i]?1:0);
        });
        const directed=!isTechnical && points.length===2;
        opacity(arrow,directed?1:0);
        if(directed){
          const [a,b]=points,mid=shift(a,b,.6),angle=Math.atan2(b.y-a.y,b.x-a.x)*180/Math.PI;
          arrow.setAttribute("transform","translate("+mid.x+" "+mid.y+") rotate("+angle+")");
        }
      }
      previousMove=move;previousFold=fold;previousRoute=index;
      opacity(panels,stage(regroup,0,.8));opacity(labels,stage(regroup,.15,.80));
      if(before){const alpha=1-stage(regroup,0,.28);opacity(before.panels,alpha);opacity(before.labels,alpha);}
      opacity(atoms,1-.74*fold);opacity(wires,1-.84*fold);opacity(bundles,fold);
    };
  }

  header("Five views of the shop","Same source records   ·   five projections");
  const views=graph.views;
  const navX=margin,navY=mobile?75:55,navGap=mobile?8:10;
  const navW=mobile?151:(W-2*margin-4*navGap)/5,navH=mobile?72:68;

  // These thumbnails carry the shape of the result. The selected one is
  // expanded beside them (phone) or directly below (film and desktop).
  function thumbnail(parent,x,y,id,col) {
    const mini=group(parent,{transform:"translate("+x+" "+y+")"});
    const stroke=(x1,y1,x2,y2)=>line(mini,x1,y1,x2,y2,{stroke:col,"stroke-width":2});
    if(id==="dashboard") {
      const samples=views.find(v=>v.id===id).series;
      const pts=samples.map((s,i)=>({x:i*11,y:2+14*(1-s.ok/s.total)}));
      wire(mini,{d:pts.slice(1).map((v,i)=>segment(pts[i],v)).join(" "),stroke:col,"stroke-width":2});
      pts.forEach(p=>dot(mini,p.x,p.y,2.6,{fill:col}));
    } else if(id==="health") {
      stroke(2,2,2,16);stroke(2,10,13,10);stroke(2,16,22,16);
      [[2,2],[13,10],[22,16]].forEach(([x,y])=>dot(mini,x,y,2.4,{fill:col}));
    } else if(id==="kubernetes") {
      stroke(10,2,10,9);stroke(3,9,18,9);stroke(3,9,3,16);stroke(18,9,18,16);
      [[8,0],[0,14],[15,14]].forEach(([x,y])=>box(mini,x,y,6,5,{fill:col,rx:1}));
    } else if(id==="azure") {
      box(mini,0,1,23,17,{fill:"none",stroke:col,"stroke-width":1.6,rx:2});
      [4,12].forEach(x=>box(mini,x,6,5,6,{fill:col,rx:1}));
    } else {
      [2,9,16].forEach(y=>{dot(mini,2,y,1.9,{fill:col});stroke(7,y,22,y);});
    }
  }
  const panels=[],tabs=[];
  const sx=mobile?navX+navW+13:margin,sy=mobile?navY:135;
  const sw=W-margin-sx,sh=H-sy-(mobile?12:16);
  const infoW=mobile?sw:sw*.30,infoX=sx+(mobile?9:18);
  const chartX=mobile?infoX:sx+infoW+20;
  const chartY=mobile?sy+94:sy+18,chartW=W-margin-chartX-(mobile?10:18),chartH=H-chartY-(mobile?23:32);
  const rect=(p,x,y,w,h,color)=>box(p,x,y,w,h,{fill:color,"fill-opacity":.18,stroke:color,"stroke-opacity":.76,rx:3});
  function summary(parent,x,y,copy,width,size,leading) {
    // Measure the live SVG typeface instead of wrapping identifiers by character count.
    let row=text(parent,x,y,"",size,muted), words=[];
    for(const word of copy.split(" ")) {
      row.textContent=[...words,word].join(" ");
      if(words.length && row.getComputedTextLength()>width) {
        row.textContent=words.join(" ");words=[word];y+=leading;row=text(parent,x,y,word,size,muted);
      } else words.push(word);
    }
    return y;
  }
  function dashboard(panel,view) {
    const x=chartX+12, span=chartW-24,y=chartY+58, h=chartH-(mobile?168:127);
    text(panel,chartX,chartY+(mobile?10:14),"Payment API replies",mobile?16:20,ink,650);
    [0,.5,1].forEach(v=>line(panel,x,y+h*(1-v),x+span,y+h*(1-v),{stroke:hairline,"stroke-width":1,"stroke-dasharray":"3 5"}));
    const coords=view.series.map((s,i)=>({x:x+span*(.13+i*.37), y:y+h*(1-s.ok/s.total)}));
    const curve=coords.map((p,i)=>(i?"L":"M")+p.x+" "+p.y).join(" ");
    el(panel,"path",{d:curve+" V"+(y+h)+" H"+coords[0].x+" Z",fill:viewColors.dashboard,"fill-opacity":.25});
    wire(panel,{d:curve,stroke:viewColors.dashboard,"stroke-width":3});
    coords.forEach((p,i)=>{
      const s=view.series[i],color=states[view.rows[i].state];
      dot(panel,p.x,p.y,6.5,{fill:color,stroke:background,"stroke-width":1.7});
      text(panel,p.x,p.y-13,s.ok+" / "+s.total,mobile?16:19,ink,700,"middle");
      text(panel,p.x,y+h+26,s.time,mobile?15:17,muted,590,"middle");
    });
    const tag=text(panel,chartX,y+h+59,"Successful replies / attempts",mobile?15:17,muted);
    fit(tag,chartW);
  }
  function health(panel,view) {
    const spacing=mobile?55:44, x=chartX+(mobile?2:8),y=chartY+26;
    const yFor=i=>y+i*spacing+(i===3?11:0);
    const rows=view.rows;
    rows.forEach((r,i)=>{
      const px=x+(r.depth||0)*(mobile?9:32), py=yFor(i), color=states[r.state]||muted;
      box(panel,px+6,py-(mobile?15:19),chartX+chartW-px-6,mobile?47:36,{fill:color,"fill-opacity":.14,rx:3});
      if(r.depth) {
        const prevx=px-(mobile?9:32),prevy=yFor(i-1);
        wire(panel,{d:"M"+prevx+" "+(prevy+5)+" V"+py+" H"+px,stroke:edge,"stroke-width":1.4});
      } else if(i>0)line(panel,x,py-spacing*.59,chartX+chartW,py-spacing*.59,{stroke:hairline,"stroke-dasharray":"4 5"});
      dot(panel,px,py,4.3,{fill:color});
      const name=text(panel,px+11,py+5,r.label,mobile?15.5:19,ink,620);
      fit(name,mobile?chartW-(px-x)-13:chartW*.66);
      const status=symbols[r.state] ? symbols[r.state]+"  "+r.value : r.value;
      if(mobile)text(panel,px+11,py+25,status,15.5,color,650);
      else text(panel,chartX+chartW-3,py+5,status,19,color,650,"end");
    });
  }
  function kubernetes(panel,view) {
    const rows=view.rows, short=s=>s.replace(/^deployment\/|^replicaset\/|^pod\//i,"");
    const cards=[];
    if(mobile) {
      rows.forEach((r,i)=>{
        const indent=[0,11,22,22][i],cy=chartY+5+i*55;
        cards.push({x:chartX+indent+7,y:cy,w:chartW-indent-9,h:43,r});
      });
    } else {
      const cw=(chartW-70)/3, cy=chartY+chartH*.34;
      rows.forEach((r,i)=>cards.push({x:chartX+Math.min(i,2)*(cw+35),y:i<2?cy:cy+(i===2?-40:40),w:cw,h:64,r}));
    }
    for(const [i,parent] of [[1,0],[2,1],[3,1]]) {
      const a=cards[parent],b=cards[i];
      const d=mobile?"M"+(a.x+3)+" "+(a.y+a.h)+" V"+(b.y+b.h/2)+" H"+b.x:
        "M"+(a.x+a.w)+" "+(a.y+a.h/2)+" H"+(a.x+a.w+17)+" V"+(b.y+b.h/2)+" H"+b.x;
      wire(panel,{d,stroke:viewColors.kubernetes,"stroke-width":2});
    }
    cards.forEach(({x,y,w,h,r})=>{
      rect(panel,x,y,w,h,viewColors.kubernetes);
      text(panel,x+7,y+(mobile?15:21),r.label,mobile?13:15,viewColors.kubernetes,620);
      fit(text(panel,x+7,y+(mobile?34:51),short(r.value),mobile?15.5:20,ink,690),w-14);
    });
  }
  function azure(panel,view) {
    const x=chartX,y=chartY+2,w=chartW,h=Math.min(chartH-5,mobile?254:204);
    rect(panel,x,y,w,h,blue);
    text(panel,x+10,y+22,"rg-shop",mobile?18:21,ink,690);
    const fields=view.rows.slice(0,mobile?4:6),cols=mobile?1:2, rows=Math.ceil(fields.length/cols),rowH=(h-37)/rows;
    text(panel,x+w-9,y+21,fields.length+" of "+view.rows.length+" resources",mobile?13:15,muted,570,"end");
    for(const [i,r] of fields.entries()) {
      const lx=x+11+(i%cols)*w/cols, ly=y+34+Math.floor(i/cols)*rowH;
      line(panel,lx,ly,x+(i%cols+1)*w/cols-8,ly,{stroke:hairline,"stroke-width":1});
      if(mobile) {
        fit(text(panel,lx,ly+13,r.label,12.8,blue,560),w-18);
        fit(text(panel,lx,ly+36,r.value,15,ink,650),w-18);
      } else {
        fit(text(panel,lx,ly+17,r.label,15,blue,550),w/cols-20);
        fit(text(panel,lx,ly+46,r.value,19,ink,660),w/cols-20);
      }
    }
  }
  function dnsView(panel,view) {
    const x=chartX,y=chartY+7,step=mobile?54:49;
    view.rows.forEach((r,i)=>{
      const ry=y+i*step,c=r.state?states[r.state]:master;
      if(i<3 && i>0)line(panel,x+4,ry-step+10,x+4,ry,{stroke:master,"stroke-width":1.5});
      dot(panel,x+4,ry+4,4.3,{fill:c});
      if(mobile) {
        fit(text(panel,x+16,ry+6,r.label,15,muted,520),chartW-16);
        fit(text(panel,x+16,ry+33,r.value,17.5,c,680),chartW-16);
      } else {
        fit(text(panel,x+17,ry+9,r.label,19,ink,600),chartW*.55);
        fit(text(panel,x+chartW*.60,ry+9,r.value,20,c,680),chartW*.40);
      }
      if(i===2)line(panel,x,ry+step-.5*step,x+chartW,ry+step-.5*step,{stroke:hairline,"stroke-dasharray":"3 5"});
    });
  }
  const renderView={dashboard,health,kubernetes,azure,dns:dnsView};
  views.forEach((view,i)=>{
    const nx=navX+(mobile?0:i*(navW+navGap)),ny=navY+(mobile?i*(navH+navGap):0);
    const color=viewColors[view.id];
    const tab=group(g,{"data-view-label":view.id}),bg=box(tab,nx,ny,navW,navH,{rx:3,fill:color,stroke:color});
    const contents=group(tab);
    thumbnail(contents,nx+12,ny+10,view.id,"currentColor");
    const name=text(contents,nx+10,ny+navH-11,view.label,mobile?15:18,"currentColor",650);
    fit(name,navW-20);
    const marker=line(tab,nx+3,ny+navH-3,nx+navW-3,ny+navH-3,{stroke:background,"stroke-width":2});
    tabs.push({bg,contents,marker,color});
    const panel=group(g,{"data-output-view":view.id});
    box(panel,sx,sy,sw,sh,{fill:color,"fill-opacity":.09,rx:4,stroke:color,"stroke-opacity":.65});
    const heading=text(panel,infoX,sy+(mobile?25:36),view.label,mobile?21:26,color,650);
    fit(heading,infoW-(mobile?18:24));
    const paragraph=summary(panel,infoX,sy+(mobile?47:76),view.summary,infoW-(mobile?18:27),mobile?14:18,mobile?18:24);
    if(mobile)line(panel,infoX,Math.max(paragraph+8,chartY-18),sx+sw-9,Math.max(paragraph+8,chartY-18),{stroke:hairline});
    else line(panel,sx+infoW,sy+13,sx+infoW,sy+sh-13,{stroke:hairline});
    renderView[view.id](panel,view);
    panels.push(panel);
  });
  let active=-1;
  return p=>{
    const next=graph.viewAt(p);
    if(next===active)return;
    active=next;
    panels.forEach((panel,i)=>{
      panel.setAttribute("display",i===active?"inline":"none");
      const {bg,contents,marker,color}=tabs[i];
      bg.setAttribute("stroke-opacity",i===active?1:.62);
      bg.setAttribute("fill-opacity",i===active?.92:.15);
      contents.setAttribute("color",i===active?background:color);
      opacity(marker,i===active?1:0);
    });
  };
}
