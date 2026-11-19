// Figures use the same seek position in the film and the interactive guide.
// Arrows on dependency graphs point from a consumer to the component it needs.
const ShopReplayScenes = (() => {
  const colors = {
    bg:"var(--page)", ink:"var(--ink)", muted:"var(--muted)",
    line:"var(--line)", accent:"var(--blue)", master:"var(--master)", identity:"var(--identity)",
    added:"var(--added)", policy:"var(--rule)",
    states:{Healthy:"var(--healthy)",Unhealthy:"var(--unhealthy)",Degraded:"var(--degraded)",Unknown:"var(--unknown)"}
  };
  const symbols = {Healthy:"✓", Unhealthy:"×", Degraded:"▲", Unknown:"?"};
  const wash=(color,percent)=>`color-mix(in srgb, ${color} ${percent}%, var(--page))`;
  function studio(ctx) {
    const {el,txt,g,box,W,H,mobile:m,stage}=ctx, pad=m?13:25;
    const layer=(parent=g,attrs={})=>el(parent,"g",attrs);
    const write=(parent,x,y,s,size,color=colors.ink,weight=570,anchor="start")=>
      txt(parent,x,y,s,{"font-size":size,fill:color,"font-weight":weight,"text-anchor":anchor});
    const shape=(parent,d,color=colors.line,width=2,extra={})=>
      el(parent,"path",{d,fill:"none",stroke:color,"stroke-width":width,"stroke-linejoin":"round","stroke-linecap":"round",...extra});
    function header(title,source,color) {
      box(g,0,0,W,H,{fill:colors.bg});
      box(g,0,0,W,m?63:48,{fill:wash(color,13)});
      box(g,0,0,5,m?63:48,{fill:color});
      write(g,pad,m?27:32,title,m?23:29,colors.ink,650);
      write(g,m?pad:W-pad,m?51:31,source,m?15.5:17,color,550,m?"start":"end");
      shape(g,`M0 ${m?63:48}H${W}`,color,1,{"stroke-opacity":.52});
    }
    function arrow(parent,x1,y1,x2,y2,color=colors.line,width=2.7) {
      const dx=x2-x1,dy=y2-y1,len=Math.hypot(dx,dy),ux=dx/len,uy=dy/len;
      shape(parent,`M${x1} ${y1}L${x2} ${y2}m${-ux*9-uy*4.5} ${-uy*9+ux*4.5}L${x2} ${y2}l${-ux*9+uy*4.5} ${-uy*9-ux*4.5}`,color,width);
    }
    function block(parent,x,y,w,h,title,detail,color=colors.accent) {
      const row=layer(parent);
      box(row,x,y,w,h,{rx:5,fill:wash(color,24),stroke:color,"stroke-width":2});
      box(row,x+2,y+3,5,h-6,{rx:2,fill:color});
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

  const rollup = ShopReplayCases.rollup;
  const atMinute = minute => rollup.frames.find(frame => frame.minute === minute);
  function treeRoute(s,parent,points,color,baseline=true) {
    const [first,...rest]=points;
    const d="M"+first.join(" ")+rest.map(p=>"L"+p.join(" ")).join("");
    if(baseline)s.shape(parent,d,s.colors.line,2.5);
    const trace=s.shape(parent,d,color,3.5,{pathLength:1,"stroke-dasharray":"1 1"});
    const reduced=s.inGuide && matchMedia("(prefers-reduced-motion:reduce)").matches;
    return p=>trace.setAttribute("stroke-dashoffset",1-(reduced ? Number(p>0) : p));
  }

  function introduction(s) {
    const {g,W,H,m,header,write,box,colors:c,stage}=s;
    const {root,nodes}=rollup.tree, frame=atMinute("10:11");
    header("Where the shop’s problem starts","Webshop · database availability",c.policy);
    const positions=m?{
      "service:web":[241,100],"service:api":[67,199],"deployment:web":[182,199],
      "resource:shopcache":[297,199],"resource:shopassets":[412,199],
      "service:database":[67,306],"deployment:api":[182,306],"resource:pg-shop":[67,418]
    }:{
      "service:web":[553,85],"service:api":[230,170],"deployment:web":[503,170],
      "resource:shopcache":[744,170],"resource:shopassets":[974,170],
      "service:database":[130,258],"deployment:api":[341,258],"resource:pg-shop":[130,349]
    };
    const w=m?106:179,h=m?62:64;
    const down=new Map(),up=new Map();
    for(const node of nodes){
      for(const child of node.children){
        const [x1,y1]=positions[node.key], [x2,y2]=positions[child], join=(y1+y2)/2;
        const path=[[x1,y1+h/2],[x1,join],[x2,join],[x2,y2-h/2]];
        down.set(child,treeRoute(s,g,path,c.accent));
        up.set(child,treeRoute(s,g,[...path].reverse(),c.states.Unhealthy,false));
      }
    }
    const borders=new Map();
    for(const {key,name,kind} of nodes){
      const [x,y]=positions[key], state=frame.treeStates[key], color=c.states[state];
      const a=x-w/2,b=y-h/2;
      const border=box(g,a,b,w,h,{rx:5,fill:wash(color,21),stroke:color,"stroke-width":1.6});
      borders.set(key,border);
      write(g,x,b+(m?17:19),name,m?17:20,c.ink,720,"middle");
      const typeLabel={"resource:pg-shop":"PostgreSQL","resource:shopcache":"Redis cache","resource:shopassets":"Storage"}[key] || kind;
      write(g,x,b+(m?34:38),typeLabel,m?14.5:16,c.muted,600,"middle");
      box(g,a+1,b+h-21,w-2,20,{fill:color,rx:3});
      write(g,x,b+h-5,symbols[state]+" "+state,m?14.5:16,c.bg,700,"middle");
    }
    const nx=m?246:570,ny=m?274:247,nw=W-nx-(m?14:25);
    box(g,nx,ny,nw,m?150:111,{rx:4,fill:wash(c.policy,13),stroke:c.policy});
    write(g,nx+11,ny+26,"Database checks (UTC)",m?18:23,c.ink,700);
    const failed=rollup.readings.findIndex(sample=>sample.value===0);
    const good=rollup.readings[failed-1], bad=rollup.readings[failed];
    write(g,nx+11,ny+(m?60:59),good.minute+"   ✓  Answered",m?16:20,c.states.Healthy,630);
    const firstFailure=write(g,nx+11,ny+(m?91:88),bad.minute+"   ×  First failure",m?16:20,c.states.Unhealthy,720);
    if(m)write(g,nx+11,ny+125,"One check per minute",15,c.muted);
    write(g,m?14:570,H-(m?15:16),"pg-shop is the database behind the shop’s API.",m?16:18,c.muted);
    const fail=s.spokenAt("The first failed database check",.16), follow=s.spokenAt("Follow the branch",.29),
      impact=s.spokenAt("That’s how a database problem",.56), cache=s.spokenAt("Redis and storage",.70),
      principle=s.spokenAt("A Health Tree lets you",.84);
    const branch=["service:api","service:database","resource:pg-shop"];
    return p=>{
      for(const draw of down.values())draw(0);
      for(const draw of up.values())draw(0);
      branch.forEach((key,i)=>{
        const step=(impact-follow)/3;
        down.get(key)(p<impact?stage(p,follow+i*step,follow+(i+1)*step):0);
        const back=(cache-impact)/3, n=2-i;
        up.get(key)(stage(p,impact+n*back,impact+(n+1)*back));
      });
      for(const key of ["resource:shopcache","resource:shopassets"])
        down.get(key)(stage(p,cache,principle));
      firstFailure.setAttribute("opacity",.70+.30*stage(p,fail,follow));
      borders.get("resource:pg-shop").setAttribute("stroke-width",1.6+2.5*stage(p,fail,follow));
      borders.get(root).setAttribute("stroke-width",1.6+2.5*stage(p,principle,principle+.05));
    };
  }

  function derive(s) {
    const {g,W,m,header,write,box,stage,colors:c}=s, src=rollup.tree.source;
    header("How the tree gets a branch","pg-shop · live projections",c.master);
    const mx=m?13:20,mw=m?454:455,my=m?75:71, mh=m?74:106, gap=m?8:23;
    const cards=[
      ["Kubernetes · Service","database  ·  externalName",src.externalName],
      ["Azure · PostgreSQL Flexible Server",src.resourceName+"  ·  host built from name and type",src.externalName]
    ].map(([title,label,value],i)=>{
      const y=my+i*(mh+gap);
      const tile=box(g,mx,y,mw,mh,{rx:5,fill:wash(c.master,16),stroke:c.master});
      write(g,mx+12,y+(m?20:27),title,m?16:21,c.master,690);
      write(g,mx+12,y+(m?42:61),label,m?16:19,c.ink,580);
      const host=write(g,mx+12,y+(m?63:88),value,m?16:19,c.identity,670);
      return {tile,host};
    });
    const tx=m?13:625,tw=W-tx-(m?13:20),sy=m?266:71,sw=m?95:127,hy=m?385:238,hh=m?102:136;
    // Both source descriptions meet at the same name. Its spelling alone is
    // insufficient: the structure query constrains which Azure resource types can back the service.
    const matches=[];
    if(m) {
      matches.push(treeRoute(s,g,[[mx+mw/2,my+2*mh+gap],[mx+mw/2,sy]],c.identity));
      write(g,mx+mw/2+12,sy-11,"= same host",15,c.identity,670);
    } else {
      const jx=529,jy=sy+sw*.52;
      cards.forEach((_,i)=>{
        const y=my+i*(mh+gap)+mh/2;
        matches.push(treeRoute(s,g,[[mx+mw,y],[jx,y],[jx,jy],[tx,jy]],c.identity));
      });
      write(g,548,jy-12,"= host",18,c.identity,670);
    }
    const structure=box(g,tx,sy,tw,sw,{rx:5,fill:wash(c.accent,17),stroke:c.accent});
    write(g,tx+13,sy+(m?24:32),"Workload structure",m?20:25,c.accent,720);
    const xLeft=tx+16,xRight=tx+tw*.64;
    write(g,xLeft,sy+(m?55:77),"database",m?20:24,c.ink,700);
    write(g,xRight,sy+(m?55:77),src.resourceName,m?20:24,c.ink,700);
    s.arrow(g,tx+tw*.31,sy+(m?49:70),xRight-10,sy+(m?49:70),c.accent,2.5);
    write(g,xLeft,sy+(m?82:109),"Service",m?15:18,c.muted);
    write(g,tx+tw*.45,sy+(m?82:109),"BackedBy · Affects",m?14:16,c.accent,650,"middle");
    write(g,xRight,sy+(m?82:109),"Azure resource",m?15:18,c.muted);
    const center=tx+tw*.5;
    const second=treeRoute(s,g,[[center,sy+sw],[center,hy]],c.policy);
    const health=box(g,tx,hy,tw,hh,{rx:5,fill:wash(c.policy,18),stroke:c.policy});
    write(g,tx+13,hy+(m?25:31),"Workload health",m?20:25,c.policy,720);
    write(g,tx+13,hy+(m?52:64),"Structure  +  measurements  +  signal rules",m?17:20,c.ink,620);
    const bar=box(g,tx+13,hy+(m?66:84),tw-26,m?26:37,{rx:3,fill:c.states.Unhealthy});
    const label=write(g,center,hy+(m?85:110),"×  pg-shop and database service · Unhealthy",m?16:19,c.bg,700,"middle");
    const kube=s.spokenAt("Look at the name",.25),
      azure=s.spokenAt("The structure query builds",.38), same=s.spokenAt("The names match",.53),
      rule=s.spokenAt("The health query combines",.70), update=s.spokenAt("As the source records",.85);
    return p=>{
      cards.forEach(({tile,host},i)=>{
        const t=stage(p,i?azure:kube,i?same:azure);
        tile.setAttribute("stroke-width",1+2*t);
        host.setAttribute("opacity",.65+.35*t);
      });
      for(const trace of matches)trace(stage(p,same,rule));
      structure.setAttribute("stroke-width",1+2*stage(p,same,rule));
      second(stage(p,rule,update));
      health.setAttribute("stroke-width",1+2*stage(p,rule,update));
      for(const mark of [bar,label])mark.setAttribute("opacity",stage(p,rule,update));
    };
  }

  function signal(s) {
    const {g,W,H,m,header,write,box,shape,dot,stage,colors:c}=s, rule=rollup.rule;
    header("When did the alarm open and clear?","pg-shop · database checks in UTC",c.policy);
    const x0=m?13:20,y0=m?75:71,rw=m?454:315,rh=m?104:267;
    box(g,x0,y0,rw,rh,{rx:5,fill:wash(c.policy,16),stroke:c.policy});
    write(g,x0+12,y0+(m?24:35),rule.id,m?20:25,c.policy,720);
    write(g,x0+12,y0+(m?48:79),rule.meter,m?17:22,c.ink,650);
    const enter=write(g,x0+12,y0+(m?85:138),"Open · "+rule.enterAfter+" failed minute",m?16:21,c.states.Unhealthy,680);
    const exit=write(g,x0+(m?218:12),y0+(m?85:188),"Clear · "+rule.exitAfter+" good minutes",m?16:21,c.states.Healthy,680);
    const pts=rollup.readings.filter(v=>v.minute>="10:10" && v.minute<="10:15");
    const left=m?33:385,right=m?447:1074, high=m?243:153,low=m?306:225;
    const xs=pts.map((_,i)=>left+i*(right-left)/(pts.length-1));
    const y=v=>v===1?high:low;
    write(g,left,m?212:99,"Availability reading",m?19:24,c.ink,700);
    [0,1].forEach(v=>{
      shape(g,`M${left} ${y(v)}H${right}`,c.line,1.4,{"stroke-dasharray":"3 5"});
      write(g,left-11,y(v)+6,String(v),m?15:18,c.muted,600,"end");
    });
    const line=pts.map((v,i)=>i?`H${xs[i]}V${y(v.value)}`:`M${xs[i]} ${y(v.value)}`).join("");
    shape(g,line,c.line,3);
    const trail=shape(g,line,c.accent,3,{pathLength:1,"stroke-dasharray":"1 1"});
    pts.forEach((v,i)=>{
      dot(g,xs[i],y(v.value),m?5:7,{fill:v.value?c.states.Healthy:c.states.Unhealthy,stroke:c.bg,"stroke-width":1.6});
      write(g,xs[i],low+(m?27:34),v.minute,m?15:18,c.muted,610,"middle");
    });
    const t0=m?14:357, width=m?452:747, y0b=m?356:287, spacing=width/3;
    const events=[
      [rollup.alarmTimes.opened.slice(0,5),"Alarm opens",atMinute("10:11")],
      ["10:14","First good check",atMinute("10:14")],
      [rollup.alarmTimes.cleared.slice(0,5),"Alarm clears",atMinute("10:15")]
    ];
    const bars=events.map(([minute,label,frame],i)=>{
      const left=t0+i*spacing, state=frame.states["pg-shop"], color=c.states[state], group=s.layer();
      box(group,left+2,y0b,spacing-5,m?103:95,{rx:4,fill:wash(color,20),stroke:color});
      write(group,left+10,y0b+(m?25:27),minute+" UTC",m?16:19,c.ink,650);
      write(group,left+10,y0b+(m?52:54),label,m?16:19,c.muted,590);
      box(group,left+3,y0b+(m?67:62),spacing-7,m?34:31,{rx:2,fill:color});
      write(group,left+spacing/2,y0b+(m?90:84),symbols[state]+" "+state,m?16:18,c.bg,720,"middle");
      return group;
    });
    const clock=write(g,m?14:21,H-(m?15:19),"",m?16:17,c.muted,600);
    const cursor=dot(g,0,0,m?11:13,{fill:"none",stroke:c.ink,"stroke-width":2.5});
    const open=s.spokenAt("It failed three checks",.12), fail=s.spokenAt("At 10:11",.24),
      own=s.spokenAt("Sattle raised the alarm",.35), first=s.spokenAt("At 10:14",.50),
      clear=s.spokenAt("The rule waits",.62), second=s.spokenAt("At 10:15",.73),
      recovery=s.spokenAt("The tree showed the recovery",.88);
    return p=>{
      const n=p<fail?0:p<first?1:p<second?4:5;
      cursor.setAttribute("cx",xs[n]);cursor.setAttribute("cy",y(pts[n].value));
      trail.setAttribute("stroke-dashoffset",1-n/(pts.length-1));
      enter.setAttribute("opacity",.76+.24*stage(p,open,fail));
      exit.setAttribute("opacity",.76+.24*stage(p,clear,second));
      bars.forEach((b,i)=>b.setAttribute("opacity",p>=[own,first,recovery][i]?1:.50));
      clock.textContent="Sample "+pts[n].minute+" UTC  ·  received "+pts[n].received+" UTC";
    };
  }

  function paths(s) {
    const {g,W,H,m,header,write,box,stage,colors:c}=s;
    const {root,nodes}=rollup.tree, byKey=new Map(nodes.map(node=>[node.key,node]));
    const frame=atMinute("10:11"), depths=[[root]];
    while(depths.at(-1).some(key=>byKey.get(key).children.length)){
      depths.push(depths.at(-1).flatMap(key=>byKey.get(key).children));
    }
    header("Why the problem reaches web","pg-shop · first failed check at 10:11 UTC",c.policy);
    const x0=m?13:20, gap=m?7:13, pw=m?454:(W-2*x0-3*gap)/4, ph=m?77:224;
    const marks=depths.map((keys,i)=>{
      const x=m?x0:x0+i*(pw+gap), y=m?75+i*(ph+gap):80;
      const panel=box(g,x,y,pw,ph,{rx:5,fill:wash(c.accent,11),stroke:c.line,"stroke-width":1.5});
      write(g,x+10,y+(m?23:31),i+" "+(i===1?"hop":"hops"),m?18:22,c.accent,710);
      keys.forEach((key,j)=>{
        const {name,kind}=byKey.get(key), own=frame.treeOwn[key];
        const color=own===undefined?c.muted:own===0?c.states.Healthy:c.states.Unhealthy;
        const col=m?j%2:0, row=m?Math.floor(j/2):j;
        const left=m?x+93+col*180:x+12,top=m?y+8+row*34:y+49+row*40;
        box(g,left,top,m?29:34,m?25:30,{rx:3,fill:color});
        write(g,left+(m?14.5:17),top+(m?18:22),own===undefined?"–":String(own),m?16:18,c.bg,720,"middle");
        write(g,left+(m?35:43),top+(m?18:22),name+" "+(kind==="resource"?"":kind),m?15:18,c.ink,600);
      });
      return panel;
    });
    const color=c.states[frame.treeStates[root]], dy=m?423:328;
    const result=box(g,x0,dy,W-x0*2,m?66:61,{rx:5,fill:wash(color,18),stroke:color});
    const score=write(g,x0+13,dy+(m?25:37),"",m?19:23,color,720);
    const help=write(g,m?x0+13:x0+425,dy+(m?50:37),"–  No own severity",m?16:19,c.muted);
    const start=s.spokenAt("Start at web itself",.23), walk=s.spokenAt("Then follow the links",.40),
      deep=s.spokenAt("Three links down",.56), worst=s.spokenAt("That outranks",.67),
      raised=s.spokenAt("The query keeps",.88);
    const steps=[start,walk,(walk+deep)/2,deep];
    return p=>{
      marks.forEach((panel,i)=>{
        const active=stage(p,steps[i],steps[i]+.04), shade=i===3?color:c.accent;
        panel.setAttribute("stroke",active?shade:c.line);
        panel.setAttribute("fill",wash(shade,10+11*active));
      });
      const current=p>=deep?color:p>=walk?c.states.Healthy:c.accent;
      result.setAttribute("fill",wash(current,18));
      result.setAttribute("stroke",current);
      score.setAttribute("fill",current);
      score.textContent=p>=worst?"Maximum own severity = 3":p>=deep?"Found pg-shop · own severity 3":p>=walk?"Highest seen so far = 0":"Follow Affects · 0 to 16 hops";
      help.textContent=p>=raised?"×  Web is Unhealthy · inherited from pg-shop":"–  No own severity";
      help.setAttribute("fill",p>=raised?color:c.muted);
    };
  }

  function observation(s,data) {
    const {g,W,m,pad,header,write,arrow,block,box,stage,colors:c}=s;
    const flow=data.flow;
    header("The API pod calls a new address","pg-shop · network traffic",c.master);
    const lx=m?17:26,rx=m?303:505,y1=m?102:99,y2=m?228:254,w=m?157:192,h=m?56:68;
    const edgeX=lx+w;
    for(const y of [y1,y2])arrow(g,edgeX+1,y+h/2,rx-3,y+h/2,c.accent,2.5);
    const a=[edgeX+1,y2+h*.37],b=[rx-3,y1+h*.72];
    const newCall=treeRoute(s,g,[a,b],c.added,false);
    const label=s.layer();
    const head=s.layer();
    arrow(head,b[0]+(a[0]-b[0])*.035,b[1]+(a[1]-b[1])*.035,...b,c.added,3);
    block(g,lx,y1,w,h,"web-7d4-1","Web pod",c.accent);
    block(g,lx,y2,w,h,flow.pod,"API pod",c.accent);
    block(g,rx,y1,w,h,flow.address,"Redis address",c.identity);
    block(g,rx,y2,w,h,"10.2.0.6","PostgreSQL address",c.identity);
    write(label,(a[0]+b[0])/2,m?152:167,"+ New call",m?17:20,c.added,730,"middle");
    const ax=m?pad:752,ay=m?314:80,aw=W-ax-pad,ah=m?170:240;
    const record=s.layer();
    box(record,ax,ay,aw,ah,{rx:5,fill:wash(c.master,18),stroke:c.master});
    box(record,ax,ay,aw,39,{rx:4,fill:c.master});
    write(record,ax+13,ay+27,"Hubble · "+flow.time+" UTC",m?20:22,c.bg,710);
    write(record,ax+13,ay+(m?64:78),flow.pod+" → "+flow.address,m?18:22,c.ink,690);
    write(record,ax+13,ay+(m?91:120),"Destination name in the flow",m?16:17,c.muted,560);
    write(record,ax+13,ay+(m?114:148),flow.host,m?18:19,c.identity,650);
    write(record,ax+13,ay+(m?149:199),flow.verdict,m?16:18,c.master,650);
    const call=s.spokenAt("At fifteen seconds past",.27), recordAt=s.spokenAt("That one call",.48);
    return p=>{
      const shown=stage(p,call,call+.12);
      newCall(shown);
      head.setAttribute("opacity",stage(shown,.86,1));
      label.setAttribute("opacity",shown);
      record.setAttribute("opacity",.8+.2*stage(p,recordAt,recordAt+.10));
    };
  }

  function resolve(s,data) {
    const {g,W,m,pad,header,write,block,box,stage,colors:c}=s;
    const {flow,endpointSlice:slice,dependency,proofs}=data;
    const endpoint=proofs.find(proof=>proof.how==="privateEndpoint").sources.find(item=>item.role==="endpoint").label;
    header("Which services are at the two ends?","pg-shop · workload structure",c.identity);
    const left=m?pad:24,right=m?262:692,w=m?205:404,top=m?79:67,gap=m?77:84,h=m?51:62;
    const rows=[
      [[flow.pod,"Observed source pod",c.accent],[flow.address,"Observed destination",c.identity]],
      [[slice.name,"Kubernetes EndpointSlice",c.accent],[endpoint,"Azure private endpoint",c.identity]],
      [["API","Kubernetes service",c.accent],[dependency.targetLabel,"Redis resource",c.identity]]
    ];
    const x=[left,right];
    const guides=[[],[]],links=[[],[]];
    for(const col of [0,1]){
      const center=x[col]+w/2;
      for(const i of [0,1]){
        const y=top+i*gap+h;
        links[col].push(treeRoute(s,g,[[center,y],[center,y+gap-h]],col===0?c.accent:c.identity));
      }
    }
    rows.forEach((r,i)=>r.forEach(([name,description,color],col)=>{
      guides[col].push(block(g,x[col],top+i*gap,w,h,name,description,color));
    }));
    const bridgeY=top+2*gap+h/2, a=left+w+3, b=right-3;
    const bridge=treeRoute(s,g,[[a,bridgeY],[b,bridgeY]],c.added);
    if(!m)write(g,(a+b)/2,bridgeY-12,dependency.type,21,c.added,690,"middle");
    const fy=m?327:320,footer=s.layer(),fw=W-pad*2;
    box(footer,pad,fy,fw,m?149:66,{rx:4,fill:wash(c.identity,13),stroke:c.identity});
    write(footer,pad+12,fy+24,"DNS name on the flow",m?17:18,c.identity,660);
    write(footer,pad+12,fy+(m?50:52),flow.host,m?18:20,c.ink,640);
    if(m){
      write(footer,pad+12,fy+87,"Matched resource",17,c.identity,650);
      write(footer,pad+12,fy+115,"Redis · "+dependency.targetLabel,20,c.ink,660);
    }else{
      write(footer,600,fy+24,"Matched resource",18,c.identity,650);
      write(footer,600,fy+52,"Redis · "+dependency.targetLabel,20,c.ink,660);
    }
    const caller=s.spokenAt("Kubernetes lists",.19),target=s.spokenAt("The destination address matches",.39);
    const dns=s.spokenAt("The host name",.57),both=s.spokenAt("Put those matches together",.77);
    return p=>{
      [caller,target].forEach((at,col)=>{
        links[col].forEach((paint,i)=>paint(stage(p,at+i*.045,at+.08+i*.045)));
        guides[col].forEach((tile,i)=>tile.setAttribute("opacity",i===0?1:.80+.20*stage(p,at+(i-1)*.04,at+.10+(i-1)*.04)));
      });
      footer.setAttribute("opacity",.80+.20*stage(p,dns,dns+.09));
      bridge(stage(p,both,both+.11));
    };
  }

  function newBranch(s,data) {
    const {g,W,m,pad,header,write,box,block,stage,colors:c}=s;
    const {before,after,nodes,type}=data.topology;
    const apiKey="service:api",redisKey="resource:shopcache";
    const existing=new Set(before.filter(link=>link.from===apiKey).map(link=>link.to));
    const names=new Map(nodes.map(node=>[node.key,node.name]));
    header("Redis appears under the API","Health model · compared through "+data.cutoff+" UTC",c.policy);
    const gap=m?12:26, pw=m?W-pad*2:(W-pad*2-gap)/2, ph=m?160:230;
    const top=m?77:75;
    let revealLink,revealCard;
    [before,after].forEach((edges,i)=>{
      const x=m?pad:pad+i*(pw+gap),y=m?top+i*(ph+gap):top;
      const shade=i===0?c.accent:c.added;
      box(g,x,y,pw,ph,{rx:5,fill:wash(shade,10),stroke:shade});
      box(g,x,y,pw,m?31:39,{rx:4,fill:wash(shade,30)});
      write(g,x+12,y+(m?22:27),i===0?"Original API branch":"With the new call",m?18:22,shade,710);
      const serviceX=x+(m?12:17),childX=x+pw*(m?.515:.52),startY=y+(m?49:65);
      const parentW=m?128:150,childW=x+pw-childX-13,nh=m?44:57,dy=m?54:73;
      const parentY=startY+dy*.5;
      block(g,serviceX,parentY,parentW,nh,"API","Service",c.accent);
      const children=edges.filter(link=>link.from===apiKey)
        .sort((a,b)=>Number(a.to===redisKey)-Number(b.to===redisKey));
      children.forEach(({to},j)=>{
        const newEdge=!existing.has(to), nodeColor=newEdge?c.added:c.accent;
        const cy=startY+dy*j, sourceX=serviceX+parentW,midX=(sourceX+childX)/2;
        const route=treeRoute(s,g,[[sourceX,parentY+nh/2],[midX,parentY+nh/2],[midX,cy+nh/2],[childX,cy+nh/2]],nodeColor,false);
        const card=block(g,childX,cy,childW,nh,names.get(to),to===redisKey?"Redis cache":"Database service",nodeColor);
        if(newEdge){
          card.setAttribute("data-added-dependency",to);
          revealLink=route;revealCard=card;
        }
        else route(1);
      });
    });
    const footerY=m?438:341;
    write(g,pad,footerY,m?"Web → Redis in both runs":"Web already depends on Redis in both runs.",m?17:21,c.ink,660);
    write(g,pad,footerY+(m?29:34),m?"Same rules · "+type:"Same model rules   ·   "+type+" links",m?16:18,c.muted,580);
    const aside=write(g,W-pad,footerY+(m?29:34),"+ API → Redis",m?17:20,c.added,710,"end");
    const redis=s.spokenAt("Redis appears alongside",.50), same=s.spokenAt("The existing rules picked up",.77);
    return p=>{
      const shown=stage(p,redis,redis+.10);
      revealLink(shown);
      revealCard.setAttribute("opacity",shown);
      aside.setAttribute("opacity",stage(p,same,same+.07));
    };
  }

  function inspect(s,data) {
    const {g,W,m,pad,header,write,shape,box,layer,stage,colors:c}=s;
    const {proofs,flow,dependency}=data;
    header("What put Redis on this branch?","pg-shop · evidence for the API link",c.identity);
    const bw=m?W-2*pad:395,bx=m?pad:23,by=m?76:90,bh=m?70:165;
    box(g,bx,by,bw,bh,{rx:5,fill:wash(c.policy,18),stroke:c.policy});
    write(g,bx+13,by+(m?25:37),"Health model · "+dependency.type,m?17:21,c.policy,690);
    write(g,bx+13,by+(m?55:84),"API → Redis",m?24:31,c.ink,710);
    if(!m)write(g,bx+13,by+125,"Source · workload structure",18,c.muted);
    else write(g,pad,by+bh+23,"Source · workload structure",16,c.muted,610);
    const ex=m?pad:514,ey=m?179:74,ew=W-ex-pad,eh=m?85:102,step=m?97:120;
    const proofGroups=proofs.map(proof=>{
      const addr=proof.how==="privateEndpoint",y=ey+(addr?0:step),row=layer();
      const find=role=>proof.sources.find(source=>source.role===role).label;
      box(row,ex,y,ew,eh,{rx:5,fill:wash(c.identity,17),stroke:c.identity});
      box(row,ex+2,y+3,5,eh-6,{rx:2,fill:c.identity});
      write(row,ex+15,y+(m?23:27),addr?"Private endpoint match":"DNS name match",m?18:21,c.identity,710);
      write(row,ex+15,y+(m?48:58),addr?find("destination")+" → "+find("endpoint"):flow.host,m?18:21,c.ink,670);
      write(row,ex+15,y+(m?72:85),addr?"Resource reached · "+dependency.targetLabel:"Resource named · "+find("azureResource"),m?16:18,c.muted,600);
      s.el(row,"title",{},proof.detail);
      return {row,addr,y};
    });
    const origin=layer(),oy=m?393:321,ow=W-pad*2;
    box(origin,pad,oy,ow,m?82:62,{rx:4,fill:wash(c.master,15),stroke:c.master});
    write(origin,pad+13,oy+24,"Hubble · "+flow.time+" UTC",m?17:19,c.master,680);
    write(origin,pad+13,oy+(m?53:49),flow.pod+" → "+flow.address,m?20:21,c.ink,670);
    if(!m)write(origin,W-pad-12,oy+38,flow.record,18,c.master,590,"end");
    const trails=proofGroups.map(({y})=>m?
      treeRoute(s,g,[[bx+bw-13,by+bh],[bx+bw-5,by+bh],[bx+bw-5,y-5],[bx+bw-13,y]],c.identity,false):
      treeRoute(s,g,[[bx+bw,by+bh/2],[ex-52,by+bh/2],[ex-52,y+eh/2],[ex,y+eh/2]],c.identity,false));
    const endpoint=s.spokenAt("One record matches",.31),dns=s.spokenAt("Another matches",.51),seen=s.spokenAt("Both lead back",.72);
    return p=>{
      proofGroups.forEach(({row,addr},i)=>{
        const at=addr?endpoint:dns,phase=stage(p,at,at+.10);
        row.setAttribute("opacity",.78+.22*phase);
        trails[i](phase);
      });
      origin.setAttribute("opacity",.80+.20*stage(p,seen,seen+.09));
    };
  }

  return {
    rollup(ctx) {
      const draw=[introduction,derive,signal,paths][ctx.beat];
      return draw(studio(ctx));
    },
    discovery(ctx) {
      const draw=[observation,resolve,newBranch,inspect][ctx.beat];
      return draw(studio(ctx),ShopReplayCases.discovery);
    }
  };
})();
