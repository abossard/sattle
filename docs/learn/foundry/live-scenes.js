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
      customer:["Customer","#83d5f1",0,0], commerce:["Store & checkout","#9bdeb4",1,0],
      fulfillment:["Logistics","#f8c174",2,0], agents:["Microsoft Foundry","#bfaefe",0,1],
      systems:["Order & payment","#f0a588",1,1], engagement:["CRM & marketing","#edaad6",2,1],
      tools:["Agent tools","#72dfc5",0,2], jobs:["Background jobs","#f1d779",1,2],
      data:["Stored records","#9dbce9",2,2]
    };
    const short={
      "returns-agent":"Returns agent","returns-app":"Returns app",shopper:"Customer",chat:"Chat",
      project:"Foundry project","lookup-order":"Order lookup",sandbox:"Sandbox",
      orders:"Order system",processor:"Payments","refund-worker":"Refund worker",reconciler:"Reconciliation",
      "case-store":"Case store","receipt-blob":"Receipt files",checkout:"Commerce Scale Unit",
      storefront:"Storefront",warehouse:"Warehouse",carrier:"Carrier",crm:"CRM",analytics:"Analytics"
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
    const sale=["shopper","storefront","checkout","orders","warehouse","carrier","tracking"];
    const delivery=new Set([...sale,"inventory"]);
    const recovery=new Set(["shopper","chat","returns-app","returns-desk","case-store","inspection","approval","refund-worker","refund-id","processor","processor-receipt"]);
    const strokes=FoundryCase.edges.map(([aId,bId,kind])=>{
      const a=coords.get(aId),b=coords.get(bId);
      const bend=a.id===b.id?0:Math.sign(b.x-a.x||1)*(m?8:15);
      const d=`M${a.x} ${a.y} Q${(a.x+b.x)/2+bend} ${(a.y+b.y)/2-bend} ${b.x} ${b.y}`;
      const e=pen(links,d,{"data-foundry-edge":kind});
      el(e,"title",{},aId+" → "+bId+" · "+kind);
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
      detail.textContent=beat===1?"A line can mean places, ships, reads or pays.":returns?
        "Case → inspection → approval → payment":"Storefront → checkout → order system → warehouse → carrier";
      if(m&&beat!==1&&!returns)detail.textContent="Checkout → order system → warehouse → carrier";
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
    const lanesData=[["Customer","Chat",blue],["Returns agent","Foundry",purple],["App handler","Merchant app",teal],["Order system","External",inkColor]];
    lanes.forEach((x,i)=>{
      const [name,scope,color]=lanesData[i];
      mid(g,x,header,name,{fill:color,"font-size":m?15:21,"font-weight":710});
      mid(g,x,header+(m?18:22),scope,{fill:muted,"font-size":m?12.7:15});
      line(g,x,header+28,x,m?451:377,{stroke:"#a1b5b4","stroke-width":1.6,"stroke-dasharray":"4 5"});
    });
    const first=m?157:148, step=m?56:42;
    const rows=[
      [0,1,"Where is my refund?",blue,0],[1,2,"lookup_order",purple,1],
      [2,3,"Read O-104",teal,1],[3,2,"Return received",inkColor,2],
      [2,1,"Tool result",teal,2],[1,0,"Your return arrived.",purple,2]
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
    let outline=`M${paperX} ${paperY} h${paperW} v${paperH}`;
    for(let k=0;k<8;k++)outline+=` l-${paperW/16} -5 l-${paperW/16} 5`;
    pen(receipt,outline+" Z",{fill:"#fffefa",stroke:"#b89a74","stroke-width":1.6});
    label(receipt,paperX+13,paperY+28,"Itemized receipt",{fill:inkColor,"font-size":m?15:20,"font-weight":700});
    label(receipt,paperX+13,paperY+51,"Eligible items",{fill:soft,"font-size":m?13:16});
    const moneyLine=(id,y,amount)=>{
      label(receipt,paperX+13,y,id,{fill:soft,"font-size":m?13:16});
      label(receipt,paperX+paperW-13,y,amount,{"text-anchor":"end",fill:inkColor,"font-size":m?16:20,"font-weight":690});
    };
    line(receipt,paperX+12,paperY+64,paperX+paperW-12,paperY+64,{stroke:"#b39f88","stroke-width":1.5});
    moneyLine("Item 1",paperY+(m?88:101),"$18");
    moneyLine("Item 2",paperY+(m?116:139),"$12");
    label(receipt,paperX+13,paperY+paperH-15,"Customer attachment",{fill:gold,"font-size":m?12:15});

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
      note.textContent=beat===0?"The app supplies the receipt.":beat===1?"Working files live inside this session.":"A later review can open the saved calculation.";
    };
  }
  function logistics(ctx) {
    const {g,el,box,dot,line,stage,W,H,mobile:m,beat}=ctx;
    const {group,pen,label,mid,arrow}=ink(ctx);
    const inkColor="#193e38",muted="#465e54",floor="#f6f4df",page="#edf2e8";
    const green="#175f43",red="#963e35",gold="#88601e",blue="#225d7d";
    box(g,0,0,W,H,{fill:page,rx:15});
    label(g,m?15:25,m?31:37,"Warehouse receiving",{fill:inkColor,"font-size":m?22:28,"font-weight":740});
    label(g,m?15:W-24,m?56:35,"Order O-104  →  Return R-104",{
      fill:muted,"font-size":m?15:19,"text-anchor":m?"start":"end"});
    const fx=m?10:23,fy=m?71:68,fw=m?460:704,fh=m?243:268;
    const plan=group(g);
    box(plan,fx,fy,fw,fh,{fill:floor,rx:5});
    // Warehouse walls have an opening for returns. Three small stripes are a
    // dock marking. The bench, shelf and bin use their floor positions throughout.
    pen(plan,`M${fx} ${fy+51} V${fy+1} H${fx+fw-1} V${fy+fh-1} H${fx} V${fy+fh-43}`,
      {stroke:"#98a797","stroke-width":m?2:3});
    for(let i=0;i<4;i++) {
      const y=fy+fh*.37+i*(m?15:18);
      pen(plan,`M${fx+1} ${y} l${m?12:16} -6`,{stroke:"#c5a771","stroke-width":m?3:4});
    }
    const dockX=fx+fw*.15,benchX=fx+fw*.47,stockX=fx+fw*.81;
    const centerY=fy+fh*.49, upperY=fy+fh*.22, lowerY=fy+fh*.80;
    // Shutter and desk are seen from the side; its scanner registers each item.
    const dockW=m?71:98, dockH=m?53:66;
    box(plan,dockX-dockW/2,centerY-dockH/2,dockW,dockH,{rx:3,fill:"#e0e5d8",stroke:muted,"stroke-width":2});
    for(let i=1;i<5;i++)line(plan,dockX-dockW/2+5,centerY-dockH/2+i*dockH/5,dockX+dockW/2-5,centerY-dockH/2+i*dockH/5,
      {stroke:"#9cae9d","stroke-width":1.5});
    mid(plan,dockX,centerY-dockH/2-11,"Returns desk",{fill:inkColor,"font-size":m?14:18,"font-weight":690});
    const scan=mid(plan,dockX,centerY+dockH/2+23,"",{fill:blue,"font-size":m?13:17,"font-weight":700});
    const benchW=m?60:90,benchH=m?30:38;
    box(plan,benchX-benchW/2,centerY-benchH/2,benchW,benchH,{fill:"#cfbd8e",rx:3,stroke:"#897a4f","stroke-width":2});
    for(const side of [-1,1]) {
      line(plan,benchX+side*(benchW/2-4),centerY+benchH/2,benchX+side*(benchW/2-4),centerY+benchH/2+12,{stroke:"#897a4f","stroke-width":3});
    }
    mid(plan,benchX,centerY-31,"Inspection",{fill:inkColor,"font-size":m?15:19,"font-weight":690});

    const shelfW=m?73:93,shelfH=m?34:42;
    for(const side of [-1,1])line(plan,stockX+side*shelfW/2,upperY-shelfH/2,stockX+side*shelfW/2,upperY+shelfH/2+4,
      {stroke:green,"stroke-width":3.1});
    for(const edge of [-1,0,1])line(plan,stockX-shelfW/2,upperY+edge*shelfH/2,stockX+shelfW/2,upperY+edge*shelfH/2,
      {stroke:green,"stroke-width":2.5});
    mid(plan,stockX,upperY-shelfH/2-9,"Restock shelf",{fill:green,"font-size":m?14:18,"font-weight":700});
    mid(plan,stockX,upperY+shelfH/2+23,"Credit · $18",{fill:green,"font-size":m?14:17});
    const binW=m?59:74,binH=m?27:33;
    pen(plan,`M${stockX-binW/2} ${lowerY-binH/2} h${binW} l-6 ${binH} h-${binW-12} Z`,{
      fill:"#e4c7a7",stroke:red,"stroke-width":2.4});
    pen(plan,`M${stockX-binW/2-3} ${lowerY-binH/2-3} H${stockX+binW/2+3}`,{stroke:red,"stroke-width":3});
    for(let i=-1;i<=1;i++)pen(plan,`M${stockX+i*11-3} ${lowerY+binH/2-4} l7 -11`,{stroke:"#b16f57","stroke-width":1.6});
    mid(plan,stockX,lowerY-binH/2-11,"Scrap bin",{fill:red,"font-size":m?14:18,"font-weight":700});
    mid(plan,stockX,lowerY+binH/2+22,"Scrap · $12",{fill:red,"font-size":m?14:17});

    const approach=arrow(plan,`M${dockX+dockW/2+4} ${centerY} H${benchX-benchW/2-5}`,blue,2.4);
    const routeX=benchX+fw*.105;
    function forkPath(target) {
      const side=target<centerY?-1:1;
      return `M${benchX+benchW/2+3} ${centerY} Q${routeX} ${centerY} ${routeX+7} ${centerY+side*14} L${routeX+fw*.085} ${target} H${stockX-shelfW/2-6}`;
    }
    const forks=[arrow(plan,forkPath(upperY),green,2.5),arrow(plan,forkPath(lowerY),red,2.5)];
    const cartons=[["$18","#e9bf62","#644d28",upperY],["$12","#8db8cf","#334f62",lowerY]].map(([amount,fill,stroke,target],i)=>{
      const item=group(plan);
      pen(item,"M-16 -7 l7 -5 h24 l-6 5 Z",{fill:"#fcf5da",stroke,"stroke-width":1.3});
      pen(item,"M9 -7 l6 -5 v14 l-6 6 Z",{fill:stroke,stroke,"stroke-width":1});
      box(item,-16,-7,25,15,{fill,stroke,"stroke-width":1.2});
      mid(item,-3,4,amount,{fill:"#20343d","font-size":10.4,"font-weight":750});
      const offset=i===0?-9:10;
      const motion=pen(plan,`M${dockX+(i===0?-8:10)} ${centerY+offset} H${benchX-fw*.06} Q${benchX} ${centerY+offset} ${benchX+fw*.03} ${centerY} C${routeX+10} ${centerY} ${routeX+fw*.04} ${target} ${stockX} ${target}`,{});
      const length=motion.getTotalLength();
      return v=>{
        const point=motion.getPointAtLength(v*length);
        item.setAttribute("transform",`translate(${point.x} ${point.y}) scale(${m?.88:1})`);
      };
    });
    const rail=m?325:0;
    function ruledSheet(x,y,w,h,title,color,rows) {
      const sheet=group(g);
      box(sheet,x+3,y+4,w,h,{rx:2,fill:"#d4ddcc"});
      box(sheet,x,y,w,h,{rx:2,fill:"#fffcf1",stroke:"#adbaa8","stroke-width":1.4});
      const margin=m?14:22;
      label(sheet,x+margin,y+(m?26:29),title,{fill:color,"font-size":m?17:20,"font-weight":720});
      const outputs=rows.map((name,i)=>{
        const rowY=y+(m?53:58)+i*(m?30:29);
        line(sheet,x+9,rowY+8,x+w-9,rowY+8,{stroke:"#d4dbcf","stroke-width":1});
        label(sheet,x+margin,rowY,name,{fill:muted,"font-size":m?13:16});
        return label(sheet,x+w-margin,rowY,"",{"text-anchor":"end",fill:inkColor,"font-size":m?14:18,"font-weight":690});
      });
      return outputs;
    }
    const sheetW=m?217:344;
    const stock=ruledSheet(m?12:761,m?rail:61,sheetW,m?134:131,"Stock ledger",green,["Received","Sellable stock","Scrapped"]);
    const money=ruledSheet(m?250:761,m?rail:216,sheetW,m?134:118,"Refund ledger",blue,["Eligible credit","Settlement"]);
    const note=label(g,m?12:25,H-(m?15:22),"",{fill:muted,"font-size":m?14:18});
    return p=>{
      const scanned=beat>0?2:p<.30?0:p<.58?1:2;
      const inspected=beat===0?0:beat===2?2:p<.50?0:p<.85?1:2;
      scan.textContent=scanned+" of 2 scanned";
      stock[0].textContent=String(scanned);
      stock[1].textContent=inspected>=1?"+1":"—";
      stock[2].textContent=inspected===2?"1":"—";
      money[0].textContent=inspected===2?"$30":inspected===1?"$18":"—";
      money[1].textContent=beat===2&&p>.30?"Await receipt":"—";
      approach(beat===0?stage(p,.55,.86):1);
      cartons.forEach((move,i)=>move(beat===0?0:beat===2?1:stage(p,.05+i*.27,.51+i*.34)));
      forks.forEach((advance,i)=>advance(beat===0?0:beat===2?1:stage(p,.22+i*.32,.47+i*.32)));
      note.textContent=beat===0?"Two returned items match the original order lines.":beat===1?
        "The inspector chooses a disposition for each item.":"Settlement still needs a payment receipt.";
    };
  }

  return {estate,live,sandbox,logistics};
})();
