// The same case appears at three scales: an estate, one conversation and one computation.
const FoundryLiveScenes = (() => {
  function ink({el,txt}) {
    const group = (g,attrs={}) => el(g,"g",attrs);
    const pen = (g,d,attrs={}) => el(g,"path",{d,fill:"none","stroke-linejoin":"round","stroke-linecap":"round",...attrs});
    const label = (g,x,y,text,attrs={}) => txt(g,x,y,text,{"font-size":18,"font-weight":550,...attrs});
    const mid = (g,x,y,text,attrs={}) => label(g,x,y,text,{"text-anchor":"middle",...attrs});
    function arrow(g,d,color,width=3) {
      const stroke=pen(g,d,{stroke:color,"stroke-width":width});
      const length=stroke.getTotalLength();
      stroke.setAttribute("stroke-dasharray",length+" "+length);
      const tip=stroke.getPointAtLength(length), tail=stroke.getPointAtLength(Math.max(0,length-1));
      const angle=Math.atan2(tip.y-tail.y,tip.x-tail.x)*180/Math.PI;
      const head=pen(g,"M-8 -4.5 L0 0 L-8 4.5",{stroke:color,"stroke-width":width,
        transform:`translate(${tip.x} ${tip.y}) rotate(${angle})`});
      return p=>{
        stroke.setAttribute("stroke-dashoffset",length*(1-p));
        head.setAttribute("opacity",p>.985?1:0);
      };
    }
    return {group,pen,label,mid,arrow};
  }

  function estate(ctx) {
    const {g,el,box,dot,stage,mobile:m,W,H,beat}=ctx;
    const {group,pen,label,mid}=ink(ctx);
    const pale="#eaf4f3",muted="#c0d2d9",field="#102a38",route="#ffda80",refund="#72e4ca";
    box(g,0,0,W,H,{fill:field,rx:15});
    const regions={
      customer:["Client apps","#83d5f1",0,0], commerce:["Store & checkout","#9bdeb4",1,0],
      fulfillment:["Logistics APIs","#f8c174",2,0], agents:["Microsoft Foundry","#bfaefe",0,1],
      systems:["Order & payment","#f0a588",1,1], engagement:["CRM & marketing","#edaad6",2,1],
      tools:["Agent tools","#72dfc5",0,2], jobs:["Background jobs","#f1d779",1,2],
      data:["Stored records","#9dbce9",2,2]
    };
    const short={
      "returns-agent":"Returns agent","returns-app":"Returns app","web-client":"Web client",chat:"Chat API",
      project:"Foundry project","lookup-order":"Order lookup",sandbox:"Sandbox",
      orders:"Order system",processor:"Payment API","refund-job":"Refund job",reconciler:"Reconciliation",
      "case-store":"Case store","receipt-blob":"Receipt files",checkout:"Checkout service",
      storefront:"Storefront","warehouse-api":"Warehouse API","carrier-api":"Carrier API",crm:"CRM",analytics:"Analytics"
    };
    const showLabels= new Set(Object.keys(short));
    const margin=m?9:23,gap=m?9:22,cw=(W-2*margin-2*gap)/3,ch=m?97:82;
    const firstY=m?83:62,stepY=m?112:96;
    const sceneBoxHeight=stepY*2+ch+3;
    const viewport=el(g,"svg",{x:0,y:firstY-1,width:W,height:sceneBoxHeight,
      viewBox:`0 ${firstY-1} ${W} ${sceneBoxHeight}`,overflow:"hidden"});
    const camera=group(viewport), panels=group(camera), links=group(camera), marks=group(camera);
    const coords=new Map(), marksById=new Map();
    label(g,margin,m?30:34,"The shop’s master graph",{fill:pale,"font-size":m?24:28,"font-weight":740});
    label(g,m?margin:W-margin,m?57:33,`${FoundryCase.nodes.length} entities  /  ${FoundryCase.edges.length} typed links`,{
      fill:muted,"font-size":m?17:19,"text-anchor":m?"start":"end"});
    for(const {id} of FoundryCase.groups) {
      const [title,color,col,row]=regions[id], members=FoundryCase.nodes.filter(n=>n.group===id);
      const x=margin+col*(cw+gap), y=firstY+row*stepY;
      box(panels,x,y,cw,ch,{rx:10,fill:color,"fill-opacity":.065,stroke:color,"stroke-opacity":.58});
      label(marks,x+(m?7:11),y+(m?18:19),title,{fill:color,"font-size":m?13.1:16.5,"font-weight":720,
        "paint-order":"stroke",stroke:field,"stroke-width":4});
      // Anchor each entity once. Adjoining groups and repeated paths can share
      // it, while the group name stays readable even on the phone map.
      const lowerCount=Math.floor(members.length/2),upperCount=members.length-lowerCount;
      members.forEach((node,i)=>{
        const lower=i>=upperCount, count=lower?lowerCount:upperCount, column=lower?i-upperCount:i;
        const cx=x+cw*(column+.5)/count, cy=y+(m?39:32)+(lower?(m?32:29):0);
        coords.set(node.id,{x:cx,y:cy,color,id});
        const point=group(marks,{"data-foundry-node":node.id});
        el(point,"title",{},node.label);
        const circle=dot(point,cx,cy,m?4:4.6,{fill:color,stroke:field,"stroke-width":1.2});
        const word=showLabels.has(node.id)&&(!m||count<=2&&short[node.id].length<=14)?
          mid(point,cx,cy+(m?13:14),short[node.id],{fill:pale,"font-size":m?10.4:12.5,"font-weight":600,
            "paint-order":"stroke",stroke:field,"stroke-width":3.8}):null;
        marksById.set(node.id,{circle,word,color});
      });
    }
    const sale=["web-client","storefront","checkout","orders","warehouse-api","carrier-api","tracking-service"];
    const delivery=new Set([...sale,"inventory"]);
    const recovery=new Set(["web-client","chat","returns-app","returns-api","case-store","inspection-log","approval","refund-job","refund-id","processor","processor-receipt"]);
    const nodeLabels=new Map(FoundryCase.nodes.map(n=>[n.id,n.label]));
    const strokes=FoundryCase.edges.map(([aId,bId,kind])=>{
      const a=coords.get(aId),b=coords.get(bId);
      const bend=a.id===b.id?0:Math.sign(b.x-a.x||1)*(m?8:15);
      const d=`M${a.x} ${a.y} Q${(a.x+b.x)/2+bend} ${(a.y+b.y)/2-bend} ${b.x} ${b.y}`;
      const e=pen(links,d,{"data-foundry-edge":kind});
      el(e,"title",{},[nodeLabels.get(aId),kind,nodeLabels.get(bId)].join(" "));
      return {e,aId,bId};
    });
    const railY=m?437:367;
    const caption=label(g,margin,railY,"",{fill:route,"font-size":m?18:19,"font-weight":690});
    const detail=label(g,margin,railY+(m?28:24),"",{fill:muted,"font-size":m?15:16.5});
    return p=>{
      // The film follows the order with the camera. The guide keeps all nine groups in view.
      const travel=stage(p,.10,.72);
      const zoom=beat===0 && !ctx.inGuide?1+.31*(1-stage(p,.70,.95)):1;
      const anchorX=W*(.12+.70*travel),anchorY=firstY+ch*(.60+.44*Math.sin(travel*Math.PI));
      camera.setAttribute("transform",`translate(${(1-zoom)*anchorX} ${(1-zoom)*anchorY}) scale(${zoom})`);
      const returns=beat===2&&p>=.52, active=returns?recovery:delivery,color=returns?refund:route;
      const reveal=1+Math.floor(stage(p,.02,.60)*(sale.length-1));
      const prefix=new Set(sale.slice(0,reveal+1));
      const use=beat===0?prefix:active;
      strokes.forEach(({e,aId,bId})=>{
        const focus=beat!==1&&use.has(aId)&&use.has(bId);
        e.setAttribute("stroke",focus?color:"#abc3cf");
        e.setAttribute("stroke-width",focus?(m?2.1:2.8):(m?.95:1.4));
        e.setAttribute("stroke-opacity",focus?.96:beat===1?.59:.19);
      });
      marksById.forEach(({circle,word,color:base},id)=>{
        const highlight=beat!==1&&use.has(id);
        circle.setAttribute("fill",highlight?color:base);
        circle.setAttribute("r",highlight?(m?5.7:6.5):m?4:4.6);
        if(word)word.setAttribute("opacity",beat===1||highlight?1:.73);
      });
      caption.setAttribute("fill",color);
      caption.textContent=beat===1?"The whole shop":returns?"Return R-104  ·  refund view":"Order O-104  ·  delivery view";
      detail.textContent=beat===1?"Each link names an action, such as reads, calls or writes.":returns?
        "Inspection log → approval → refund job → payment API":"Storefront → checkout → order system → warehouse API → carrier API";
      if(m&&beat!==1&&!returns)detail.textContent="Checkout → warehouse API → carrier API";
    };
  }

  function live(ctx) {
    const {g,box,dot,line,stage,mobile:m,W,H,beat}=ctx;
    const {group,label,mid,arrow}=ink(ctx);
    const paper="#f5f2e9",inkColor="#18364a",muted="#4b646d",blue="#17678b",purple="#684490",teal="#14674f";
    box(g,0,0,W,H,{fill:paper,rx:15});
    label(g,m?16:27,m?32:37,"A return-status conversation",{fill:inkColor,"font-size":m?24:28,"font-weight":720});
    label(g,m?16:29,m?56:62,"Order O-104  /  Return R-104",{fill:muted,"font-size":m?15:17});
    const lanes=m?[55,184,312,425]:[137,405,687,966];
    const header=m?90:92;
    const lanesData=[["Chat API","Web client",blue],["Returns agent","Foundry",purple],["App handler","Merchant app",teal],["Order system","External",inkColor]];
    lanes.forEach((x,i)=>{
      const [name,scope,color]=lanesData[i];
      mid(g,x,header,name,{fill:color,"font-size":m?15:21,"font-weight":710});
      mid(g,x,header+(m?18:22),scope,{fill:muted,"font-size":m?12.7:15});
      line(g,x,header+28,x,m?451:377,{stroke:"#a1b5b4","stroke-width":1.6,"stroke-dasharray":"4 5"});
    });
    const first=m?157:148, step=m?56:42;
    const rows=[
      [0,1,"Where is my refund?",blue,0],[1,2,"lookup_order",purple,1],
      [2,3,"Read O-104",teal,1],[3,2,"Status: received",inkColor,2],
      [2,1,"Tool result",teal,2],[1,0,"Status: received.",purple,2]
    ];
    const tracks=rows.map(([from,to,message,color,part],i)=>{
      const y=first+i*step, a=lanes[from],b=lanes[to],dir=Math.sign(b-a);
      const track=group(g), start=a+dir*5,finish=b-dir*7;
      dot(track,a,y,4,{fill:color});
      const advance=arrow(track,`M${start} ${y} H${finish}`,color,2.8);
      mid(track,(a+b)/2,y-9,message,{fill:color,"font-size":m?12.9:17,"font-weight":660,
        "paint-order":"stroke",stroke:paper,"stroke-width":4});
      return {track,advance,part};
    });
    if(m)label(g,16,488,"Time runs down the page.",{fill:muted,"font-size":15});
    else {
      label(g,W-28,37,"Time runs down the page.",{"text-anchor":"end",fill:muted,"font-size":17});
      mid(g,lanes[3],366,"Order record",{fill:muted,"font-size":15,"paint-order":"stroke",stroke:paper,"stroke-width":5});
    }
    return p=>{
      let active=0;
      for(const {track,advance,part} of tracks) {
        if(part>beat) {track.setAttribute("opacity",0);advance(0);continue;}
        if(part<beat) {track.setAttribute("opacity",.62);advance(1);continue;}
        const n=beat===0?1:beat===1?2:3;
        const start=.04+active*.63/n, end=start+.29;
        track.setAttribute("opacity",.18+.82*stage(p,start,start+.12));
        advance(stage(p,start,end));
        active++;
      }
    };
  }

  function sandbox(ctx) {
    const {g,el,box,dot,line,stage,mobile:m,W,H,beat}=ctx;
    const {group,pen,label,mid,arrow}=ink(ctx);
    const paper="#fff7e7",inkColor="#293542",soft="#555b60",purple="#664986",green="#125f51",gold="#93551f";
    box(g,0,0,W,H,{fill:paper,rx:15});
    label(g,m?15:25,m?30:38,"Receipt calculation",{fill:inkColor,"font-size":m?23:29,"font-weight":735});
    label(g,W-(m?15:25),m?29:36,"Return R-104",{fill:soft,"font-size":m?15:18,"text-anchor":"end"});
    const paperX=m?14:28, paperY=m?83:94, paperW=m?161:214, paperH=m?164:210;
    const receipt=group(g);
    box(receipt,paperX,paperY,paperW,paperH,{rx:7,fill:"#fffefa",stroke:"#b89a74","stroke-width":1.6});
    label(receipt,paperX+13,paperY+28,"Receipt file",{fill:inkColor,"font-size":m?15:20,"font-weight":700});
    label(receipt,paperX+13,paperY+51,"Eligible lines",{fill:soft,"font-size":m?13:16});
    const moneyLine=(id,y,amount)=>{
      label(receipt,paperX+13,y,id,{fill:soft,"font-size":m?13:16});
      label(receipt,paperX+paperW-13,y,amount,{"text-anchor":"end",fill:inkColor,"font-size":m?16:20,"font-weight":690});
    };
    line(receipt,paperX+12,paperY+64,paperX+paperW-12,paperY+64,{stroke:"#b39f88","stroke-width":1.5});
    moneyLine("Line 1",paperY+(m?88:101),"$18");
    moneyLine("Line 2",paperY+(m?116:139),"$12");
    label(receipt,paperX+13,paperY+paperH-15,"Chat attachment",{fill:gold,"font-size":m?12:15});

    const sx=m?206:306,sy=m?57:67,sw=m?260:363,sh=m?214:242;
    const session=group(g);
    box(session,sx,sy,sw,sh,{rx:16,fill:"#efe8f5",stroke:purple,"stroke-width":2.8,"stroke-dasharray":"8 5"});
    label(session,sx+16,sy+29,"Private sandbox",{fill:purple,"font-size":m?19:24,"font-weight":715});
    label(session,sx+16,sy+51,"Code Interpreter · Python",{fill:soft,"font-size":m?13:16});
    const ix=sx+16,iy=sy+64,iw=sw-32;
    const file=group(session);
    box(file,ix,iy,iw,m?71:84,{rx:6,fill:"#fefffc",stroke:"#b7a7c3","stroke-width":1.3});
    label(file,ix+10,iy+20,"Temporary files",{fill:purple,"font-size":m?13:16});
    label(file,ix+10,iy+(m?47:54),"receipt    calculation",{fill:inkColor,"font-family":"Menlo, monospace","font-size":m?13:17});
    const total=mid(session,sx+sw/2,sy+sh-29,"$18 + $12 = $30",{fill:purple,"font-size":m?23:29,"font-weight":740});
    const input=arrow(g,`M${paperX+paperW+3} ${sy+sh*.49} H${sx-5}`,gold,m?2.5:3.5);

    // Code Interpreter returns a file to the app. Only the app's file writer
    // crosses the storage permission boundary, and the case keeps its reference.
    const archive=group(g), ax=m?15:717,ay=m?302:71,aw=m?451:374,ah=m?172:238;
    box(archive,ax,ay,aw,ah,{rx:14,fill:"#edf3e6",stroke:"#9bb8a2","stroke-width":1.5});
    label(archive,ax+14,ay+27,"Saved evidence",{fill:green,"font-size":m?19:23,"font-weight":720});
    label(archive,ax+14,ay+49,"Merchant app · storage permission",{fill:soft,"font-size":m?13:16});
    const writerX=ax+(m?67:62), blobX=ax+(m?249:207), cy=ay+(m?99:114);
    box(archive,writerX-42,cy-20,84,40,{rx:6,fill:"#fffcf3",stroke:green,"stroke-width":1.8});
    mid(archive,writerX,cy+5,"File writer",{fill:green,"font-size":m?13:15,"font-weight":700});
    const output=arrow(g,m?`M${sx+sw*.66} ${sy+sh+2} v14 H${writerX} V${ay-2}`:
      `M${sx+sw+2} ${sy+sh*.49} H${ax-4}`,purple,m?2.3:3.2);
    const save=arrow(archive,`M${writerX+45} ${cy} H${blobX-45}`,green,2.5);
    const blob=group(archive);
    pen(blob,`M${blobX-40} ${cy-13} V${cy+14} C${blobX-40} ${cy+30} ${blobX+40} ${cy+30} ${blobX+40} ${cy+14} V${cy-13}Z`,
      {fill:"#cef1d8",stroke:green,"stroke-width":2});
    el(blob,"ellipse",{cx:blobX,cy:cy-13,rx:40,ry:9,fill:"#edfbed",stroke:green,"stroke-width":2});
    mid(blob,blobX,cy+12,"Blob",{fill:green,"font-size":16,"font-weight":730});
    const caseX=m?ax+aw-48:blobX, caseY=m?cy+3:ay+ah-40;
    const reference=arrow(archive,m?`M${caseX-35} ${caseY} H${blobX+47}`:
      `M${caseX} ${caseY-12} V${cy+31}`,green,2);
    mid(archive,caseX,caseY+(m?4:6),"R-104",{fill:green,"font-size":m?15:18,"font-weight":710});
    mid(archive,caseX,caseY+26,"case link",{fill:soft,"font-size":m?12.5:15});
    const note=label(g,m?16:28,H-(m?10:22),"",{fill:soft,"font-size":m?14:18});
    return p=>{
      input(beat===0?stage(p,.23,.68):1);
      file.setAttribute("opacity",beat===0?.42:beat===2?1-.52*stage(p,.60,.93):1);
      total.setAttribute("opacity",beat===0?0:beat===1?stage(p,.20,.54):1);
      archive.setAttribute("opacity",beat===2?1:.52);
      blob.setAttribute("opacity",beat===2?.65+.35*stage(p,.42,.69):.7);
      output(beat===2?stage(p,.06,.38):0);
      save(beat===2?stage(p,.33,.63):0);
      reference(beat===2?stage(p,.60,.85):0);
      note.textContent=beat===0?"The app sends the receipt file.":beat===1?"Working files live inside this session.":"A later review can open the saved calculation.";
    };
  }
  function logistics(ctx) {
    const {g,box,line,stage,W,H,mobile:m,beat}=ctx;
    const {group,label,arrow}=ink(ctx);
    const page="#edf2e8",inkColor="#193e38",muted="#465e54";
    const green="#175f43",red="#963e35",blue="#225d7d";
    box(g,0,0,W,H,{fill:page,rx:15});
    label(g,m?14:25,m?30:37,"Return event log",{fill:inkColor,"font-size":m?24:29,"font-weight":740});
    label(g,W-(m?14:25),m?29:35,"O-104  /  R-104",{"text-anchor":"end",fill:muted,"font-size":m?16:20});

    const x=m?12:24,y=m?51:73,w=m?W-24:610,h=m?203:246;
    box(g,x,y,w,h,{rx:9,fill:"#fffdf2",stroke:"#97ad9c","stroke-width":1.5});
    label(g,x+15,y+26,"Warehouse API events",{fill:inkColor,"font-size":m?17:22,"font-weight":710});
    const columns=m?[15,67,158,247]:[18,88,202,340];
    const headerY=y+(m?52:61);
    ["Seq.","Record","Order line","Recorded value"].forEach((title,i)=>{
      label(g,x+columns[i],headerY,title,{fill:muted,"font-size":m?13:16,"font-weight":650});
    });
    const entries=[
      ["01","Receipt","Line 1","Received"],
      ["02","Receipt","Line 2","Received"],
      ["03","Inspection","Line 1","Restock · $18 credit"],
      ["04","Inspection","Line 2","Scrap · $12 credit"]
    ];
    const rowHeight=m?31:38;
    const rows=entries.map((cells,i)=>{
      const row=group(g), ry=headerY+12+i*rowHeight;
      box(row,x+7,ry,w-14,rowHeight-3,{rx:4,fill:[ "#e7f1f5","#e7f1f5","#dbefdf","#f4dfd4" ][i]});
      cells.forEach((cell,j)=>label(row,x+columns[j],ry+rowHeight*.66,cell,{
        fill:j===3?[blue,blue,green,red][i]:inkColor,"font-size":m?13.5:17,"font-weight":j===3?690:560
      }));
      return row;
    });

    const panelX=m?12:752,panelY=m?294:62,panelW=m?(W-37)/2:W-panelX-24;
    const secondX=m?W-panelW-12:panelX, secondY=m?panelY:218;
    const connectionY=panelY-17;
    const inventoryFlow=arrow(g,m?
      `M${W/2} ${y+h+2} V${connectionY} H${panelX+panelW/2} V${panelY-3}`:
      `M${x+w+3} ${y+h*.58} H${panelX-65} V${panelY+56} H${panelX-4}`,green,3);
    const approvalFlow=arrow(g,m?
      `M${W/2} ${y+h+2} V${connectionY} H${secondX+panelW/2} V${secondY-3}`:
      `M${x+w+3} ${y+h*.77} H${panelX-88} V${secondY+54} H${panelX-4}`,blue,3);
    function resultPanel(px,py,title,color,names) {
      const ph=m?137:125;
      box(g,px,py,panelW,ph,{rx:8,fill:"#f8fbf5",stroke:color,"stroke-width":1.7});
      label(g,px+13,py+27,title,{fill:color,"font-size":m?17:21,"font-weight":730});
      return names.map((name,i)=>{
        const rowY=py+(m?54:51)+i*(m?29:26);
        line(g,px+12,rowY-15,px+panelW-12,rowY-15,{stroke:"#d6dfd1","stroke-width":1});
        label(g,px+13,rowY+3,name,{fill:muted,"font-size":m?13.5:16});
        return label(g,px+panelW-13,rowY+3,"",{"text-anchor":"end",fill:inkColor,"font-size":m?16:19,"font-weight":690});
      });
    }
    const stock=resultPanel(panelX,panelY,"Stock ledger",green,["Received lines","Available","Scrap"]);
    const credit=resultPanel(secondX,secondY,"Refund ledger",blue,["Approved","Settled"]);
    const note=label(g,m?13:25,H-(m?19:17),"",{fill:muted,"font-size":m?15:18});
    return p=>{
      const received=beat===0?(p<.2?0:p<.52?1:2):2;
      const inspected=beat===0?0:beat===2?2:p<.24?0:p<.62?1:2;
      const count=received+inspected;
      rows.forEach((row,i)=>row.setAttribute("opacity",i<count?1:0));
      stock[0].textContent=String(received);
      stock[1].textContent=inspected?"+1":"—";
      stock[2].textContent=inspected===2?"+1":"—";
      credit[0].textContent=["—","$18","$30"][inspected];
      credit[1].textContent=beat===2?"Pending":"—";
      inventoryFlow(beat===0?stage(p,.27,.72):1);
      approvalFlow(beat===0?0:beat===1?stage(p,.27,.72):1);
      note.textContent=[
        "Both receipt events refer to order O-104.",
        "Each inspection sets a stock status and a credit amount.",
        "The refund stays pending until a payment receipt arrives."
      ][beat];
    };
  }

  return {estate,live,sandbox,logistics};
})();
