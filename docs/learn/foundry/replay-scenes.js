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
  const atRevision = n => rollup.frames.find(frame => frame.revision === n);
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
    const {root,nodes}=rollup.tree, frame=atRevision(16);
    header("Web’s Health Tree","pg-shop · workload revision 16",c.policy);
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
    box(g,nx,ny,nw,m?159:111,{rx:4,fill:wash(c.policy,13),stroke:c.policy});
    write(g,nx+11,ny+26,"Rooted at "+root.split(":")[1],m?20:23,c.ink,700);
    write(g,nx+11,ny+(m?55:53),"↓  Follow a dependency",m?16:19,c.accent,630);
    write(g,nx+11,ny+(m?82:79),"↑  Trace its effect",m?16:19,c.states.Unhealthy,630);
    if(m)write(g,nx+11,ny+121,nodes.length+" components",16,c.muted);
    write(g,m?14:570,H-(m?15:16),"A parent’s state includes what it depends on.",m?16:18,c.muted);
    const fail=s.spokenAt("The database server failed",.16), follow=s.spokenAt("Follow the branch",.29),
      impact=s.spokenAt("Its failure reaches",.56), cache=s.spokenAt("The cache and storage",.70),
      principle=s.spokenAt("A Health Tree starts",.84);
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
    const kube=s.spokenAt("Kubernetes gives",.25),
      azure=s.spokenAt("The structure query builds",.38), same=s.spokenAt("The names match",.53),
      rule=s.spokenAt("The health query combines",.70), update=s.spokenAt("When new evidence changes",.85);
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
    header("One failed minute opens the alarm","pg-shop · Azure Monitor",c.policy);
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
    const frames=[16,17,18].map(atRevision), indices=[1,4,5];
    const bars=frames.map((frame,i)=>{
      const left=t0+i*spacing, state=frame.states["pg-shop"], color=c.states[state], group=s.layer();
      box(group,left+2,y0b,spacing-5,m?103:95,{rx:4,fill:wash(color,20),stroke:color});
      write(group,left+10,y0b+(m?25:27),"Revision "+frame.revision,m?16:19,c.ink,650);
      write(group,left+10,y0b+(m?52:54),["First zero","1 good minute","2 good minutes"][i],m?16:19,c.muted,590);
      box(group,left+3,y0b+(m?67:62),spacing-7,m?34:31,{rx:2,fill:color});
      write(group,left+spacing/2,y0b+(m?90:84),symbols[state]+" "+state,m?16:18,c.bg,720,"middle");
      return group;
    });
    const clock=write(g,m?14:21,H-(m?15:19),"",m?16:17,c.muted,600);
    const cursor=dot(g,0,0,m?11:13,{fill:"none",stroke:c.ink,"stroke-width":2.5});
    const open=s.spokenAt("The database rule says",.12), fail=s.spokenAt("At ten eleven",.24),
      own=s.spokenAt("That gives the server",.35), clear=s.spokenAt("The rule also sets",.47),
      first=s.spokenAt("At ten fourteen",.56), second=s.spokenAt("At ten fifteen",.73);
    return p=>{
      const n=p<fail?0:p<first?1:p<second?4:5;
      cursor.setAttribute("cx",xs[n]);cursor.setAttribute("cy",y(pts[n].value));
      trail.setAttribute("stroke-dashoffset",1-n/(pts.length-1));
      enter.setAttribute("opacity",.76+.24*stage(p,open,fail));
      exit.setAttribute("opacity",.76+.24*stage(p,clear,first));
      bars.forEach((b,i)=>b.setAttribute("opacity",n>=indices[i]?(p<own&&i===0?.88:1):.50));
      clock.textContent="Sample "+pts[n].minute+" UTC  ·  received "+pts[n].received+" UTC";
    };
  }

  function paths(s) {
    const {g,W,H,m,header,write,box,stage,colors:c}=s;
    const {root,nodes}=rollup.tree, byKey=new Map(nodes.map(node=>[node.key,node]));
    const frame=atRevision(16), depths=[[root]];
    while(depths.at(-1).some(key=>byKey.get(key).children.length)){
      depths.push(depths.at(-1).flatMap(key=>byKey.get(key).children));
    }
    header("How web gets its state","workloadHealth · revision 16",c.policy);
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
      raised=s.spokenAt("Web turns red",.88);
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

  function observation(s, data) {
    const {g,W,H,m,pad,header,write,shape,arrow,pulse,block,box,stage,colors:c}=s;
    const flow=data.flow;
    header("One more destination for the API pod","Controlled Hubble replay · shop namespace",c.master);
    const lx=m?17:26,rx=m?303:501, y1=m?107:104,y2=m?224:264,
      w=m?157:185,h=m?56:68,edgeX=lx+w;
    block(g,lx,y1,w,h,"web-7d4-1","Web pod");
    block(g,lx,y2,w,h,flow.pod,"API pod");
    block(g,rx,y1,w,h,flow.address,"Redis address",c.identity);
    block(g,rx,y2,w,h,"10.2.0.6","PostgreSQL address",c.identity);
    for(const y of [y1,y2]) arrow(g,edgeX+2,y+h/2,rx-3,y+h/2,c.muted);
    const p1=[edgeX+2,y2+h*.42], p2=[rx-3,y1+h*.68];
    arrow(g,...p1,...p2,c.added,4);
    const move=pulse(g,p1,p2,c.added,7);
    write(g,(edgeX+rx)/2,m?202:214,"New flow",m?16:19,c.added,740,"middle");
    const ax=m?pad:742, ay=m?311:78, aw=W-ax-pad, ah=m?174:245;
    box(g,ax,ay,aw,ah,{rx:4,fill:wash(c.master,18),stroke:c.master,"stroke-width":1.4});
    box(g,ax,ay,aw,42,{rx:4,fill:c.master});
    const time=flow.time;
    write(g,ax+16,ay+31,"Hubble  ·  "+time+" UTC",m?20:23,c.bg,710);
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
    header("How the flow becomes a dependency","Hubble + Kubernetes + Azure Resource Graph",c.identity);
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
    arrow(g,left+ww+1,low+hh/2,right-3,low+hh/2,c.added,4);
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
      s.box(warning,x,wy+29,m?133:148,25,{rx:4,fill:c.states[state]});
      write(warning,x+8,wy+47,symbols[state]+" "+state,m?16:17,c.bg,680);
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
    header("Three graphs, three revisions","Controlled replay · selected patch edits",c.added);
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
      box(sheet,x,y,cardW,cardH,{rx:4,fill:wash(color,17),stroke:color,"stroke-width":1.5});
      box(sheet,x,y,cardW,m?40:55,{rx:4,fill:color});
      write(sheet,x+13,y+(m?28:36),name,m?19:23,c.bg,720);
      write(sheet,x+cardW-13,y+(m?28:36),patch.from+" → "+patch.to,m?16:19,c.bg,680,"end");
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
      sheet.setAttribute("opacity",.85+.15*value);
      progress.setAttribute("stroke-dashoffset",1-value);
    });
  }

  function inspect(s,data) {
    const {g,W,H,m,pad,header,write,shape,box,layer,stage,colors:c}=s;
    const {before,after,proofs}=data;
    const cutoff=data.cutoff;
    header("Where did this dependency come from?","Same readings in both runs · "+cutoff+" UTC",c.identity);
    const lx=m?pad:590;
    const colA=m?249:312,colB=m?400:471, colY=m?354:147, rowH=m?28:46;
    const table=s.layer();
    write(table,pad,colY,"Component",m?15:18,c.muted,650);
    write(table,colA,colY,"Original",m?16:18,c.muted,650,"middle");
    write(table,colB,colY,"With the flow",m?16:18,c.muted,650,"middle");
    for(const [i,[key,label]] of [["shopcache","Redis"],["api","API"],["apim-shop","Gateway"]].entries()) {
      const y=colY+(i+1)*rowH;
      write(table,pad,y,label,m?17:22,c.ink,680);
      for(const [x,state] of [[colA,before[key]],[colB,after[key]]]) {
        const width=m?130:153;
        box(table,x-width/2,y-(m?17:21),width,m?24:32,{rx:4,fill:c.states[state]});
        write(table,x,y+1,symbols[state]+"  "+state,m?16:19,c.bg,700,"middle");
      }
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
      box(row,x,y,width,height,{rx:4,fill:wash(c.identity,20),stroke:c.identity});
      box(row,x+2,y+3,5,height-6,{rx:2,fill:c.identity});
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
      proofGroups.forEach((row,i)=>row.setAttribute("opacity",.87+.13*stage(p,marks[i],marks[i]+.06)));
      table.setAttribute("opacity",.87+.13*stage(p,compare,compare+.06));
    };
  }

  return {
    rollup(ctx) {
      const draw=[introduction,derive,signal,paths][ctx.beat];
      return draw(studio(ctx));
    },
    discovery(ctx) {
      const draw=[observation,resolve,patches,inspect][ctx.beat];
      return draw(studio(ctx),ShopReplayCases.discovery);
    }
  };
})();
