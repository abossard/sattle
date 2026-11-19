// Figures use the same seek position in the film and the interactive guide.
// Arrows on dependency graphs point from a consumer to the component it needs.
const ShopReplayScenes = (() => {
  const colors = {
    bg:"var(--page)", panel:"var(--surface)", raised:"var(--raised)", ink:"var(--ink)", muted:"var(--muted)",
    line:"var(--line)", accent:"var(--blue)", master:"var(--master)", identity:"var(--identity)",
    added:"var(--added)", policy:"var(--rule)",
    states:{Healthy:"var(--healthy)",Unhealthy:"var(--unhealthy)",Degraded:"var(--degraded)",Unknown:"var(--unknown)"}
  };
  const symbols = {Healthy:"✓", Unhealthy:"×", Degraded:"▲", Unknown:"?"};
  function studio(ctx) {
    const {el,txt,g,box,W,H,mobile:m,stage}=ctx, pad=m?13:25;
    const layer=(parent=g,attrs={})=>el(parent,"g",attrs);
    const write=(parent,x,y,s,size,color=colors.ink,weight=570,anchor="start")=>
      txt(parent,x,y,s,{"font-size":size,fill:color,"font-weight":weight,"text-anchor":anchor});
    const shape=(parent,d,color=colors.line,width=2,extra={})=>
      el(parent,"path",{d,fill:"none",stroke:color,"stroke-width":width,"stroke-linejoin":"round","stroke-linecap":"round",...extra});
    function header(title,source) {
      box(g,0,0,W,H,{fill:colors.bg});
      write(g,pad,m?27:32,title,m?23:29,colors.ink,650);
      write(g,m?pad:W-pad,m?51:31,source,m?15.5:17,colors.muted,550,m?"start":"end");
      shape(g,`M${pad} ${m?63:48}H${W-pad}`,colors.line,1);
    }
    function arrow(parent,x1,y1,x2,y2,color=colors.line,width=2.7) {
      const dx=x2-x1,dy=y2-y1,len=Math.hypot(dx,dy),ux=dx/len,uy=dy/len;
      shape(parent,`M${x1} ${y1}L${x2} ${y2}m${-ux*9-uy*4.5} ${-uy*9+ux*4.5}L${x2} ${y2}l${-ux*9+uy*4.5} ${-uy*9-ux*4.5}`,color,width);
    }
    function block(parent,x,y,w,h,title,detail,color=colors.accent) {
      const row=layer(parent);
      box(row,x,y,w,h,{rx:4,fill:colors.panel,stroke:color,"stroke-width":1.8});
      write(row,x+w/2,y+h*.40,title,m?19:23,colors.ink,700,"middle");
      write(row,x+w/2,y+h*.83,detail,m?15:17,color,600,"middle");
      return row;
    }
    function pulse(parent,a,b,color,r=m?5:6) {
      const spot=el(parent,"circle",{r,fill:color});
      return t=>{
        spot.setAttribute("cx",a[0]+(b[0]-a[0])*t);
        spot.setAttribute("cy",a[1]+(b[1]-a[1])*t);
        spot.setAttribute("opacity",t<=0||t>=1?0:1);
      };
    }
    function opacity(node,value) {node.setAttribute("opacity",value);}
    return {...ctx,colors,pad,m,write,layer,shape,arrow,pulse,header,block,stage,opacity};
  }

  function introduction(s) {
    const {g,W,H,m,pad,header,write,shape,arrow,block,pulse,dot,opacity,layer,stage,colors:c}=s;
    header("Why the database service turns red","pg-shop replay · workload revision 16");
    const cx=m?W/2:283, w=m?275:355;
    const y1=m?76:65,y2=m?203:232,bh=m?67:80;
    const states=ShopReplayCases.rollup.frames.find(r=>r.revision===16).states;
    block(g,cx-w/2,y1,w,bh,"Database service","Depends on pg-shop");
    block(g,cx-w/2,y2,w,bh,"pg-shop",symbols[states["pg-shop"]]+"  "+states["pg-shop"],c.states.Unhealthy);
    const left=cx-56,right=cx+52;
    arrow(g,left,y1+bh+4,left,y2-4,c.accent);
    arrow(g,right,y2-4,right,y1+bh+4,c.states.Unhealthy);
    write(g,left-9,(y1+bh+y2)/2+5,"needs",m?15:17,c.accent,620,"end");
    write(g,right+9,(y1+bh+y2)/2+5,"affects health",m?15:17,c.states.Unhealthy,620);
    const needs=pulse(g,[left,y1+bh+4],[left,y2-4],c.accent);
    const hit=pulse(g,[right,y2-4],[right,y1+bh+4],c.states.Unhealthy);
    const downstream=layer();
    const x=m?pad:610, y=m?295:86;
    if(!m) shape(g,`M553 70V${H-25}`,c.line,1);
    write(downstream,x,y,"Who else relies on it?",m?22:26,c.ink,710);
    const chain=[["pg-shop","PostgreSQL"],["database","Database service"],["api","API service"],["web","Web service"]];
    const lineX=x+(m?17:25),step=m?40:60;
    const items=chain.map(([key,label],i)=>{
      const h=y+(m?30:42)+i*step;
      if(i) arrow(downstream,lineX,h-step+11,lineX,h-11,c.states.Unhealthy,2.4);
      dot(downstream,lineX,h,9,{fill:c.bg,stroke:c.states.Unhealthy,"stroke-width":2.5});
      const record=layer(downstream);
      write(record,lineX+23,h+6,label,m?17:21,c.ink,650);
      write(record,m?W-pad:W-pad-20,h+6,states[key],m?16:19,c.states[states[key]],650,"end");
      return record;
    });
    write(g,pad,H-12,m?"Revision 16   ·   One update for all four":"Revision 16 records the database, API and web as Unhealthy in the same update.",m?16:19,c.muted);
    const blue=s.spokenAt("The blue one",.35), red=s.spokenAt("The red one",.56);
    const rollup=s.spokenAt("That’s health roll-up",.73);
    return p=>{
      needs(stage(p,blue,red));hit(stage(p,red,rollup));
      // Highlight the explanation. The health labels all come from the same revision.
      items.forEach(node=>opacity(node,.78+.22*stage(p,rollup,rollup+.07)));
    };
  }

  function signal(s) {
    const {g,W,H,m,pad,header,write,shape,box,dot,stage,colors:c}=s;
    const rule=ShopReplayCases.rollup.rule, window=ShopReplayCases.rollup.readings.filter(r=>r.minute>="10:09" && r.minute<="10:13");
    const failed=window.find(r=>r.value===0);
    header("The first failed reading","Azure Monitor · "+failed.meter);
    const left=m?56:89, right=m?W-31:690, top=m?105:106, base=m?230:258;
    const x=i=>left+i*(right-left)/(window.length-1), y=v=>base-(base-top)*v;
    for(const v of [0,1]) {
      shape(g,`M${left} ${y(v)}H${right}`,c.line,1.4,{"stroke-dasharray":"4 6"});
      write(g,left-19,y(v)+7,String(v),m?20:25,c.ink,720,"end");
    }
    const d=window.reduce((result,r,i)=>i?result+`H${x(i)}V${y(r.value)}`:`M${x(i)} ${y(r.value)}`,"");
    shape(g,d,c.line,3);
    const elapsed=shape(g,d,c.accent,4,{pathLength:1,"stroke-dasharray":"1 1"});
    window.forEach((r,i)=>{
      dot(g,x(i),y(r.value),m?6:8,{fill:c.bg,stroke:r.value===0?c.states.Unhealthy:c.accent,"stroke-width":3});
      write(g,x(i),base+(m?33:45),r.minute,m?15:18,c.muted,600,"middle");
    });
    const failX=x(window.indexOf(failed));
    const ring=dot(g,failX,y(0),m?13:16,{fill:"none",stroke:c.states.Unhealthy,"stroke-width":3});
    write(g,failX+8,base-24,"First zero",m?16:21,c.states.Unhealthy,700);
    const x0=m?pad:742, y0=m?295:70, width=W-pad-x0, height=m?187:263;
    box(g,x0,y0,width,height,{rx:4,fill:c.panel});
    write(g,x0+16,y0+32,rule.id,m?22:27,c.policy,700);
    write(g,x0+16,y0+63,"Enter after "+rule.enterAfter+" failed sample",m?18:20,c.states.Unhealthy,660);
    write(g,x0+16,y0+90,"Clear after "+rule.exitAfter+" good samples",m?18:20,c.states.Healthy,660);
    shape(g,`M${x0+16} ${y0+108}H${x0+width-16}`,c.line,1);
    write(g,x0+16,y0+135,"Measured "+failed.minute+":00 UTC",m?16:19,c.muted);
    write(g,x0+16,y0+160,"Received "+failed.received+" UTC",m?16:19,c.muted);
    if(!m) write(g,pad,H-15,"0 = unavailable     1 = available",18,c.muted);
    const failedAt=s.spokenAt("The chart shows",.28), receivedAt=s.spokenAt("Azure Monitor delivers",.47);
    return p=>{
      elapsed.setAttribute("stroke-dashoffset",1-stage(p,failedAt,receivedAt));
      ring.setAttribute("r",(m?12:14)+5*Math.sin(Math.PI*stage(p,failedAt,receivedAt)));
    };
  }

  function paths(s) {
    const {g,W,H,m,pad,header,write,shape,arrow,box,pulse,stage,colors:c}=s;
    header("Which paths carry the alarm?","Workload structure · selected Affects links");
    // These are edges in workloadStructure.final.snapshot.json. Direction
    // matches the graph, and the animated markers follow the query from web to PostgreSQL.
    const positions=m?{
      "apim-shop":[115,105],web:[353,105],api:[123,211],shopcache:[353,211],
      database:[123,320],"pg-shop":[123,426]
    }:{
      "apim-shop":[145,115],web:[145,264],api:[404,117],shopcache:[404,276],
      database:[671,117],"pg-shop":[956,117]
    };
    const states=ShopReplayCases.rollup.frames.find(r=>r.revision===16).states;
    const names={"apim-shop":"Gateway",web:"Web",api:"API",shopcache:"shopcache",database:"Database service","pg-shop":"pg-shop"};
    const w=m?164:193,h=m?58:73, routes=[
      ["database","pg-shop"],["api","database"],["apim-shop","api"],["web","api"],["web","shopcache"]
    ];
    const packets=routes.map(([a,b])=>{
      const [ax,ay]=positions[a],[bx,by]=positions[b],dx=bx-ax,dy=by-ay;
      const crop=Math.min(w/(2*Math.abs(dx)||1),h/(2*Math.abs(dy)||1));
      const start=[ax+dx*crop,ay+dy*crop],end=[bx-dx*crop,by-dy*crop];
      const color=b==="shopcache"?c.states.Healthy:c.states.Unhealthy;
      arrow(g,...start,...end,color,2.7);
      return {from:a,to:b,move:pulse(g,start,end,color)};
    });
    for(const [key,[x,y]] of Object.entries(positions)) {
      const color=c.states[states[key]];
      box(g,x-w/2,y-h/2,w,h,{rx:4,fill:c.panel,stroke:color,"stroke-width":2});
      write(g,x,y-2,names[key],key==="database"?(m?17:20):(m?19:23),c.ink,720,"middle");
      write(g,x,y+(m?19:24),symbols[states[key]]+"  "+states[key],m?16:19,color,650,"middle");
    }
    if(m) {
      write(g,245,315,"Each arrow points",17,c.muted);
      write(g,245,339,"to a dependency.",17,c.muted);
      write(g,245,384,"Up to 16 links.",17,c.ink,650);
      write(g,245,408,"Keep the worst",17,c.ink,650);
      write(g,245,431,"state reached.",17,c.ink,650);
    } else {
      write(g,661,232,"Affects",24,c.ink,730);
      write(g,661,263,"Follow these edges to find the contributors.",19,c.muted);
      write(g,661,291,"Keep the worst state within 16 hops.",19,c.muted);
    }
    shape(g,`M${pad} ${H-33}H${W-pad}`,c.line,1);
    write(g,pad,H-10,m?"Same revision  ·  cache stays healthy":"Revision 16  ·  These components are evaluated together. The cache stays healthy.",m?16:18,c.muted);
    const web=s.spokenAt("Web has a path",.19), cache=s.spokenAt("The cache has no such path",.35);
    const query=s.spokenAt("The health query follows",.65);
    const path=["web","api","database","pg-shop"];
    const route=path.slice(1).map((to,i)=>packets.find(e=>e.from===path[i] && e.to===to));
    const drawPath=(p,start,end)=>{
      const step=(end-start)/route.length;
      route.forEach((edge,i)=>edge.move(stage(p,start+i*step,start+(i+1)*step)));
    };
    return p=>{
      if(p<query)drawPath(p,web,cache);
      else drawPath(p,query,Math.min(1,query+.21));
    };
  }

  function recovery(s) {
    const {g,W,H,m,pad,header,write,shape,box,layer,stage,colors:c}=s;
    const samples=ShopReplayCases.rollup.readings.filter(r=>r.minute>="10:13" && r.minute<="10:16");
    const closed=ShopReplayCases.rollup.frames.find(r=>r.revision===18);
    const open=ShopReplayCases.rollup.frames.find(r=>r.revision===16);
    header("When does the alarm clear?","pg-shop · "+ShopReplayCases.rollup.rule.id);
    const tableX=m?103:184, right=W-pad, cell=(right-tableX)/samples.length;
    const start=m?113:96,step=m?64:52;
    const y=i=>start+step*i;
    let consecutive=0;
    const columns=samples.map((sample,i)=>{
      const x=tableX+i*cell;
      const col=layer();
      box(col,x+2,y(0)-25,cell-4,y(3)-y(0)+47,{fill:i===2?c.raised:c.panel,rx:4});
      write(col,x+cell/2,y(0),sample.minute,m?18:22,c.ink,720,"middle");
      write(col,x+cell/2,y(1),String(sample.value),m?24:31,sample.value===0?c.states.Unhealthy:c.states.Healthy,720,"middle");
      consecutive=sample.value===1?consecutive+1:0;
      const count=Math.min(consecutive,ShopReplayCases.rollup.rule.exitAfter);
      const state=i<2?open.states["pg-shop"]:closed.states["pg-shop"];
      write(col,x+cell/2,y(2),count+" / "+ShopReplayCases.rollup.rule.exitAfter,m?19:24,c.muted,680,"middle");
      write(col,x+cell/2,y(3),m?symbols[state]:symbols[state]+"  "+state,m?26:22,c.states[state],740,"middle");
      return col;
    });
    ["Sample","Reading","Good run","Alarm"].forEach((v,i)=>write(g,pad,y(i),v,m?17:20,c.muted,640));
    const cursor=shape(g,"",c.accent,4);
    const foot=m?369:302;
    write(g,pad,foot,"10:14   ·   One good minute",m?22:25,c.ink,700);
    write(g,pad,foot+(m?26:29),"Revision 17 holds the alarm open.",m?17:19,c.states.Unhealthy,630);
    const secondX=m?pad:W*.51,secondY=m?foot+71:foot;
    write(g,secondX,secondY,"10:15   ·   Two in a row",m?22:25,c.ink,700);
    write(g,secondX,secondY+(m?26:29),"Revision 18 clears the database and its callers.",m?17:19,c.states.Healthy,630);
    const first=s.spokenAt("At ten fourteen",.30), second=s.spokenAt("Ten fifteen makes two",.53);
    return p=>{
      const col=p<first?0:p<second?1:2, x=tableX+cell*col;
      cursor.setAttribute("d",`M${x+8} ${y(3)+24}h${cell-16}`);
      columns.forEach((node,i)=>node.setAttribute("opacity",i>col?.7:1));
    };
  }

  function observation(s, data) {
    const {g,W,H,m,pad,header,write,shape,arrow,pulse,block,box,stage,colors:c}=s;
    const flow=data.flow;
    header("One more destination for the API pod","Controlled Hubble replay · shop namespace");
    const lx=m?17:26,rx=m?303:501, y1=m?107:104,y2=m?224:264,
      w=m?157:185,h=m?56:68,edgeX=lx+w;
    block(g,lx,y1,w,h,"web-7d4-1","Web pod");
    block(g,lx,y2,w,h,flow.pod,"API pod");
    block(g,rx,y1,w,h,flow.address,"Redis address",c.identity);
    block(g,rx,y2,w,h,"10.2.0.6","PostgreSQL address",c.identity);
    for(const y of [y1,y2]) arrow(g,edgeX+2,y+h/2,rx-3,y+h/2,c.muted);
    const p1=[edgeX+2,y2+h*.42], p2=[rx-3,y1+h*.68];
    arrow(g,...p1,...p2,c.added,3);
    const move=pulse(g,p1,p2,c.added,7);
    write(g,(edgeX+rx)/2,m?202:214,"New flow",m?16:19,c.added,740,"middle");
    const ax=m?pad:742, ay=m?311:78, aw=W-ax-pad, ah=m?174:245;
    box(g,ax,ay,aw,ah,{rx:4,fill:c.panel,stroke:c.line,"stroke-width":1.4});
    const time=flow.time;
    write(g,ax+16,ay+31,"Hubble  ·  "+time+" UTC",m?20:23,c.ink,710);
    write(g,ax+16,ay+64,flow.verdict,m?18:21,c.ink,720);
    write(g,ax+16,ay+94,"Destination named by the flow",m?16:18,c.muted);
    write(g,ax+16,ay+119,flow.host,m?16:17,c.ink,620);
    write(g,ax+16,ay+(m?150:180),"Redis already exists in the graph.",m?16:18,c.muted);
    const call=s.spokenAt("Hubble recorded",.25), observed=s.spokenAt("The new line means",.53);
    return p=>move(stage(p,call,observed));
  }

  function resolve(s,data) {
    const {g,W,H,m,pad,header,write,shape,arrow,block,pulse,stage,colors:c}=s;
    const {flow,endpointSlice:slice,dependency}=data;
    header("How the flow becomes a dependency","Hubble + Kubernetes + Azure Resource Graph");
    // The endpoint slice identifies the caller. Two independent matches
    // support the same Redis target, one by address and one by DNS name.
    const left=m?pad:24, right=m?260:692, ww=m?205:401,
      top=m?82:72, gap=m?74:86, hh=m?53:66;
    const x1=left+ww/2,x2=right+ww/2;
    block(g,left,top,ww,hh,flow.pod,"Observed source pod",c.accent);
    block(g,right,top,ww,hh,flow.address,"Observed destination",c.identity);
    arrow(g,left+ww+2,top+hh/2,right-3,top+hh/2,c.added,2.3);
    const mid=top+gap, low=mid+gap;
    block(g,left,mid,ww,hh,slice.name,"EndpointSlice",c.accent);
    block(g,left,low,ww,hh,"API","Kubernetes service",c.accent);
    block(g,right,mid,ww,hh,"pe-shopcache","Private endpoint",c.identity);
    block(g,right,low,ww,hh,dependency.targetLabel,"Azure Redis resource",c.identity);
    const matches=[];
    for(const [x,color] of [[x1,c.accent],[x2,c.identity]]) {
      arrow(g,x,top+hh+1,x,mid-3,color);
      arrow(g,x,mid+hh+1,x,low-3,color);
      matches.push(pulse(g,[x,top+hh+1],[x,mid-3],color),pulse(g,[x,mid+hh+1],[x,low-3],color));
    }
    const packet=pulse(g,[left+ww+3,low+hh/2],[right-3,low+hh/2],c.added);
    arrow(g,left+ww+1,low+hh/2,right-3,low+hh/2,c.added,3);
    if(!m)write(g,(left+ww+right)/2,low+hh/2-12,dependency.type,21,c.added,700,"middle");
    const fy=m?306:319;
    shape(g,`M${pad} ${fy}H${W-pad}`,c.line,1);
    const name=write(g,pad,fy+26,flow.host,m?18:21,c.identity,680);
    const match=write(g,pad,fy+49,"DNS name on the flow and Redis resource",m?15.5:17,c.muted);
    if(m)write(g,pad,fy+83,"API  →  "+dependency.type+"  →  "+dependency.targetLabel,20,c.added,720);
    const warning=s.layer();
    const wx=m?pad:605, wy=m?fy+124:fy+26;
    write(warning,wx,wy,"With the new dependency",m?17:19,c.muted,650);
    const after=data.after;
    for(const [i,[key,label]] of [["shopcache","Redis"],["api","API"],["apim-shop","Gateway"]].entries()) {
      const state=after[key],x=wx+i*(m?147:163);
      write(warning,x,wy+25,label,m?16:17,c.ink,650);
      write(warning,x,wy+47,symbols[state]+" "+state,m?16:17,c.states[state],680);
    }
    const caller=s.spokenAt("Kubernetes ties",.18), endpoint=s.spokenAt("At the other end",.32);
    const dns=s.spokenAt("The DNS name",.49), derived=s.spokenAt("That gives",.61);
    const health=s.spokenAt("The health model can",.82);
    const stops=[caller,(caller+endpoint)/2,endpoint,(endpoint+dns)/2,dns];
    return p=>{
      matches.forEach((move,i)=>move(stage(p,stops[i],stops[i+1])));
      const seen=.72+.28*stage(p,dns,derived);
      name.setAttribute("opacity",seen);match.setAttribute("opacity",seen);
      packet(stage(p,derived,health));
      warning.setAttribute("opacity",p>=health?1:0);
    };
  }

  function patches(s,data) {
    const {g,W,H,m,pad,header,write,shape,box,layer,stage,colors:c}=s;
    const master=data.masterPatch, structure=data.structurePatch, health=data.healthPatch;
    const count=(patch,type)=>patch.edits.filter(edit=>edit.operation==="added" && edit.type===type).length;
    header("Three graphs, three revisions","Controlled replay · selected patch edits");
    const groups=[
      [master,"Master",c.master,[
        ["+ Sends","Pod → destination"],
        ["Existing endpoints","0 new entities"]
      ]],
      [structure,"Workload structure",c.accent,[
        ["+ DependsOn, TalksTo, Affects","API → shopcache"],
        ["+ "+count(structure,"DependencyEvidence")+" evidence records","+ "+count(structure,"EvidenceFor")+" EvidenceFor links"]
      ]],
      [health,"Workload health",c.policy,[
        ["+ DependsOn","API → shopcache"],
        ["Changed states","API and gateway"]
      ]]
    ];
    const band=m?77:77,gap=m?13:20,cardW=m?W-pad*2:(W-pad*2-gap*2)/3,cardH=m?124:260;
    const sheets=groups.map(([patch,name,color,changes],i)=>{
      const x=m?pad:pad+i*(cardW+gap),y=m?band+i*(cardH+gap):band;
      const sheet=layer();
      box(sheet,x,y,cardW,cardH,{rx:4,fill:c.panel,stroke:c.line,"stroke-width":1.5});
      write(sheet,x+13,y+(m?28:36),name,m?19:23,color,720);
      write(sheet,x+cardW-13,y+(m?28:36),patch.from+" → "+patch.to,m?16:19,color,680,"end");
      shape(sheet,`M${x+13} ${y+(m?40:55)}H${x+cardW-13}`,c.line,1);
      changes.forEach(([edit,detail],j)=>{
        if(m) {
          write(sheet,x+13,y+68+j*33,edit,16.5,edit.startsWith("+")?c.added:c.ink,650);
          write(sheet,x+cardW-13,y+68+j*33,detail,15.5,c.muted,570,"end");
        }else{
          write(sheet,x+13,y+96+j*80,edit,20,edit.startsWith("+")?c.added:c.ink,650);
          write(sheet,x+13,y+125+j*80,detail,18,c.muted);
        }
      });
      s.el(sheet,"title",{},patch.graph+" "+patch.from+" → "+patch.to+" · "+patch.edits.map(edit=>edit.operation+" "+edit.type+" "+edit.id).join(", "));
      const progress=shape(sheet,`M${x+5} ${y+cardH-3}H${x+cardW-5}`,color,3,{pathLength:1,"stroke-dasharray":"1 1"});
      return {sheet,progress};
    });
    const marks=[s.spokenAt("In the master graph",.35),s.spokenAt("In the structure graph",.55),s.spokenAt("In the health graph",.74)];
    return p=>sheets.forEach(({sheet,progress},i)=>{
      const value=stage(p,marks[i],marks[i]+.075);
      sheet.setAttribute("opacity",.72+.28*value);
      progress.setAttribute("stroke-dashoffset",1-value);
    });
  }

  function inspect(s,data) {
    const {g,W,H,m,pad,header,write,shape,box,layer,stage,colors:c}=s;
    const {before,after,proofs}=data;
    const cutoff=data.cutoff;
    header("Where did this dependency come from?","Same readings in both runs · "+cutoff+" UTC");
    const lx=m?pad:590;
    const colA=m?249:312,colB=m?400:471, colY=m?354:147, rowH=m?28:46;
    const table=s.layer();
    write(table,pad,colY,"Component",m?15:18,c.muted,650);
    write(table,colA,colY,"Original",m?16:18,c.muted,650,"middle");
    write(table,colB,colY,"With the flow",m?16:18,c.muted,650,"middle");
    for(const [i,[key,label]] of [["shopcache","Redis"],["api","API"],["apim-shop","Gateway"]].entries()) {
      const y=colY+(i+1)*rowH;
      write(table,pad,y,label,m?17:22,c.ink,680);
      for(const [x,state] of [[colA,before[key]],[colB,after[key]]])
        write(table,x,y,symbols[state]+"  "+state,m?16:19,c.states[state],670,"middle");
    }
    write(table,pad,colY+rowH*4+4,"Same Redis serverLoad readings in both runs",m?16:20,c.states.Degraded,650);
    // Follow the recorded workload edge to its source in workloadStructure.
    const lineageX=pad, lineY=m?85:89;
    write(g,lineageX,lineY,"Workload → Workload structure → Master",m?17:22,c.accent,680);
    write(g,m?pad:lx,m?107:74,"Provenance",m?15:18,c.muted,680);
    if(!m)shape(g,`M555 68V${H-25}`,c.line,1);
    const at=m?117:93;
    const proofGroups=proofs.map(proof=>{
      const address=proof.how==="privateEndpoint",x=m?pad:lx, y=at+(address?0:(m?86:113));
      const row=layer(), width=W-x-pad, height=m?75:91;
      box(row,x,y,width,height,{rx:4,fill:c.panel,stroke:c.line});
      write(row,x+13,y+(m?22:27),address?"Private endpoint match":"DNS name match",m?17:21,c.identity,680);
      if(address) {
        const label=role=>proof.sources.find(source=>source.role===role).label;
        write(row,x+13,y+(m?44:52),label("pod")+"  →  "+label("destination"),m?16:20,c.ink,650);
        write(row,x+13,y+(m?64:77),"Private endpoint   "+label("endpoint"),m?15:17,c.muted);
      } else {
        write(row,x+13,y+(m?44:52),data.flow.host,m?16:19,c.ink,650);
        write(row,x+13,y+(m?64:77),"Azure Redis   "+data.dependency.targetLabel,m?15:17,c.muted);
      }
      s.el(row,"title",{},proof.detail);
      return row;
    });
    write(g,m?pad:lx,m?304:340,"Hubble flow   "+data.flow.record,m?15:18,c.master,600);
    if(m)shape(g,`M${pad} 326H${W-pad}`,c.line,1);
    const marks=[s.spokenAt("One links",.30),s.spokenAt("The other identifies",.45)];
    const compare=s.spokenAt("Now compare",.71);
    return p=>{
      proofGroups.forEach((row,i)=>row.setAttribute("opacity",.73+.27*stage(p,marks[i],marks[i]+.06)));
      table.setAttribute("opacity",.73+.27*stage(p,compare,compare+.06));
    };
  }

  return {
    rollup(ctx) {
      const draw=[introduction,signal,paths,recovery][ctx.beat];
      return draw(studio(ctx));
    },
    discovery(ctx) {
      const draw=[observation,resolve,patches,inspect][ctx.beat];
      return draw(studio(ctx),ShopReplayCases.discovery);
    }
  };
})();
