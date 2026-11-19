// The guide and the film seek the same teaching figure through its recorded dialogue.
function ShopGkqlScene({g,el,txt,box,line,dot,W,H,mobile:m,beat,stage,spokenAt,inGuide}) {
  const example=ShopGkqlExample;
  const first=example.frames[0], changed=example.frames[1];
  const bg="var(--page)", ink="var(--ink)", muted="var(--muted)", hair="var(--line)";
  const source="var(--master)", operation="var(--input)", row="var(--rule)", output="var(--blue)";
  const healthy="var(--healthy)", unhealthy="var(--unhealthy)";
  const wash=(color,n)=>`color-mix(in srgb,${color} ${n}%,${bg})`;
  const group=(parent=g,attrs={})=>el(parent,"g",attrs);
  function write(parent,x,y,copy,size,color=ink,weight=570,anchor="start",mono=false) {
    return txt(parent,x,y,copy,{fill:color,"font-size":size,"font-weight":weight,"text-anchor":anchor,
      "font-family":mono?"var(--mono)":"var(--sans)"});
  }
  function panel(parent,x,y,w,h,color,opacity=14) {
    return box(parent,x,y,w,h,{rx:5,fill:wash(color,opacity),stroke:wash(color,66),"stroke-width":1.5});
  }
  function heading(title,sourceLine) {
    box(g,0,0,W,H,{fill:bg});
    box(g,0,0,W,m?65:51,{fill:wash(operation,14)});
    box(g,0,0,5,m?65:51,{fill:operation});
    write(g,m?15:25,m?28:33,title,m?23:28,ink,720);
    write(g,m?15:W-25,m?52:32,sourceLine,m?16:18,operation,610,m?"start":"end");
  }
  const reduced=inGuide && matchMedia("(prefers-reduced-motion: reduce)").matches;
  const set=(node,attr,value)=>{
    if(node.getAttribute(attr)!==String(value))node.setAttribute(attr,value);
  };
  const cue=(prefix,fallback)=>spokenAt(prefix,fallback);
  const reveal=(node,p)=>set(node,"opacity",p);
  const stateColor=name=>name==="Healthy"?healthy:unhealthy;
  const stateWord=name=>(name==="Healthy"?"✓ ":"× ")+name;

  // Draw the whole route first. A single pulse follows its wire as the matching
  // spoken cue arrives. A backwards seek restores both the trail and the pulse.
  function wire(parent,points,color,{width=2.5,dashed=false}={}) {
    const d=points.map(([x,y],i)=>(i?"L":"M")+x+" "+y).join(" ");
    const attrs={d,fill:"none","stroke-linejoin":"round","stroke-linecap":"round"};
    el(parent,"path",{...attrs,stroke:wash(color,53),"stroke-width":width,...(dashed?{"stroke-dasharray":"5 5"}:{})});
    const path=el(parent,"path",{...attrs,stroke:color,"stroke-width":width+1,pathLength:1,"stroke-dasharray":"1 1"});
    const head=points.at(-1), previous=points.at(-2), angle=Math.atan2(head[1]-previous[1],head[0]-previous[0]);
    el(parent,"path",{d:"M-8 -4.5 L0 0 L-8 4.5",fill:"none",stroke:color,"stroke-width":2,
      transform:`translate(${head}) rotate(${angle*180/Math.PI})`});
    const lengths=points.slice(1).map(([x,y],i)=>Math.hypot(x-points[i][0],y-points[i][1]));
    const total=lengths.reduce((sum,n)=>sum+n,0);
    const pulse=dot(parent,0,0,4.8,{fill:color,stroke:bg,"stroke-width":1.3});
    return progress=>{
      set(path,"stroke-dashoffset",1-progress);
      const travelling=!reduced && progress>0 && progress<1;
      set(pulse,"visibility",travelling?"visible":"hidden");
      if(!travelling)return;
      let distance=progress*total, index=0;
      while(index<lengths.length-1 && distance>lengths[index])distance-=lengths[index++];
      const a=points[index], b=points[index+1], t=distance/lengths[index];
      set(pulse,"transform",`translate(${a[0]+(b[0]-a[0])*t} ${a[1]+(b[1]-a[1])*t})`);
    };
  }
  function overview() {
    heading("A graph that builds another graph","deploy-health fixture · "+first.time+" UTC");
    const regions=m?[[13,75,454,111],[13,199,454,159],[13,370,454,121]]:
      [[18,70,286,307],[327,70,447,307],[797,70,305,307]];
    const area=(i,title,color)=>{
      const [x,y,w,h]=regions[i], node=group(g,{transform:`translate(${x} ${y})`});
      const backing=panel(node,0,0,w,h,color);
      write(node,12,m?23:31,title,m?18:22,color,700);
      return {node,w,h,backing};
    };
    const input=area(0,"Master graph",source);
    const podsY=m?88:194, depY=m?49:86;
    const dx=[input.w*.27,input.w*.77], px=[input.w*.15,input.w*.40,input.w*.77];
    const inputWires=example.pods.map((pod,i)=>{
      const dep=dx[example.deployments.indexOf(pod.deployment)];
      return wire(input.node,[[px[i],podsY-8],[dep,depY+15]],source,{width:1.8});
    });
    example.deployments.forEach((id,i)=>{
      panel(input.node,dx[i]-32,depY-14,64,28,source,25);
      write(input.node,dx[i],depY+6,id,17,ink,650,"middle");
    });
    example.pods.forEach((_,i)=>dot(input.node,px[i],podsY,7,{fill:source,stroke:ink,"stroke-width":1}));
    if(!m) {
      write(input.node,input.w/2,130,"ControlledBy",16,source,600,"middle");
      write(input.node,input.w/2,236,"3 pods · 2 deployments",19,ink,630,"middle");
    }
    const program=area(1,"GKQL program · two branches",operation);
    const x1=m?13:18, x2=program.w*(m?.57:.58), bw=m?158:155;
    const y1=m?37:61, y2=m?102:183, bh=m?44:67;
    const actions=[];
    const verb=(x,y,w,label,id)=>{
      panel(program.node,x,y,w,bh,operation,22);
      write(program.node,x+9,y+(m?19:27),label,m?17:21,operation,720,"start",true);
      write(program.node,x+9,y+(m?37:52),id,m?15:17,ink,540);
    };
    verb(x1,y1,bw,"graph-match","deployments");
    verb(x2,y1,bw,"nodes","workloads");
    const topY=y1+bh/2;
    write(program.node,(x1+bw+x2)/2,topY-7,"d",m?14:15,row,650,"middle");
    actions.push(wire(program.node,[[x1+bw+1,topY],[x2-3,topY]],row));
    // These are the two named inputs of the actual destination-state join.
    const joinX=166, edgeX=program.w-119;
    verb(joinX,y2,105,"join","withDst");
    verb(edgeX,y2,105,"edges","talks");
    for(const [i,id,port] of [[0,"withSrc","left"],[1,"dstStatus","right"]]) {
      const y=y2+bh*(i?.82:.28);
      write(program.node,12,y+5,id,m?15:17,ink,610);
      write(program.node,126,y-5,port,m?13:15,row,640,"middle");
      actions.push(wire(program.node,[[91,y],[joinX-3,y]],row,{width:1.85}));
    }
    actions.push(wire(program.node,[[joinX+106,y2+bh/2],[edgeX-3,y2+bh/2]],row));
    const target=area(2,"Target · deploymentHealth",output);
    const ends=example.deployments.map((id,i)=>{
      const x=m?target.w*(i?.79:.21):target.w*(i?.73:.27), y=m?77:(i?217:103);
      panel(target.node,x-(m?49:56),y-18,m?98:112,36,output,30);
      write(target.node,x,y+7,id,21,ink,700,"middle");
      return [x,y];
    });
    const [[ax,ay],[bx,by]]=ends;
    const talk=wire(target.node,m?[[ax+50,ay],[bx-51,by]]:
      [[ax,ay+18],[ax,159],[bx,159],[bx,by-20]],output);
    write(target.node,m?(ax+bx)/2:target.w*.5,m?ay-9:149,"Talks",17,output,680,"middle");
    if(!m)write(target.node,target.w/2,265,"2 Workload nodes",18,ink,600,"middle");
    const podCue=cue("The dots in the master graph",.17), query=cue("The query itself",.4);
    const wired=cue("A wire tells",.58), sink=cue("The target graph shows",.76);
    return p=>{
      inputWires.forEach((paint,i)=>paint(stage(p,podCue+i*.025,podCue+.14+i*.025)));
      set(program.backing,"stroke-width",1.5+2*stage(p,query,query+.07));
      actions.forEach((paint,i)=>paint(stage(p,wired+i*.03,wired+.14+i*.03)));
      talk(stage(p,sink,sink+.15));
      set(target.backing,"stroke-width",1.5+2*stage(p,sink,sink+.08));
    };
  }
  function match() {
    heading("Find every place the pattern fits","graph-match · readyPods");
    const px=m?13:18, py=m?76:73, pw=m?454:390, ph=m?153:291;
    panel(g,px,py,pw,ph,operation);
    write(g,px+13,py+(m?25:34),"Pattern in the master graph",m?18:22,operation,700);
    const a=[px+pw*.20,py+(m?78:137)], b=[px+pw*.80,a[1]];
    const relation=wire(g,[[a[0]+23,a[1]],[b[0]-30,b[1]]],source);
    dot(g,...a,22,{fill:wash(source,26),stroke:source,"stroke-width":2.5});
    panel(g,b[0]-28,b[1]-20,56,40,source,26);
    write(g,a[0],a[1]+8,"p",24,source,720,"middle",true);
    write(g,b[0],b[1]+8,"d",24,source,720,"middle",true);
    write(g,(a[0]+b[0])/2,a[1]-10,"c · ControlledBy",m?16:17,source,640,"middle");
    write(g,a[0],a[1]+41,"Pod",17,ink,600,"middle");
    write(g,a[0],a[1]+63,"ready = true",16,source,610,"middle",true);
    write(g,b[0],b[1]+41,"Deployment",17,ink,600,"middle");
    const tx=m?13:441, ty=m?241:73, tw=m?454:661, th=m?251:291;
    panel(g,tx,ty,tw,th,row);
    write(g,tx+13,ty+(m?27:34),"Matching rows",m?21:24,row,710);
    const counter=write(g,tx+tw-14,ty+(m?26:32),"0 of 3",m?17:20,ink,650,"end");
    const xPod=tx+15, xEdge=tx+tw*.48, xOwner=tx+tw*.79;
    const yLabels=ty+(m?62:77);
    [[xPod,"p · Pod"],[xEdge,"c · link"],[xOwner,"d · owner"]].forEach(([x,t])=>write(g,x,yLabels,t,m?17:19,row,690));
    const h=m?48:57, y0=yLabels+13;
    const rows=example.pods.map((pod,i)=>{
      const y=y0+i*h;
      line(g,tx+9,y+h-5,tx+tw-9,y+h-5,{stroke:hair,"stroke-width":1});
      const r=group(g,{"data-gkql-binding":pod.id});
      box(r,tx+7,y,tw-14,h-7,{rx:3,fill:wash(row,17)});
      box(r,tx+7,y,3,h-7,{fill:row});
      write(r,xPod,y+(m?26:31),pod.id,m?17:21,ink,630,"start",true);
      write(r,xEdge,y+(m?26:31),"ControlledBy",m?16:19,source,610);
      write(r,xOwner,y+(m?26:31),pod.deployment,m?18:22,ink,660,"start",true);
      return r;
    });
    const letters=cue("The letters",.2), firstPod=cue("Watch the first web pod",.35);
    const marks=[cue("That pairing",.5),cue("The other web pod",.68),cue("So does the database pod",.83)];
    return p=>{
      relation(stage(p,firstPod,marks[0]));
      set(counter,"fill",p<letters?muted:row);
      const n=marks.filter(at=>p>=at).length;
      counter.textContent=n+" of "+rows.length;
      rows.forEach((r,i)=>{
        const scanning=i===0?.75*stage(p,firstPod,firstPod+.055):0;
        reveal(r,Math.max(scanning,stage(p,marks[i],marks[i]+.055)));
      });
    };
  }
  function count() {
    heading("How many pods are ready?","deploymentHealth · readiness branch");
    // Each pair is [upper left, width, height]. The phone folds the same DAG
    // downward, giving the two sources their own visible input to union.
    const bounds=m?{
      deployments:[13,76,190,45],readyPods:[276,76,190,45],zero:[13,148,190,45],one:[276,148,190,45],
      rows:[168,218,144,44],count:[13,286,209,46],status:[270,286,196,46],target:[13,359,453,133]
    }:{
      deployments:[18,98,155,62],readyPods:[18,231,155,62],zero:[202,98,153,62],one:[202,231,153,62],
      rows:[409,163,105,62],count:[548,163,175,62],status:[548,285,175,62],target:[789,77,313,300]
    };
    const routes=m?[
      [[108,122],[108,145]],[[371,122],[371,145]],
      [[108,194],[108,240],[164,240]],[[371,194],[371,240],[315,240]],
      [[241,263],[241,274],[118,274],[118,283]],[[223,309],[267,309]],
      [[367,334],[367,355]]
    ]:[
      [[174,129],[199,129]],[[174,262],[199,262]],
      [[356,129],[381,129],[381,180],[406,180]],[[356,262],[381,262],[381,208],[406,208]],
      [[515,194],[545,194]],[[636,226],[636,282]],[[724,316],[785,316]]
    ];
    const pipes=routes.map((points,i)=>wire(g,points,i<2?source:i<5?row:output,{width:3}));
    function verb(id,verbName,detail,color) {
      const [x,y,w,h]=bounds[id];
      const panelNode=panel(g,x,y,w,h,color,18);
      write(g,x+w/2,y+(m?19:25),verbName,m?18:20,color,690,"middle",true);
      write(g,x+w/2,y+(m?38:49),detail,m?15:17,ink,580,"middle");
      return panelNode;
    }
    verb("deployments","graph-match","each Deployment",source);
    verb("readyPods","graph-match","each ready pod",source);
    verb("zero","project","d, n = 0",row);
    verb("one","project","d, n = 1",row);
    verb("rows","union","all rows",row);
    const sum=verb("count","summarize","sum(n) by d",operation);
    const status=verb("status","extend","readyPods > "+example.minReady,operation);
    const [x,y,w,h]=bounds.target;
    panel(g,x,y,w,h,output,16);
    write(g,x+12,y+(m?23:34),"nodes · workloadState",m?19:23,output,710);
    if(!m)write(g,x+12,y+64,"Workload identity = d",18,ink,560);
    const results=example.deployments.map((id,i)=>{
      const cx=m?x+13+i*(w/2):x+14, cy=m?y+36:y+86+i*91;
      const cw=m?w/2-22:w-28, ch=m?84:76;
      panel(g,cx,cy,cw,ch,output,19);
      write(g,cx+10,cy+(m?25:28),id,m?21:25,ink,720,"start",true);
      const qty=write(g,cx+cw-8,cy+(m?25:28),"—",m?17:19,output,680,"end");
      const flag=group();
      box(flag,cx+1,cy+ch-30,cw-2,29,{rx:3,fill:healthy});
      write(flag,cx+cw/2,cy+ch-9,stateWord(first.states[id]),m?17:18,bg,710,"middle");
      return {id,qty,flag};
    });
    const zero=cue("Start each deployment",.12), one=cue("Add one",.27), merge=cue("The summarize step",.40);
    const web=cue("Web gets two",.51), db=cue("The database gets one",.6);
    const cutoff=cue("The threshold",.7), keys=cue("Web appears once",.85);
    return p=>{
      pipes[0](stage(p,zero,one));pipes[1](stage(p,one,merge));
      pipes[2](stage(p,zero+.035,merge));pipes[3](stage(p,one+.035,merge+.025));
      pipes[4](stage(p,merge,web));pipes[5](stage(p,db,cutoff));pipes[6](stage(p,cutoff,keys));
      set(sum,"stroke-width",1.5+2*stage(p,merge,merge+.05));
      set(status,"stroke-width",1.5+2*stage(p,cutoff,cutoff+.05));
      results.forEach(({id,qty,flag},i)=>{
        qty.textContent=p>=(i?db:web)?first.counts[id]+" ready":"—";
        reveal(flag,stage(p,cutoff,cutoff+.08));
      });
    };
  }
  function traffic() {
    heading("Two pod calls become one workload link","deploymentHealth · Talks");
    const sx=m?13:18, sw=W-2*sx, sy=m?76:68, sh=m?170:144;
    panel(g,sx,sy,sw,sh,source,11);
    write(g,sx+12,sy+(m?25:28),"Master graph · Calls",m?19:22,source,700);
    const clock=write(g,sx+sw-12,sy+(m?24:28),first.time+" UTC",m?16:19,ink,630,"end",true);
    const rowX=m?35:95, targetX=m?320:820;
    const ay=sy+(m?64:58), by=sy+(m?120:106), dy=(ay+by)/2;
    const targetId=example.calls[0].to;
    const podEnds=example.calls.map((call,i)=>{
      const y=i===0?ay:by;
      dot(g,rowX-8,y,5.5,{fill:source});
      write(g,rowX+3,y+6,call.from,m?16:19,ink,630,"start",true);
      const start=m?175:301, bend=m?215:512, stop=targetX-12;
      const track=wire(g,[[start,y],[bend,y],[bend+(m?23:75),dy],[stop,dy]],source);
      if(!m)write(g,start+45,y-9,call.at,15,source,610,"start",true);
      return track;
    });
    dot(g,targetX-5,dy,6,{fill:source});
    write(g,targetX+6,dy+6,targetId,m?16:20,ink,640,"start",true);
    const readyText=write(g,targetX+5,dy+32,"Ready = true",m?15:17,healthy,630);
    write(g,rowX+2,sy+sh-11,"Deployment web",m?15:17,source,600);
    write(g,targetX+3,sy+sh-11,"Deployment db",m?15:17,source,600);
    const oy=m?272:228, oh=m?219:161;
    panel(g,sx,oy,sw,oh,output,14);
    write(g,sx+12,oy+(m?26:30),"Target graph · "+(m?"Talks":"deploymentHealth"),m?20:22,output,700);
    write(g,sx+sw-12,oy+(m?25:29),"edges · talks",m?16:18,output,580,"end",true);
    const left=m?25:79, right=m?319:813, cw=m?135:222, cy=oy+(m?59:47), ch=m?94:81;
    const outs=example.deployments.map((id,i)=>{
      const x=i?right:left;
      const tile=panel(g,x,cy,cw,ch,healthy,18);
      write(g,x+11,cy+(m?26:29),id,m?21:24,ink,700,"start",true);
      const count=write(g,m?x+11:x+cw-11,cy+(m?50:29),first.counts[id]+" ready",
        m?17:18,ink,610,m?"start":"end");
      const band=box(g,x+1,cy+ch-26,cw-2,25,{rx:3,fill:healthy});
      const state=write(g,x+cw/2,cy+ch-7,stateWord(first.states[id]),m?16:18,bg,700,"middle");
      return {id,tile,count,band,state};
    });
    const wy=cy+ch/2, start=left+cw+3, end=right-3;
    const links=[healthy,unhealthy].map(color=>{
      const overlay=group();
      const path=wire(overlay,[[start,wy],[end,wy]],color,{width:4});
      return {overlay,path};
    });
    const edgeLabel=write(g,(start+end)/2,wy-12,"Talks",m?19:22,healthy,700,"middle");
    const edgeState=write(g,(start+end)/2,wy+26,stateWord(first.talks),m?15:19,healthy,640,"middle");
    const result=write(g,sx+12,oy+oh-(m?18:10),"2 pod calls · 1 pair of workloads",m?16:18,muted,570);
    const follow=cue("Follow the two pod calls",.16), both=cue("Both reach",.34), event=cue("At ten oh three",.5);
    const failed=cue("Its count falls",.62), edge=cue("The edge takes",.74), last=cue("Web still has",.9);
    return p=>{
      podEnds.forEach((paint,i)=>paint(stage(p,follow+i*.025,both+i*.025)));
      const time=p>=event?changed:first, tally=p>=failed?changed:first, rel=p>=edge?changed:first;
      clock.textContent=time.time+" UTC";
      readyText.textContent="Ready = "+time.ready[targetId];
      set(readyText,"fill",time.ready[targetId]?healthy:unhealthy);
      outs.forEach(({id,tile,count,band,state})=>{
        const name=tally.states[id], color=stateColor(name);
        set(tile,"fill",wash(color,20));set(tile,"stroke",color);
        set(band,"fill",color);state.textContent=stateWord(name);count.textContent=tally.counts[id]+" ready";
      });
      links.forEach(({overlay,path},i)=>{
        reveal(overlay,i===0?(p<edge?1:0):(p>=edge?1:0));
        path(i===0?stage(p,both,event):stage(p,edge,last));
      });
      set(edgeLabel,"fill",stateColor(rel.talks));set(edgeState,"fill",stateColor(rel.talks));
      edgeState.textContent=stateWord(rel.talks);
      result.textContent=p>=event?"10:03 UTC · db pod is running, readiness is false":"2 pod calls · 1 pair of workloads";
    };
  }

  return [overview,match,count,traffic][beat]();
}
