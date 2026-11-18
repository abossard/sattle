// The pg-shop fixture supplies the samples and the two replay comparisons.
const ChangeScenes = (() => {
  const state = {
    Healthy:   { ink: "#0e604b", pale: "#bfe6cc", mark: "✓" },
    Degraded:  { ink: "#86510a", pale: "#ffda81", mark: "▲" },
    Unhealthy: { ink: "#a02643", pale: "#fac7cf", mark: "×" },
    Unknown:   { ink: "#65418e", pale: "#e5d5fb", mark: "?" }
  };
  function paint(node, attrs) {
    for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, value);
  }
  function lettering(size, weight=500, fill="#172d3b", anchor="start") {
    return {"font-size":size,"font-weight":weight,fill,"text-anchor":anchor};
  }
  function caption(ctx, group, x, y, text, size, color, anchor="start") {
    return ctx.txt(group,x,y,text,{...lettering(size,700,color,anchor),"letter-spacing":0.45});
  }
  function path(ctx, group, d, stroke, width, attrs={}) {
    return ctx.el(group,"path",{d,fill:"none",stroke,"stroke-width":width,"stroke-linecap":"round","stroke-linejoin":"round",...attrs});
  }
  function movingStroke(ctx, group, d, stroke, width) {
    const line = path(ctx,group,d,stroke,width,{pathLength:1,"stroke-dasharray":"1 1"});
    return fraction => paint(line,{"stroke-dashoffset":1-fraction,opacity:fraction>0?1:0});
  }

  // Sample time is the common x coordinate for both the meter and the computed
  // health states. Highlight callers one by one without inventing time delays.
  function incident(ctx) {
    const {g,el,txt,box,dot,line,stage,colors,W,H,mobile,beat} = ctx;
    const left = mobile?122:208, right=W-(mobile?10:19);
    const width=(right-left)/7, x=i=>left+(i+.5)*width;
    const gridTop=mobile?77:52, traceBase=mobile?163:114, traceHeight=mobile?55:35;
    const labelY=mobile?202:153, rowTop=mobile?222:166, rowH=mobile?35:31;
    const lanes=["pg-shop","Database service","API","Gateway","Web","Cache"];
    const cellLabels=mobile?16:18;
    const blue=colors.blue, red=state.Unhealthy.ink;
    const cursorColor="#454f96";
    const minutes=["10:10","10:11","10:12","10:13","10:14","10:15","10:16"];
    const values=[1,0,0,0,1,1,1];

    caption(ctx,g,15,26,"pg-shop  /  database availability",mobile?18:24,colors.ink);
    const running=txt(g,W-15,mobile?52:26,"",lettering(mobile?17:20,650,cursorColor,"end"));
    txt(g,15,traceBase-traceHeight+3,"Meter",lettering(mobile?16:19,700));
    txt(g,15,traceBase+3,mobile?"1 up  /  0 down":"1 answers   /   0 silent",lettering(mobile?13:17,500,colors.muted));
    caption(ctx,g,15,labelY,"Sample · UTC",mobile?14:17,colors.muted);
    for (const [i,t] of minutes.entries()) {
      line(g,x(i),gridTop,x(i),rowTop+lanes.length*rowH,{stroke:"#b9c5c4","stroke-width":1,"stroke-dasharray":"2 5"});
      txt(g,x(i),labelY,t,lettering(mobile?14:18,600,colors.ink,"middle"));
    }
    const meterY=v=>traceBase-v*traceHeight;
    let trace="M"+x(0)+" "+meterY(values[0]);
    values.slice(1).forEach((v,i)=>{trace+="H"+x(i+1)+"V"+meterY(v);});
    path(ctx,g,trace,"#9cbebd",3);
    const activeTrace=path(ctx,g,trace,blue,mobile?4:5);
    const clipId="change-meter-"+beat+(mobile?"-m":"-w");
    const defs=el(g,"defs"),clip=el(defs,"clipPath",{id:clipId});
    const mask=box(clip,left,gridTop,right-left,traceBase-gridTop+7,{fill:"white"});
    activeTrace.setAttribute("clip-path","url(#"+clipId+")");

    const rows=lanes.map((lane,r)=>{
      const y=rowTop+r*rowH;
      const cells=[];
      txt(g,left-12,y+rowH*.68,lane,lettering(mobile?15:19,r===0?750:550,colors.ink,"end"));
      for (let i=0;i<minutes.length;i++) {
        const status=(r<5 && i>=1 && i<=4)?state.Unhealthy:state.Healthy;
        const cell=el(g,"g");
        box(cell,left+i*width+2,y+2,width-4,rowH-4,{fill:status.pale,rx:4});
        txt(cell,x(i),y+rowH*.73,status.mark,lettering(cellLabels,800,status.ink,"middle"));
        cells.push(cell);
      }
      return cells;
    });
    const vertical=line(g,x(0),gridTop,x(0),rowTop+lanes.length*rowH,{stroke:cursorColor,"stroke-width":2,"stroke-dasharray":"4 4","pointer-events":"none"});
    const marker=dot(g,x(0),meterY(1),mobile?6:8,{fill:blue,stroke:"white","stroke-width":2});
    // Each minute is a complete graph result. This outline walks the caller
    // chain inside the 10:11 column while the source timestamp remains fixed.
    const focus=box(g,left+width+1,rowTop,width-2,rowH,{"fill":"none",stroke:red,"stroke-width":3,rx:5});
    const footer=txt(g,15,H-(mobile?36:17),"",lettering(mobile?17:21,700));
    const footer2=txt(g,15,H-12,"",lettering(15,550,colors.muted));
    return p=>{
      let minute, activeRow=0, message, second="";
      if (beat===0) {
        minute=stage(p,.12,.4);
        message=p<.52?"One sample marks the start of the alarm.":mobile?"Sample at 10:11:00.":"Sample at 10:11:00   →   recorder receives it at 10:12:01   ·   61 seconds later";
        if(mobile && p>=.52)second="The recorder gets it 61 seconds later, at 10:12:01.";
      } else if (beat===1) {
        minute=1;
        activeRow=p<.4?0:p<.53?1:p<.67?2:p<.76?3:4;
        message=p<.8?"pg-shop   →   database service   →   API":mobile?"Gateway and web inherit the alarm.":"Both entry points inherit the alarm. The cache stays healthy.";
        if(mobile)second=p<.8?"Read down the column at the same sample time.":"The cache stays healthy.";
      } else {
        minute=3+stage(p,0,.16)+stage(p,.52,.68);
        message=minute<4?"The alarm is still on.":minute<5?"10:14   ·   First good minute. The alarm stays on.":"10:15   ·   Two good minutes. The alarm clears.";
        if(mobile && minute>=4){message=minute<5?"10:14   ·   First good minute.":"10:15   ·   Two good minutes.";second=minute<5?"The alarm stays on.":"The alarm clears for the callers too.";}
      }
      const current=Math.min(6,Math.floor(minute+.000001));
      running.textContent=minutes[current]+" UTC";
      paint(mask,{width:x(minute)-left+2});
      paint(vertical,{x1:x(minute),x2:x(minute)});
      paint(marker,{cx:x(minute),cy:meterY(values[current])});
      paint(focus,{x:left+current*width+1,y:rowTop+activeRow*rowH,opacity:beat===1?1:0});
      rows.forEach((cells,r)=>cells.forEach((cell,i)=>{
        // Future cells serve as an axis until their recorded minute arrives.
        const withinTime=i<=current;
        const emphasis=beat===1 && i===current && r>activeRow && r<5 ? .54:1;
        cell.setAttribute("opacity",withinTime?emphasis:.1);
      }));
      footer.textContent=message;
      footer2.textContent=second;
    };
  }

  // A counterfactual circuit: both runs share the warning on the left. Only
  // the changed run closes the gap between the cache and the API.
  function dependency(ctx) {
    const {g,el,txt,box,dot,line,stage,colors,W,H,mobile,beat} = ctx;
    const amber=state.Degraded.ink, green=state.Healthy.ink;
    const trace=colors.ink, quiet="#a5b8bd";
    const anchor="middle", small=mobile?15:17;
    const sx=mobile?100:116,sy=mobile?91:170,r=mobile?45:61;
    if(mobile)caption(ctx,g,W-18,28,"10:06:30 UTC",17,colors.ink,"end");
    else caption(ctx,g,32,33,"Same readings · 10:06:30 UTC",23,colors.ink);
    const fork=mobile?{x:240,y:171}:{x:293,y:sy};
    const web={x:mobile?348:sx,y:mobile?91:318};
    const rootPath=mobile?`M${sx} ${sy+r}V${fork.y}H${fork.x}`:`M${sx+r} ${sy}H${fork.x}`;
    const commonWeb=mobile?`M${sx+r} ${sy}H${web.x-19}`:`M${sx} ${sy+r}V${web.y-19}`;
    path(ctx,g,commonWeb,amber,4);
    path(ctx,g,rootPath,amber,4);
    dot(g,fork.x,fork.y,5,{fill:amber});
    dot(g,sx,sy,r,{fill:"#fff0c9",stroke:"#d9a548","stroke-width":2});
    el(g,"circle",{cx:sx,cy:sy,r:r-6,fill:"none",stroke:amber,"stroke-width":6,
      pathLength:100,"stroke-dasharray":"85 100",transform:`rotate(-90 ${sx} ${sy})`});
    txt(g,sx,sy-3,"85%",lettering(mobile?29:36,800,amber,anchor));
    txt(g,sx,sy+(mobile?24:31),"load",lettering(mobile?14:17,600,amber,anchor));
    caption(ctx,g,sx,mobile?28:sy-r-14,"shopcache",mobile?19:22,trace,anchor);
    dot(g,web.x,web.y,18,{fill:state.Degraded.pale,stroke:amber,"stroke-width":2});
    txt(g,web.x,web.y+6,"▲",lettering(16,800,amber,anchor));
    txt(g,web.x+(mobile?0:29),web.y+(mobile?39:6),mobile?"Web":"Web in both runs",lettering(mobile?18:20,700,trace,mobile?anchor:"start"));
    if(mobile)txt(g,web.x,web.y+59,"Degraded in both runs",lettering(14,600,amber,anchor));

    const branches=[], changed={};
    for(let i=0;i<2;i++){
      const isEdit=i===1;
      const lane=mobile?(isEdit?349:126):(isEdit?270:93);
      const start=mobile?{x:lane,y:248}:{x:511,y:lane};
      const end=mobile?{x:lane,y:287}:{x:576,y:lane};
      const api=mobile?{x:lane,y:337}:{x:739,y:lane};
      const gw=mobile?{x:lane,y:428}:{x:960,y:lane};
      const group=el(g,"g");
      branches.push(group);
      const arriving=mobile?`M${fork.x} ${fork.y}H${lane}V${start.y}`:`M${fork.x} ${fork.y}V${lane}H${start.x}`;
      path(ctx,group,arriving,amber,3);
      const name=isEdit?"Changed run":"Original run";
      if(mobile){
        box(group,lane-87,192,174,39,{rx:18,fill:isEdit?"#e5dbf2":"#dceafa"});
        txt(group,lane,217,name,lettering(18,750,isEdit?colors.purple:colors.blue,anchor));
      }else{
        caption(ctx,group,326,lane-20,name,23,isEdit?colors.purple:colors.blue);
      }
      [start,end].forEach(pt=>dot(group,pt.x,pt.y,6,{fill:colors.paper,stroke:amber,"stroke-width":3}));
      const downstream=mobile?`M${end.x} ${end.y+6}V${api.y-19}`:`M${end.x+6} ${end.y}H${api.x-19}`;
      const last=mobile?`M${api.x} ${api.y+19}V${gw.y-19}`:`M${api.x+19} ${api.y}H${gw.x-19}`;
      path(ctx,group,downstream,quiet,2,{"stroke-dasharray":"4 6"});
      path(ctx,group,last,quiet,2);
      const downFlow=movingStroke(ctx,group,downstream,amber,4);
      const gwFlow=movingStroke(ctx,group,last,amber,4);
      const switchLine=path(ctx,group,"",isEdit?colors.purple:quiet,5);
      const note=txt(group,mobile?lane+(isEdit?20:-20):(start.x+end.x)/2,
        mobile?272:lane+39,"",lettering(mobile?13:17,650,isEdit?colors.purple:colors.muted,mobile?(isEdit?"start":"end"):anchor));
      const taps=[];
      for(const [label,pt] of [["API",api],["Gateway",gw]]){
        const disk=dot(group,pt.x,pt.y,17,{fill:state.Healthy.pale,stroke:green,"stroke-width":2});
        const symbol=txt(group,pt.x,pt.y+6,"✓",lettering(19,800,green,anchor));
        if(mobile) {
          txt(group,pt.x+25,pt.y+4,label,lettering(small,700,trace));
        }else{
          txt(group,pt.x,pt.y-29,label,lettering(21,700,trace,anchor));
        }
        const stateLabel=txt(group,pt.x,pt.y+(mobile?37:44),"Healthy",lettering(mobile?14:18,650,green,anchor));
        taps.push({disk,symbol,stateLabel});
      }
      if(isEdit)Object.assign(changed,{switchLine,note,downFlow,gwFlow,taps,start,end});
      else{
        const openX=mobile?end.x-19:end.x-3,openY=mobile?end.y-3:end.y-26;
        switchLine.setAttribute("d",`M${start.x} ${start.y}L${openX} ${openY}`);
        note.textContent=mobile?"Open":"No cache setting";
      }
    }
    const proof=txt(g,mobile?W/2:325,H-(mobile?9:21),"",lettering(mobile?15:21,650,colors.purple,mobile?anchor:"start"));
    function tapState(tap,status){
      const style=state[status];
      paint(tap.disk,{fill:style.pale,stroke:style.ink});
      tap.symbol.textContent=style.mark;tap.symbol.setAttribute("fill",style.ink);
      tap.stateLabel.textContent=status;tap.stateLabel.setAttribute("fill",style.ink);
    }
    return p=>{
      const close=beat===0?0:beat===1?stage(p,.24,.72):1;
      const {start,end}=changed;
      const tipX=mobile?end.x-19*(1-close):end.x-3*(1-close);
      const tipY=mobile?end.y-3*(1-close):end.y-26*(1-close);
      paint(changed.switchLine,{d:`M${start.x} ${start.y}L${tipX} ${tipY}`,stroke:close>0?colors.purple:quiet});
      changed.note.textContent=mobile?(close===1?"Closed":"Open"):(close===1?"CACHE_HOST matches":"No match yet");
      branches[1].setAttribute("opacity",beat===0?.16:1);
      const apiFlow=beat===2?stage(p,.16,.35):0;
      const gwFlow=beat===2?stage(p,.34,.55):0;
      changed.downFlow(apiFlow);changed.gwFlow(gwFlow);
      changed.taps.forEach((tap,i)=>{
        if(beat<2){
          paint(tap.disk,{fill:"#eeeae3",stroke:quiet});tap.symbol.textContent="·";tap.symbol.setAttribute("fill",quiet);
          tap.stateLabel.textContent="";
        }else tapState(tap,(i?gwFlow:apiFlow)===1?"Degraded":"Healthy");
      });
      const lineText=beat===0?"The web service already uses this cache.":beat===1?(mobile?"CACHE_HOST = shopcache.redis.cache.windows.net":"API deployment adds   CACHE_HOST = shopcache.redis.cache.windows.net"):
        (mobile?"One new link changes the gateway’s health.":"One new dependency carries the same cache warning to the gateway.");
      proof.textContent=lineText;
    };
  }

  // The intersection contains only hosts named in the API settings AND
  // backed by a matching Redis resource. Health evidence is a separate test.
  function azure(ctx) {
    const {g,el,txt,dot,line,box,stage,colors,W,H,mobile,beat} = ctx;
    const a=mobile?{x:240,y:143}:{x:389,y:171};
    const b=mobile?{x:240,y:284}:{x:715,y:171};
    const rx=mobile?209:319, ry=mobile?114:129;
    const mid={x:(a.x+b.x)/2,y:(a.y+b.y)/2};
    const ellipse=(group,point,attrs)=>el(group,"ellipse",{cx:point.x,cy:point.y,rx,ry,...attrs});
    ellipse(g,a,{fill:"#e9dff3",stroke:"#9074b2","stroke-width":2.2});
    ellipse(g,b,{fill:"#d7eaf3",stroke:"#66a1b9","stroke-width":2.2});
    const id="change-join-"+beat+(mobile?"-m":"-w");
    const clip=el(el(g,"defs"),"clipPath",{id});
    ellipse(clip,a,{fill:"white"});
    const lens=ellipse(g,b,{fill:"#d2dfd8","clip-path":"url(#"+id+")"});
    ellipse(g,a,{fill:"none",stroke:"#9074b2","stroke-width":2});
    const center="middle";
    const leftAnchor=mobile?{x:240,y:106}:{x:232,y:173};
    const rightAnchor=mobile?{x:240,y:296}:{x:875,y:206};
    const joint={x:mid.x,y:mid.y+(mobile?14:16)};
    const startColor=colors.purple;
    if(mobile){
      caption(ctx,g,240,54,"Hosts the API names",18,startColor,center);
      txt(g,240,80,"CACHE_HOST",lettering(14,650,startColor,center));
      txt(g,240,139,".redis.cache.windows.net",lettering(15,500,startColor,center));
      caption(ctx,g,240,347,"Redis hosts in Azure",18,colors.blue,center);
      txt(g,240,371,"shopcache",lettering(17,650,colors.blue,center));
    }else{
      caption(ctx,g,96,36,"Hosts named by the API",23,startColor);
      caption(ctx,g,1022,36,"Redis hosts in Azure inventory",23,colors.blue,"end");
      txt(g,leftAnchor.x,136,"API setting   ·   CACHE_HOST",lettering(17,600,startColor,center));
      txt(g,leftAnchor.x,220,".redis.cache.windows.net",lettering(17,500,startColor,center));
      txt(g,rightAnchor.x,116,"Redis resource names",lettering(17,600,colors.blue,center));
      txt(g,rightAnchor.x,154,"shopcache",lettering(22,650,colors.blue,center));
    }
    const lensLabel=txt(g,mid.x,mid.y-(mobile?12:27),"Matching host",lettering(mobile?16:21,700,colors.ink,center));
    const waiting=txt(g,joint.x,joint.y,"No match yet",lettering(mobile?17:21,550,colors.muted,center));
    function hostToken(ink,fill){
      const token=el(g,"g");
      box(token,-(mobile?70:83),-(mobile?17:21),mobile?140:166,mobile?32:39,{fill,stroke:ink,"stroke-width":1.8,rx:17});
      txt(token,0,mobile?5:7,"offers-cache",lettering(mobile?18:22,700,ink,center));
      return token;
    }
    const fromSetting=hostToken(startColor,"#f5effa");
    const fromAzure=hostToken(colors.blue,"#eef9fc");
    const match=hostToken(colors.teal,"#dbf2e2");
    const originY=mobile?412:321;
    line(g,mobile?13:49,originY,W-(mobile?13:49),originY,{stroke:"#aac0bb","stroke-width":1.5});
    const y=mobile?459:371;
    const lhs=mobile?36:132,dest=mobile?233:485;
    const statusX=mobile?415:959;
    txt(g,lhs,y,"API",lettering(mobile?19:25,750,colors.ink,"middle"));
    const arrowPath=`M${lhs+22} ${y-6}H${dest-(mobile?67:90)}`;
    const faint=path(ctx,g,arrowPath,"#8b9b9e",2,{"stroke-dasharray":"5 5"});
    const arrow=movingStroke(ctx,g,arrowPath,colors.teal,3);
    const head=path(ctx,g,`M${dest-(mobile?75:98)} ${y-13}l8 7 -8 7`,colors.teal,3);
    const destination=txt(g,dest,y,"?",lettering(mobile?19:26,700,colors.muted,center));
    const status=txt(g,statusX,y,"",lettering(mobile?18:23,750,state.Unknown.ink,center));
    const detail=txt(g,statusX,y+(mobile?26:23),"",lettering(mobile?13:16,550,colors.muted,center));
    const edgeNote=txt(g,(lhs+dest)/2,mobile?439:348,"",lettering(mobile?13:16,650,colors.teal,center));
    function moveToken(token,from,to,t,opacity){
      paint(token,{transform:`translate(${from.x+(to.x-from.x)*t} ${from.y+(to.y-from.y)*t})`,opacity});
    }
    return p=>{
      const appeared=beat===0?0:beat===1?stage(p,.04,.22):1;
      const joined=beat===0?0:beat===1?stage(p,.32,.61):1;
      const target=beat===0?0:beat===1?stage(p,.58,.77):1;
      // Only one token remains once the source records identify the same host.
      const merge=stage(joined,.55,1);
      moveToken(fromSetting,leftAnchor,joint,joined,1-merge);
      moveToken(fromAzure,rightAnchor,joint,joined,appeared*(1-merge));
      moveToken(match,joint,joint,0,merge);
      paint(waiting,{opacity:1-stage(joined,0,.19)});
      lens.setAttribute("fill",joined>.52?"#ccebd8":"#e0e7e0");
      lensLabel.textContent=joined>.75?"One matching host":"Matching hosts";
      if(mobile)lensLabel.setAttribute("opacity",Math.max(1-stage(joined,0,.14),stage(joined,.96,1)));
      paint(faint,{opacity:1-target});arrow(target);paint(head,{opacity:target});
      destination.textContent=target>.44?"offers-cache":"?";
      destination.setAttribute("fill",target>.44?colors.teal:colors.muted);
      edgeNote.textContent=target>.44?"uses":mobile?"Looking up host":"Looking up the named host";
      const showHealth=beat===2;
      status.textContent=showHealth?"?  Unknown":"";
      detail.textContent=showHealth?"0 meter readings":"";
    };
  }

  return {incident, dependency, azure};
})();
