// Each figure can be drawn at any point in its narration, including after a seek.
const FoundryWorkScenes=(() => {
  const ink="#17313d", muted="#465e69", blue="#175e86", teal="#096859", yellow="#865512", purple="#65458e", red="#982e48";
  const statuses={
    Healthy:["✓","#c6ebd3","#126047"], Degraded:["▲","#ffe2a0","#855308"],
    Unhealthy:["×","#ffd1d8","#982841"], Unknown:["?","#e5d8f9","#63418a"]
  };
  function pen(ctx) {
    const {g,el,txt,box,dot,line}=ctx;
    const label=(p,x,y,s,size=18,color=ink,weight=550,anchor="start")=>txt(p,x,y,s,{
      fill:color,"font-size":size,"font-weight":weight,"text-anchor":anchor
    });
    const path=(p,d,attrs={})=>el(p,"path",{d,fill:"none","stroke-linecap":"round","stroke-linejoin":"round",...attrs});
    const group=(p=g)=>el(p,"g");
    const rect=(p,x,y,w,h,fill,stroke="none",r=0)=>box(p,x,y,w,h,{fill,stroke,rx:r});
    const rule=(p,x1,y1,x2,y2,color="#abbaaf",w=1)=>line(p,x1,y1,x2,y2,{stroke:color,"stroke-width":w});
    const circle=(p,x,y,r,fill,stroke="none",w=1)=>dot(p,x,y,r,{fill,stroke,"stroke-width":w});
    const opacity=(p,v)=>p.setAttribute("opacity",v);
    function travel(p,d,color,w=3) {
      path(p,d,{stroke:color,"stroke-width":w,opacity:.25});
      const stroke=path(p,d,{stroke:color,"stroke-width":w,pathLength:1,"stroke-dasharray":"1 1"});
      const pointer=circle(p,0,0,w+2,color);
      const length=stroke.getTotalLength();
      return v=>{
        const z=Math.min(1,Math.max(0,v)),point=stroke.getPointAtLength(length*z);
        stroke.setAttribute("stroke-dashoffset",1-z);opacity(stroke,z===0?0:1);
        pointer.setAttribute("cx",point.x);pointer.setAttribute("cy",point.y);
        opacity(pointer,z>0&&z<1?1:0);
      };
    }
    return {label,path,group,rect,rule,circle,opacity,travel};
  }

  // A score for three kinds of work. No line connects the model's summary to
  // the payout lane: that job has its own trigger and its own authority.
  function jobs(ctx) {
    const {g,W,H,mobile:m,beat,stage}=ctx;
    const {label,path,group,rect,rule,circle,opacity,travel}=pen(ctx);
    rect(g,0,0,W,H,"#f5f3e8","none",12);
    label(g,18,m?31:38,"Work behind one return",m?23:30);
    label(g,W-18,m?29:37,"Order O-104  /  return R-104",m?14:20,muted,600,"end");
    const x=m?[62,235,409]:[243,579,930];
    const ys=m?[123,255,387]:[105,216,327];
    const lanes=[
      {title:"Review",ink:purple,pale:"#e9dff4",steps:["Return queued","Review agent","Case summary"]},
      {title:"Payout",ink:teal,pale:"#cbe8d8",steps:["Approved case","Refund worker","Processor call"]},
      {title:"Reconciliation",ink:blue,pale:"#dce9f1",steps:["Schedule","Reconciler","Receipt check"]}
    ];
    const runs=lanes.map((row,i)=>{
      const y=ys[i],lane=group(), gap=m?34:50;
      rect(lane,8,y-(m?48:42),W-16,m?104:91,row.pale,"none",8);
      label(lane,m?19:25,m?y-24:y+6,row.title,m?18:23,row.ink,720);
      const threads=[0,1].map(j=>travel(lane,`M${x[j]+gap} ${y} H${x[j+1]-gap}`,row.ink,m?3:4));
      for(const [j,cx] of x.entries()) {
        if(j===0) {
          if(i===2) {
            circle(lane,cx,y,14,row.ink);path(lane,`M${cx},${y-8} v8 l6,3`,{stroke:"#fff","stroke-width":2.5});
          } else {
            for(let k=2;k>=0;k--)rect(lane,cx-19+k*4,y-11-k*3,32,23,k===0?row.ink:row.pale,row.ink,3);
          }
        } else if(j===1) {
          path(lane,`M${cx-21} ${y} l9,-14 h24 l9,14 -9,14 h-24 Z`,{fill:row.ink});
          if(i===0) {circle(lane,cx-6,y,2.5,"white");circle(lane,cx,y,2.5,"white");circle(lane,cx+6,y,2.5,"white");}
          else path(lane,`M${cx-8},${y} h16 M${cx+2},${y-5} l6,5 -6,5`,{stroke:"white","stroke-width":2.3});
        } else {
          path(lane,`M${cx-13} ${y-15} h19 l7,7 v23 h-26 Z`,{fill:"#fff",stroke:row.ink,"stroke-width":2});
          rule(lane,cx-7,y-1,cx+6,y-1,row.ink,2);rule(lane,cx-7,y+5,cx+3,y+5,row.ink,2);
        }
        label(lane,cx,y+(m?39:37),row.steps[j],m?15.5:18,row.ink,660,"middle");
      }
      return {lane,threads};
    });
    const foot=m?
      ["The queue holds the return after the customer leaves.","The model drafts a summary for the reviewer.","The worker sends the approved amount and refund ID."]:
      ["The warehouse receives the returned items. The return case can stay in the queue after chat ends.","The review agent reads the receipt and drafts a summary for the reviewer.","The worker sends the approved amount and refund ID. The scheduled job checks settlement later."];
    label(g,17,H-11,foot[beat],m?15.5:18,muted,620);
    return p=>runs.forEach(({lane,threads},i)=>{
      opacity(lane,i===0||beat===2?1:.48);
      const v=i===0?(beat===0?.20*stage(p,.05,.75):beat===1?.20+.80*stage(p,.08,.81):1):
        beat<2?0:i===1?stage(p,.03,.54):stage(p,.52,.91);
      threads.forEach((f,j)=>f(Math.max(0,Math.min(1,v*2-j))));
    });
  }

  // A state wheel keeps four different questions about one return visible.
  // A timeout on the caller does not identify the remote processor's state.
  function failure(ctx) {
    const {g,W,H,mobile:m,beat,stage}=ctx;
    const {label,path,group,rect,rule,circle,opacity}=pen(ctx);
    rect(g,0,0,W,H,"#f8f1ec","none",12);
    label(g,18,m?32:38,"Which part can still do its job?",m?23:31);
    if(!m)label(g,W-22,37,"Late parcel  /  O-104  /  R-104",18,muted,600,"end");
    const cx=m?W/2:267, cy=m?170:208, ro=m?106:141, ri=m?48:66;
    const places=[
      {name:"Review",state:"Degraded",text:"The case summary arrives late.",introduced:0},
      {name:"Payout",state:"Unhealthy",text:"The payment call times out.",introduced:1},
      {name:"Processor",state:"Unknown",text:"Its internal readings are missing.",introduced:2},
      {name:"Lookup",state:"Healthy",text:"The order API still answers.",introduced:0}
    ];
    const items=places.map((item,i)=>{
      const [mark,pale,deep]=statuses[item.state],a=(-90+i*90)*Math.PI/180,b=a+Math.PI/2;
      const pt=(r,t)=>[cx+r*Math.cos(t),cy+r*Math.sin(t)];
      const outline=`M${pt(ro,a)} A${ro} ${ro} 0 0 1 ${pt(ro,b)} L${pt(ri,b)} A${ri} ${ri} 0 0 0 ${pt(ri,a)} Z`;
      const sector=path(g,outline,{fill:"#e4e5df",stroke:"#f8f1ec","stroke-width":4});
      const [x,y]=pt((ro+ri)/2,(a+b)/2),markLabel=label(g,x,y-5,"",m?23:34,deep,750,"middle");
      label(g,x,y+(m?18:25),item.name,m?14:17,ink,650,"middle");
      const details=group(),rowY=m?306+43*i:94+73*i;
      const left=m?26:533;
      rule(details,left,rowY-15,W-23,rowY-15,"#b7b7ae");
      const status=label(details,left,rowY+9,item.state,m?17:22,deep,720);
      label(details,left+(m?102:158),rowY+9,item.text,m?16:22,ink,520);
      return {item,pale,deep,mark,sector,markLabel,details,status};
    });
    circle(g,cx,cy,ri-3,"#273d46");
    label(g,cx,cy-6,"Return",m?16:22,"#fff",700,"middle");
    label(g,cx,cy+(m?17:24),"service",m?16:22,"#fff",550,"middle");
    label(g,m?22:533,m?486:377,m?"Order O-104 stays visible while its refund waits.":"The order still shows the shipment while its refund waits.",m?17:19,muted,600);
    return p=>items.forEach(({item,pale,deep,mark,sector,markLabel,details,status},i)=>{
      const visible=beat>item.introduced || beat===item.introduced && p>=(i===3?0:.18);
      sector.setAttribute("fill",visible?pale:"#e4e5df");
      markLabel.textContent=visible?mark:"·";markLabel.setAttribute("fill",visible?deep:muted);
      opacity(details,visible?1:.0);opacity(status,visible?1:0);
    });
  }

  // A join, drawn as two source rows meeting on one key and producing a typed
  // link. A separate empty measurement track explains Unknown.
  function evolution(ctx) {
    const {g,W,H,mobile:m,beat,stage}=ctx;
    const {label,path,group,rect,rule,circle,opacity,travel}=pen(ctx);
    const bg="#eff4ee";
    rect(g,0,0,W,H,bg,"none",12);
    label(g,18,m?35:40,"How a storage link appears",m?25:31);
    const origin=m?[18,245]:[39,552], rowW=m?217:464,rowY=m?80:82,rowH=m?99:92;
    const source=["Application config","Azure inventory"];
    const captions=["Receipt upload host","Blob account endpoint"];
    const rows=origin.map((x,i)=>{
      const row=group();
      rect(row,x,rowY,rowW,rowH,"#fff",i?teal:blue,5);
      rect(row,x,rowY,6,rowH,i?teal:blue);
      label(row,x+14,rowY+25,source[i],m?17:22,i?teal:blue,750);
      label(row,x+14,rowY+50,captions[i],m?15:18,ink,580);
      const address=group(row);
      label(address,x+14,rowY+(m?70:78),m?"returnsfiles2":"returnsfiles2.blob.core.windows.net",m?17:19,ink,640);
      if(m)label(address,x+14,rowY+90,".blob.core.windows.net",15,ink,540);
      return row;
    });
    const waiting=label(g,origin[1]+14,rowY+76,m?"Awaiting inventory":"Waiting for an inventory record",m?16:20,muted,620);
    const keyY=m?226:210, keyW=m?144:210, keyH=m?46:50,center=W/2;
    const branches=origin.map((x,i)=>{
      const start=x+rowW/2,end=center+(i?1:-1)*keyW/2;
      return travel(g,`M${start} ${rowY+rowH+1} V${keyY} H${end}`,i?teal:blue,m?3:4);
    });
    const key=group();
    path(key,`M${center-keyW/2-9} ${keyY} l14,-${keyH/2} h${keyW-10} l14,${keyH/2} -14,${keyH/2} h-${keyW-10} Z`,{fill:"#e2d9f1",stroke:purple,"stroke-width":2});
    label(key,center,keyY+6,"Same host?",m?18:22,purple,720,"middle");
    const result=group(), outY=m?298:279;
    const outputWire=travel(result,`M${center},${keyY+keyH/2} V${outY-13}`,teal,3);
    const xl=m?110:325,xr=m?368:785;
    rule(result,xl+4,outY,xr-4,outY,teal,3);
    circle(result,xl,outY,11,teal);circle(result,xr,outY,11,teal);
    rect(result,center-(m?56:70),outY-14,m?112:140,27,bg);
    label(result,center,outY+6,"writes to",m?16:19,teal,650,"middle");
    label(result,xl,outY+34,"Receipt writer",m?17:23,ink,700,"middle");
    label(result,xr,outY+34,"Blob account",m?17:23,ink,700,"middle");
    const lower=m?383:345;
    const health=group();
    rule(health,17,lower-16,W-17,lower-16,"#a8b8a9");
    label(health,23,lower+(m?15:13),"Health reading",m?19:20,ink,700);
    const qx=m?203:347;
    path(health,`M${qx-20},${lower+7} h${m?89:192}`,{stroke:purple,"stroke-width":2,"stroke-dasharray":"3 6"});
    circle(health,qx,lower+7,11,"#e3d8ef",purple,1.5);
    label(health,qx,lower+13,"?",16,purple,750,"middle");
    label(health,m?291:557,lower+14,m?"Unknown":"Unknown until a reading arrives",m?19:22,purple,730);
    const note=label(g,18,H-15,"",m?16:18,muted,600);
    return p=>{
      const joined=beat>=1;
      opacity(rows[1],joined?1:.62);opacity(rows[1].lastChild,joined?1:0);opacity(waiting,joined?0:1);
      opacity(key,joined?1:.56);
      branches[0](beat===0?stage(p,.12,.7):1);branches[1](beat===1?stage(p,.08,.42):beat===2?1:0);
      key.lastChild.textContent=joined && (beat>1||p>.4)?"Host matches":"Same host?";
      opacity(result,joined && (beat>1||p>.44)?1:0);
      outputWire(beat===1?stage(p,.42,.68):1);
      opacity(health,beat===2||beat===1&&p>.67?1:0);
      note.textContent=[
        m?"The app now names a different upload destination.":"The receipt writer’s configured storage endpoint changes.",
        m?"A matching record identifies the new account.":"The matching endpoint connects the writer to the new storage account.",
        m?"Teams can refine the types and health rules.":"People can maintain the types behind this link and refine the rule that judges health."
      ][beat];
    };
  }

  // An investigation notebook. The left page carries the assertion, the right
  // page carries its warrant; the last spread compares with a recorded case.
  function proof(ctx) {
    const {g,W,H,mobile:m,beat,stage}=ctx;
    const {label,path,group,rect,rule,circle,opacity,travel}=pen(ctx);
    const page="#fffbef",print="#2d3a37",warm="#805845";
    rect(g,0,0,W,H,"#e9dfcc","none",12);
    const sx=m?12:21,sy=m?15:20,sw=m?W-24:W-42,sh=m?H-30:H-40;
    rect(g,sx+3,sy+4,sw,sh,"#c5bba7","none",3);
    rect(g,sx,sy,sw,sh,page,"#b6ad98",3);
    const fold=m?243:560;
    if(m)rule(g,sx+12,fold,W-sx-12,fold,"#b4aa91",1.2);
    else {rule(g,fold,sy+6,fold,H-sy-6,"#c4bba4",2);rule(g,fold+4,sy+6,fold+4,H-sy-6,"#ded6c1",1);}
    const left=m?29:49, right=m?29:594, rb=m?fold:0, a=left+(m?0:10);
    const leftTitle=["Change record","Blocked payout","A second case"][beat];
    label(g,left,m?46:59,"Foundry case R-104  ·  illustration",m?15:18,warm,700);
    label(g,left,m?80:107,leftTitle,m?26:34,print,740);
    const items=beat===0?["Before","After"]:beat===1?["Payout","Required payment call"]:["Review of a return","Illustrative evidence"];
    const val=beat===0?["Original receipt storage","Replacement storage account"]:beat===1?["Unhealthy","Timeout"]:["Payment timed out","Processor state unknown"];
    const rowY=m?[132,201]:[179,278];
    const leftWidth=m?W-68:471;
    const leftRows=items.map((name,i)=>{
      const row=group();const y=rowY[i];
      rule(row,a,y-30,a+leftWidth,y-30,"#c5c3b1");
      label(row,a,y-7,name,m?15:18,muted,650);
      label(row,a,y+(m?16:23),val[i],m?20:26,beat===2?ink:beat===0?teal:red,700);
      return row;
    });
    const ry=rb+(m?35:59);
    label(g,right,ry,["Graph history","Evidence trail","Recorded companion  ·  pg-shop"][beat],m?16:19,warm,700);
    const title=beat===0?"The patch":beat===1?"What supports that state?":"The same question in a replay";
    label(g,right,ry+(m?34:49),title,m?24:29,print,740);
    const labels=beat===0?[["−","Remove link","App → original storage"],["+","Add link","App → new storage"]]:
      beat===1?[["1","Rule","Payout needs a payment reply."],["2","Observation","The worker reports a timeout."]]:
      [["16","pg-shop · workload revision","10:11  ·  The meter reads zero."],["18","After two good minutes","The alarm clears."]];
    const rightRows=labels.map(([symbol,key,value],i)=>{
      const lineY=ry+(m?74:112)+i*(m?72:102),item=group();
      circle(item,right+17,lineY+4,m?14:18,beat===0?(i?"#c9e9d6":"#f4d7d1"):"#eadfcb");
      label(item,right+17,lineY+10,symbol,m?15:19,ink,700,"middle");
      label(item,right+44,lineY,key,m?16:19,muted,650);
      label(item,right+44,lineY+(m?22:30),value,m?18:23,ink,620);
      return item;
    });
    return p=>{
      opacity(leftRows[1],.68+.32*stage(p,.08,.25));
      rightRows.forEach((r,i)=>opacity(r,.16+.84*stage(p,.21+i*.27,.35+i*.27)));
    };
  }
  // The same order in three record systems. Each spread keeps the identity used
  // by that system visible: an order link, an event ID, or a settlement receipt.
  function engagement(ctx) {
    const {g,W,H,mobile:m,beat,stage}=ctx;
    const {label,path,group,rect,rule,circle,opacity,travel}=pen(ctx);
    const paper="#f5efe6",wine="#763955",slate="#384b60",gold="#875d17";
    rect(g,0,0,W,H,paper,"none",12);
    const spread=["Customer service","Marketing events","Sales analytics"][beat];
    label(g,18,m?32:39,spread,m?26:32,ink,720);
    label(g,W-18,m?30:37,"Order O-104",m?16:20,muted,650,"end");
    rule(g,18,m?45:55,W-18,m?45:55,"#bbaea3");
    if(beat===0) {
      const fileX=m?25:557,fileY=m?214:100,fileW=m?W-50:W-fileX-27,fileH=m?219:221;
      const x=m?23:37,top=m?79:107,rw=m?205:326,rh=m?73:80;
      const sourceCards=[["Order record","O-104","Sale"],["Carrier scan","D-7","Parcel delayed"]];
      const inputs=sourceCards.map(([kind,id,detail],i)=>{
        const px=x+(m?i*(rw+18):0),py=top+(m?0:i*(rh+44));
        const a=group();
        rect(a,px,py,rw,rh,"#fffcf9",i?wine:slate,4);
        rect(a,px+1,py+1,6,rh-2,i?wine:slate);
        label(a,px+15,py+24,kind,m?16:18,muted,650);
        label(a,px+15,py+54,id,m?23:26,i?wine:slate,750);
        label(a,px+rw-12,py+54,detail,m?15:19,ink,550,"end");
        const ex=m?px+rw/2:fileX-9,ey=m?fileY+7:py+rh/2;
        const d=m?`M${ex},${py+rh} V${ey}`:`M${px+rw},${ey} H${ex}`;
        return travel(g,d,i?wine:slate,3);
      });
      label(g,m?W/2:454,m?184:223,"Tracking number",m?16:17,wine,660,"middle");
      const file=group();
      path(file,`M${fileX},${fileY} h${fileW*.31} l12,14 h${fileW*.69-12} v${fileH-14} h-${fileW} Z`,{fill:"#eed9df",stroke:wine,"stroke-width":2});
      label(file,fileX+18,fileY+36,"CRM  /  one support case",m?20:24,wine,720);
      const spineX=fileX+35,base=fileY+81;
      const trail=travel(file,`M${spineX},${base} v78`,wine,3);
      [[base,"Delivery update","Scan D-7 links the delay to O-104."],
       [base+78,"Return R-104","Support adds the two returned items."]].forEach(([y,title,desc])=>{
        circle(file,spineX,y,7,paper,wine,3);
        label(file,spineX+21,y+5,title,m?18:22,ink,700);
        label(file,spineX+21,y+31,desc,m?16:18,ink,520);
      });
      label(g,19,H-(m?34:30),m?"The same case keeps both customer updates.":"One case keeps the customer’s history. The linked order still records the sale.",m?17:22,muted,600);
      return p=>{inputs[0](stage(p,.03,.3));inputs[1](stage(p,.22,.53));trail(stage(p,.49,.79));};
    }
    if(beat===1) {
      const cx=m?243:487, cy=m?206:192;
      const startX=m?24:35, top=m?73:97, ww=m?195:228, hh=m?55:60;
      const gateW=m?193:224,gateH=m?85:103;
      const packets=[];
      for(let i=0;i<2;i++) {
        const sx=startX+(m?i*(ww+22):0), sy=top+(m?0:i*122);
        const card=group();rect(card,sx,sy,ww,hh,"#fffaf4","#9c8c80",6);
        label(card,sx+12,sy+(m?22:18),i?"Retry":"First delivery",m?15:17,muted,570);
        label(card,sx+12,sy+(m?45:51),"E-81",m?19:24,wine,750);
        const entryY=cy+(i?22:-22);
        const d=m?`M${sx+ww/2},${sy+hh} V${cy-gateH/2-12} H${cx+(i?40:-40)} v12`:
          `M${sx+ww},${sy+hh/2} H${cx-gateW/2-54} V${entryY} H${cx-gateW/2}`;
        packets.push(travel(g,d,i?gold:wine,3));
      }
      rect(g,cx-gateW/2,cy-gateH/2,gateW,gateH,"#e9d6e2",wine,8);
      label(g,cx,cy-(m?7:9),"Azure Function",m?19:24,wine,700,"middle");
      label(g,cx,cy+(m?17:18),"Checks Cosmos DB",m?15:18,ink,600,"middle");
      const check=label(g,cx,cy+(m?35:39),"",m?14:16,wine,680,"middle");
      const busY=m?313:cy;
      const busX=m?79:709,terminalX=m?364:973;
      const d=m?`M${cx},${cy+gateH/2} V${busY-26} H${busX} V${busY}`:
        `M${cx+gateW/2},${cy} H${busX}`;
      const accepted=travel(g,d,teal,3.2);
      const forwarded=travel(g,`M${busX},${busY} H${terminalX}`,teal,3.2);
      circle(g,busX,busY,9,teal);circle(g,terminalX,busY,9,teal);
      const amount=label(g,busX,busY+(m?34:43),"",m?19:23,teal,720,"middle");
      label(g,busX,busY+(m?58:69),"Service Bus",m?16:20,ink,560,"middle");
      label(g,terminalX,busY+(m?34:43),"Logic Apps",m?19:23,teal,720,"middle");
      label(g,terminalX,busY+(m?58:69),"Journeys",m?16:20,ink,560,"middle");
      const stamp=group(), a=m?30:649, y=m?411:306;
      rect(stamp,a,y,m?W-60:W-a-31,m?54:53,"#f1dbd6",red,6);
      label(stamp,a+14,y+(m?33:34),m?"Opted out  ·  promotion suppressed":"Consent check  ·  Opted out  ·  Promotion suppressed",m?19:17,red,720);
      return p=>{
        packets[0](stage(p,0,.26));packets[1](stage(p,.5,.7));accepted(stage(p,.25,.47));forwarded(stage(p,.47,.61));
        check.textContent=p<.22?"Event ID":p<.68?"Store E-81":"E-81 already stored";
        amount.textContent=p<.35?"0 messages":"1 message";
        opacity(stamp,.10+.90*stage(p,.75,.91));
      };
    }
    const left=m?65:119,right=W-(m?28:53),top=m?124:119,btm=m?269:245;
    const x=[left+.05*(right-left),left+.38*(right-left),left+.80*(right-left)];
    const yp=money=>btm-(btm-top)*money/80;
    label(g,18,m?77:87,"Merchandise after settled refunds",m?21:25,ink,700);
    label(g,m?16:41,H-15,m?"Amounts exclude shipping and tax.":"The reporting cut-off decides which settlement receipts count. Amounts exclude shipping and tax.",m?16:20,muted,600);
    for(const v of [0,50,80]) {
      const y=yp(v);
      rule(g,left,y,right,y,v===0?"#9d9690":"#d1c8bc");
      label(g,left-8,y+5,"$"+v,m?15:19,muted,600,"end");
    }
    const chartPath=`M${x[0]},${yp(80)} H${x[2]} V${yp(50)} H${right}`;
    const plotted=travel(g,chartPath,teal,m?4:5);
    const cutoff=rule(g,x[1],top-12,x[1],btm+4,wine,2);
    cutoff.setAttribute("stroke-dasharray","4 5");
    const labelY=btm+(m?24:24);
    ["Order O-104","R-104 requested","P-30 settled"].forEach((s,i)=>{
      label(g,x[i],labelY,s,m?14:18,ink,650,"middle");
    });
    [[x[0],80],[x[1],80],[x[2],50]].forEach(([cx,v])=>circle(g,cx,yp(v),m?6:8,paper,teal,3));
    const base=m?333:315,sep=m?69:0;
    const cuts=[["Early report","$80 − $0 = $80"],["Later report","$80 − $30 = $50"]].map(([key,due],i)=>{
      const card=group(),a=m?20:65+i*493, y=base+i*sep;
      if(m)rule(card,a,y-17,W-22,y-17,"#b7aa9d");
      label(card,a,y+11,key,m?17:20,wine,680);
      label(card,m?W-21:a+137,y+12,due,m?23:27,ink,720,m?"end":"start");
      label(card,a,y+(m?36:41),i?"Receipt P-30 settles $30 for O-104.":"Return requested for $30. Settlement still $0.",m?15:17,muted,600);
      return card;
    });
    return p=>{
      const v=stage(p,.07,.87),pos=x[1]+(x[2]-x[1])*stage(p,.42,.74);
      plotted(v);cutoff.setAttribute("x1",pos);cutoff.setAttribute("x2",pos);
      opacity(cuts[0],1);opacity(cuts[1],.14+.86*stage(p,.57,.80));
    };
  }
  return {jobs,failure,evolution,proof,engagement};
})();
