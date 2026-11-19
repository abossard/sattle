// Three views of the same shop: its source layers, the alarm policy, and the
// grouping that turns pod calls and Azure records into a typed service graph.
const ModelScenes = (() => {
  function pens(ctx) {
    const {el, txt, dot} = ctx;
    const group = parent => el(parent, "g");
    const path = (parent,d,attrs) => el(parent,"path",{d,fill:"none",...attrs});
    const type = (parent,x,y,value,options={}) => txt(parent,x,y,value,{
      "font-size":18, "font-weight":550, ...options
    });
    const center = (parent,x,y,value,options={}) =>
      type(parent,x,y,value,{"text-anchor":"middle",...options});
    function thread(parent,d,color,{width=3,arrow=true,dashed=false}={}) {
      const attrs={"stroke-width":width,"stroke-linecap":"round","stroke-linejoin":"round"};
      path(parent,d,{...attrs,stroke:color,"stroke-opacity":.27,...(dashed?{"stroke-dasharray":"5 6"}:{})});
      const live=path(parent,d,{...attrs,stroke:color});
      const len=live.getTotalLength();
      live.setAttribute("stroke-dasharray",len+" "+len);
      let head;
      if (arrow) {
        const tip=live.getPointAtLength(len), before=live.getPointAtLength(Math.max(0,len-2));
        const angle=180*Math.atan2(tip.y-before.y,tip.x-before.x)/Math.PI;
        head=path(parent,"M-9 -5 L0 0 L-9 5",{...attrs,stroke:color,
          transform:"translate("+tip.x+" "+tip.y+") rotate("+angle+")"});
      }
      const pulse=dot(parent,0,0,width+1,{fill:color});
      return value=>{
        const p=Math.min(1,Math.max(0,value)), q=live.getPointAtLength(len*p);
        live.setAttribute("stroke-dashoffset",len*(1-p));
        if(head)head.setAttribute("opacity",p>=.985?1:0);
        pulse.setAttribute("cx",q.x);pulse.setAttribute("cy",q.y);
        pulse.setAttribute("opacity",p>.005&&p<.995?1:0);
      };
    }
    return {group,path,type,center,thread};
  }

  function origins(ctx) {
    const {g,el,box,dot,line,mobile:m,W,H,stage,beat}=ctx;
    const {group,type,center,thread}=pens(ctx);
    const {master,structure,health,nodes,edges}=ShopMaster;
    const pale="#edf6f1", light="#bcd3dc", mint="#7befce", blue="#8aceed", gold="#ffcb80", lilac="#d5b1ff";
    box(g,0,0,W,H,{fill:"#102a37",rx:15});
    const inset=m?17:25, chartW=m?W-inset*2:718, chartY=m?75:83, chartH=m?183:224;
    const scopeWidth=m?W:769;
    if(m) line(g,inset,322,W-inset,322,{stroke:"#42616d","stroke-width":1.5});
    else line(g,scopeWidth,23,scopeWidth,H-22,{stroke:"#42616d","stroke-width":1.5});
    type(g,inset,m?30:38,"Master graph",{fill:pale,"font-size":m?23:29,"font-weight":730});
    type(g,scopeWidth-inset,m?29:37,"Final snapshot  ·  r"+master.revision,{
      fill:light,"font-size":m?14:17,"text-anchor":"end"});
    type(g,inset,m?58:74,master.entities+" entities  /  "+master.relationships+" relationships",{
      fill:mint,"font-size":m?19:23,"font-weight":660});

    // The viewport changes magnification; geometry always comes from the actual
    // snapshot. Grouping here is spatial, with every exported edge retained.
    const window=el(g,"svg",{x:inset,y:chartY,width:chartW,height:chartH,overflow:"hidden"});
    const atlas=group(window);
    const regions={
      runtime:[.036,.12,.395,.785],
      activity:[.50,.18,.249,.675],
      meters:[.812,.105,.174,.335],
      other:[.825,.615,.14,.275]
    };
    const points=nodes.map(([kind,name,region,fx,fy])=>{
      const [x,y,w,h]=regions[region];
      return {x:(x+w*fx)*chartW,y:(y+h*fy)*chartH,kind,name,region};
    });
    const palette={runtime:blue,activity:gold,meters:lilac,other:"#95b2be"};
    const strokes=group(atlas);
    for(const [a,b,kind] of edges) {
      const s=points[a],t=points[b];
      const edge=line(strokes,s.x,s.y,t.x,t.y,{
        stroke:kind==="Calls"?mint:palette[s.region],"stroke-width":kind==="Calls"?2.4:m?1.2:1.7,
        "stroke-opacity":kind==="Calls"?1:.58,"data-master-link":kind
      });
      el(edge,"title",{},s.name+" → "+t.name+" · "+kind);
    }
    const marks=group(atlas);
    const highlights=[];
    for(const point of points) {
      const {x,y,kind,name,region}=point;
      const node=group(marks);
      node.setAttribute("data-master-entity",kind);
      el(node,"title",{},name+" · "+kind);
      const active=kind==="AzureResource"&&name==="pg-shop"||kind==="Service"&&["web","api","database"].includes(name);
      const hub=kind==="AzureResource"&&name==="aks-synth";
      // Hollow meters remain distinct from the things they measure.
      const hollow=kind==="Meter";
      const color=active?mint:palette[region];
      const r=(m?3.5:4.6)*(hub?1.42:kind==="Pod"?1.22:1);
      dot(node,x,y,r,{fill:hollow?"#102a37":color,stroke:color,"stroke-width":hollow?2.2:.7});
      if(active)highlights.push([name,dot(node,x,y,r+(m?3.1:4.1),{
        fill:"none",stroke:mint,"stroke-width":1.9})]);
    }
    const labels=group(g);
    const tags=[
      [m?.015:.045,"Pods and services",blue],
      [m?.49:.50,"Azure activity",gold],
      [.99,m?"Resources":"Resources & meters",lilac]
    ];
    for(const [fx,label,color] of tags) {
      type(labels,inset+chartW*fx,chartY+chartH+18,label,{
        fill:color,"font-size":m?14:16,"font-weight":650,"text-anchor":fx>.9?"end":"start"
      });
    }
    const scaleLine=type(g,inset,m?307:376,"",{fill:light,"font-size":m?14.3:17});
    const right=m?inset:793, top=m?348:43;
    type(g,right,top,beat===2?"Two views of the shop":"One path through the shop",{
      fill:pale,"font-size":m?21:23,"font-weight":710});
    const links=[], routeDots=[];
    if(beat<2) {
      const names=["web","API","database","pg-shop"];
      const hints=["serves the page","handles the request","names its database","stores the order"];
      const positions=m?[[41,402],[158,402],[282,402],[405,402]]:names.map((_,i)=>[820,93+70*i]);
      positions.forEach(([x,y],i)=>{
        if(i<3) {
          const [nx,ny]=positions[i+1];
          links.push(thread(g,m?`M${x+13} ${y} H${nx-17}`:`M${x} ${y+13} V${ny-16}`,mint,{width:3}));
        }
        routeDots.push(dot(g,x,y,10,{fill:"#163a3c",stroke:mint,"stroke-width":2.5}));
        if(m) center(g,x,y+34,names[i],{fill:pale,"font-size":17,"font-weight":700});
        else {
          type(g,x+29,y-1,names[i],{fill:pale,"font-size":24,"font-weight":690});
          type(g,x+29,y+22,hints[i],{fill:light,"font-size":16});
        }
      });
      type(g,right,m?478:376,"Arrows follow a workload dependency.",{fill:mint,"font-size":m?15:16});
    } else {
      const boxW=m?(W-2*inset-26)/2:277;
      const cardTop=m?367:70, cardHeight=m?88:81;
      const views=[
        [structure,"Workload structure","#8aceed","Which parts connect?"],
        [health,"Health model","#7befce","Which part is failing?"]
      ];
      views.forEach(([graph,name,color,question],i)=>{
        const x=right+(m?i*(boxW+26):0), y=cardTop+(m?0:i*173);
        box(g,x,y,boxW,cardHeight,{fill:i?"#173c3e":"#193b4b",rx:12,stroke:color,"stroke-width":1.3});
        type(g,x+13,y+25,name,{fill:color,"font-size":m?17:20,"font-weight":740});
        type(g,x+13,y+48,question,{fill:pale,"font-size":m?14.5:17});
        type(g,x+13,y+69,graph.entities+" entities · "+graph.relationships+" links",{fill:light,"font-size":m?13.5:15});
      });
      if(m) {
        links.push(thread(g,`M${right+boxW+2} ${cardTop+42} h22`,mint,{width:2}));
        type(g,right,482,"Health adds meter readings and authored rules.",{fill:mint,"font-size":15});
      } else {
        links.push(thread(g,`M${right+20} ${cardTop+cardHeight+5} v80`,mint,{width:2}));
        type(g,right+38,cardTop+cardHeight+43,"Add meter readings",{fill:mint,"font-size":17});
        type(g,right+38,cardTop+cardHeight+65,"and authored rules",{fill:mint,"font-size":17});
      }
    }
    const highlightOrder=["web","api","database","pg-shop"];
    return p=>{
      const reveal=beat===0?0:beat===1?stage(p,.05,.55):1;
      const ease=reveal*reveal*(3-2*reveal), zoom=1+.62*(1-ease);
      atlas.setAttribute("transform",`translate(${(1-zoom)*chartW*.22} ${(1-zoom)*chartH*.44}) scale(${zoom})`);
      labels.setAttribute("opacity",ease);
      scaleLine.textContent=zoom>1.1?"Close-up · three recorded calls between web and API pods.":
        "Each dot represents one entity. Each line represents one relationship.";
      if(m && zoom<=1.1)scaleLine.textContent="One dot per entity. One line per relationship.";
      links.forEach((fn,i)=>fn(beat===0?stage(p,.08+i*.20,.30+i*.20):beat===2?stage(p,.38,.71):1));
      routeDots.forEach((node,i)=>node.setAttribute("fill",beat>0||p>.05+i*.19?mint:"#163a3c"));
      highlights.forEach(([name,ring])=>ring.setAttribute("opacity",beat===0?
        stage(p,.03+.17*highlightOrder.indexOf(name),.20+.17*highlightOrder.indexOf(name)):beat===1?1:.52));
    };
  }

  function rules(ctx) {
    const {g,box,dot,line,W,H,mobile:m,stage,beat}=ctx;
    const {group,path,type,center,thread}=pens(ctx);
    const ink="#183649",muted="#465d67",blue="#235a82",good="#11654c",bad="#a02f47",gold="#875211";
    box(g,0,0,W,H,{fill:"#fff5de",rx:15});
    const sx=m?24:605, sy=m?284:63, sw=m?431:491, sh=m?148:222;
    const ay=m?145:198, cx1=m?104:130, cx2=m?345:416;
    const radius=m?37:47, ring=group(g);
    type(g,m?24:32,m?24:42,"Azure PostgreSQL · pg-shop",{fill:ink,"font-size":m?21:24,"font-weight":720});
    const up=`M${cx1} ${ay-radius-1} C${cx1+12} ${ay-radius-67} ${cx2-12} ${ay-radius-67} ${cx2} ${ay-radius-1}`;
    const down=`M${cx2} ${ay+radius+1} C${cx2-12} ${ay+radius+67} ${cx1+12} ${ay+radius+67} ${cx1} ${ay+radius+1}`;
    const enter=thread(ring,up,bad,{width:3.5}), leave=thread(ring,down,good,{width:3.5});
    center(ring,(cx1+cx2)/2,m?57:90,"1 bad minute",{fill:bad,"font-size":m?19:23,"font-weight":700});
    center(ring,(cx1+cx2)/2,m?254:324,"2 good minutes",{fill:good,"font-size":m?19:23,"font-weight":700});
    const circles=[[cx1,good,"✓","Healthy"],[cx2,bad,"×","Unhealthy"]].map(([cx,color,sign,name])=>{
      const node=group(ring);
      const disc=dot(node,cx,ay,radius,{fill:"#fffaf0",stroke:color,"stroke-width":3});
      center(node,cx,ay-4,sign,{fill:color,"font-size":m?29:39,"font-weight":730});
      center(node,cx,ay+(m?23:31),name,{fill:color,"font-size":m?12:15,"font-weight":700});
      return disc;
    });
    const memory=group(ring), mid=(cx1+cx2)/2;
    const count=center(memory,mid,ay+3,"",{fill:bad,"font-size":m?24:31,"font-weight":750});
    center(memory,mid,ay+(m?27:34),"good minutes",{fill:muted,"font-size":m?14:17});
    // Draw a literal scope boundary around Kubernetes services. The Azure
    // server and the example cache are external dependencies.
    const scope=group(g);
    const scopeBorder=box(scope,sx,sy,sw,sh,{rx:18,fill:"#eef2e7",stroke:blue,"stroke-width":2.5,"stroke-dasharray":"10 6"});
    type(scope,sx+17,sy+29,"Cluster   aks-synth",{fill:blue,"font-size":m?17:21,"font-weight":690});
    type(scope,sx+17,sy+59,"Namespace   shop",{fill:ink,"font-size":m?17:21,"font-weight":690});
    const dbx=sx+sw*.23, apix=sx+sw*.73, nodeY=sy+(m?96:129);
    const scopedFlow=thread(scope,`M${apix-14} ${nodeY} H${dbx+17}`,blue,{width:3});
    dot(scope,dbx,nodeY,10,{fill:"#ccdef0",stroke:blue,"stroke-width":3});
    dot(scope,apix,nodeY,10,{fill:"#ccdef0",stroke:blue,"stroke-width":3});
    center(scope,dbx,nodeY+30,"database service",{fill:ink,"font-size":m?15:19});
    center(scope,apix,nodeY+30,"API",{fill:ink,"font-size":m?16:20});
    center(scope,(dbx+apix)/2,nodeY-15,"required",{fill:blue,"font-size":m?13:16});
    const portX=m?52:530, portY=m?262:nodeY;
    const backPath=m
      ?`M${dbx-13} ${nodeY} H12 V${portY} H${portX-9}`
      :`M${dbx-13} ${nodeY} H${portX+39}`;
    const backs=thread(g,backPath,blue,{width:2.5});
    if(m)type(g,portX,portY+5,"pg-shop",{fill:blue,"font-size":15,"font-weight":650});
    else center(g,portX,portY+5,"pg-shop",{fill:blue,"font-size":16,"font-weight":650});
    const optional=group(g), cacheY=m?473:350;
    path(optional,`M${apix+9} ${nodeY+6} H${sx+sw+8} V${cacheY} H${apix+82}`,{
      stroke:gold,"stroke-width":2.5,"stroke-dasharray":"6 6"});
    dot(optional,apix+76,cacheY,3.5,{fill:gold});
    path(optional,`M${apix+73} ${cacheY-2} l-22 -13`,{stroke:gold,"stroke-width":3});
    dot(optional,apix+48,cacheY,3.5,{fill:gold});
    line(optional,apix+45,cacheY,apix+24,cacheY,{stroke:gold,"stroke-width":2.5});
    center(optional,apix-6,cacheY+5,"cache",{fill:gold,"font-size":m?15:19,"font-weight":700});
    type(optional,m?28:sx+15,cacheY+4,"Optional call · example",{fill:gold,"font-size":m?16:19});
    if(!m) type(g,34,375,"Rule  database-down    /    Meter  is_db_alive",{fill:muted,"font-size":17});
    return p=>{
      const transition=beat===1;
      ring.setAttribute("opacity",beat===0?.68:1);
      scopeBorder.setAttribute("stroke-width",beat===0?3.8:2);
      enter(transition?stage(p,.25,.43):beat===2?1:0);
      leave(transition?stage(p,.58,.82):beat===2?1:0);
      const alarming=transition && p>=.43 && p<.82;
      memory.setAttribute("opacity",transition?1:0);
      count.textContent=p<.43?"—":p<.61?"0 / 2":p<.82?"1 / 2":"2 / 2";
      count.setAttribute("fill",alarming?bad:good);
      circles[0].setAttribute("fill",alarming?"#fffaf0":"#c7f0db");
      circles[1].setAttribute("fill",alarming?"#ffd4d6":"#fffaf0");
      scopedFlow(beat===2?stage(p,.08,.38):beat===0?stage(p,.38,.69):.35);
      backs(beat===2?stage(p,.35,.58):beat===0?stage(p,.68,.9):.35);
      optional.setAttribute("opacity",beat===2?stage(p,.42,.70):0);
    };
  }

  function layers(ctx) {
    const {g,el,box,dot,W,H,mobile:m,stage,beat}=ctx;
    const {group,path,type,center,thread}=pens(ctx);
    const ink="#1b3443", blue="#0b6183", teal="#096c61", purple="#67408f";
    box(g,0,0,W,H,{fill:"#f3f4f0",rx:15});
    // The five pod positions and their three observed web→API pairs come
    // from the replay's Kubernetes watch and Hubble records.
    const a=m?99:142, b=m?371:439, y=m?172:224;
    const rx=m?73:92, ry=m?72:89;
    const db={x:m?82:733,y:m?327:y};
    const pg={x:m?368:968,y:db.y};
    const graph=group(g), pair=group(g);
    const hulls=[[a,blue,"web service"],[b,teal,"API service"]].map(([x,color,name])=>{
      const hull=group(graph);
      el(hull,"ellipse",{cx:x,cy:y,rx,ry,fill:color,"fill-opacity":.07,stroke:color,
        "stroke-width":2.5,"stroke-dasharray":"7 4"});
      center(hull,x,y-ry-11,name,{fill:color,"font-size":m?20:23,"font-weight":730});
      return hull;
    });
    const step=m?28:35;
    const webPods=[-1,0,1].map((i)=>[a+rx*.43,y+i*step]);
    const apiPods=[-1,1].map((i)=>[b-rx*.43,y+i*step*.7]);
    const calls=webPods.map(([x1,y1],i)=>{
      const [x2,y2]=apiPods[[0,1,0][i]];
      return thread(graph,`M${x1+8} ${y1} C${x1+79} ${y1} ${x2-79} ${y2} ${x2-9} ${y2}`,blue,{width:m?2:2.5});
    });
    [...webPods,...apiPods].forEach(([x,py],i)=>{
      const web=i<3, color=web?blue:teal;
      dot(graph,x,py,6.5,{fill:color,stroke:"#ffffff","stroke-width":2});
      type(graph,x+(web?-13:13),py+5,"pod "+(web?i+1:i-2),{
        fill:ink,"font-size":m?15:17,"text-anchor":web?"end":"start"});
    });
    const bridgeY=y-ry-(m?37:46);
    const lifted=thread(graph,`M${a} ${y-ry-37} Q${a} ${bridgeY-8} ${a+18} ${bridgeY-8} H${b-18} Q${b} ${bridgeY-8} ${b} ${y-ry-37}`,teal,{width:4});
    center(graph,(a+b)/2,bridgeY-17,"TalksTo",{fill:teal,"font-size":m?17:20,"font-weight":730});
    center(graph,(a+b)/2,y+ry+(m?23:33),"3 pod calls → 1 service link",{fill:blue,"font-size":m?17:21,"font-weight":650});

    // One concrete edge is also an instance of the ontology's type signature.
    // Give the two types distinct silhouettes, so the relation remains readable
    // without color.
    const dw=m?98:123, dh=m?55:67, pr=m?45:51;
    const apiToService=m
      ?`M${b+15} ${y+ry+2} C${b+12} ${y+ry+65} ${db.x} ${db.y-67} ${db.x} ${db.y-dh/2-6}`
      :`M${b+rx+3} ${y} H${db.x-dw/2-6}`;
    thread(g,apiToService,"#677f85",{width:2})(1);
    if(!m)center(g,(b+rx+db.x-dw/2)/2,y-12,"needs",{fill:"#526b72","font-size":15});
    const translate=thread(pair,`M${db.x+dw/2+3} ${db.y} H${pg.x-pr-6}`,purple,{width:3.5});
    center(pair,(db.x+dw/2+pg.x-pr)/2,db.y-12,"BackedBy",{fill:purple,"font-size":m?17:20,"font-weight":730});
    box(pair,db.x-dw/2,db.y-dh/2,dw,dh,{rx:8,fill:"#d9eaf3",stroke:blue,"stroke-width":2.5});
    center(pair,db.x,db.y-6,"database",{fill:ink,"font-size":m?17:21,"font-weight":700});
    center(pair,db.x,db.y+20,"service",{fill:blue,"font-size":m?14:16});
    el(pair,"ellipse",{cx:pg.x,cy:pg.y,rx:pr,ry:dh/2+4,fill:"#e8dff3",stroke:purple,"stroke-width":2.5});
    center(pair,pg.x,pg.y-6,"pg-shop",{fill:ink,"font-size":m?17:20,"font-weight":700});
    center(pair,pg.x,pg.y+20,"server",{fill:purple,"font-size":m?14:16});

    // Two sources use the same stable Azure resource key. The paths terminate
    // on one server; the Kubernetes database service remains a separate entity.
    const idEvidence=group(g);
    const recordX=m?40:762, ys=m?[403,467]:[105,351];
    const joinLines=ys.map((ry,i)=>{
      const yOffset=m?0:i===0?-1:1;
      const endX=m?pg.x-(i?4:14):pg.x, endY=pg.y+(m?1:yOffset)*(dh/2+7);
      const startX=m?recordX+257:recordX+210;
      const d=m
        ?`M${startX} ${ry-2} Q${endX} ${ry-2} ${endX} ${endY+6}`
        :`M${startX} ${ry+(i===0?20:-9)} Q${endX} ${ry+(i===0?46:-32)} ${endX} ${endY}`;
      const connection=thread(idEvidence,d,purple,{width:2,arrow:false});
      const sourceName=i?"Azure Monitor":"Azure Resource Graph";
      type(idEvidence,recordX,ry-10,sourceName,{fill:ink,"font-size":m?16:19,"font-weight":700});
      type(idEvidence,recordX,ry+14,"…/flexibleservers/pg-shop",{fill:purple,"font-size":m?15:17,"font-family":"Menlo, monospace"});
      return connection;
    });
    if(!m) {
      type(g,37,45,"Running copies",{fill:blue,"font-size":25,"font-weight":730});
      type(g,647,45,"One backing server",{fill:purple,"font-size":25,"font-weight":730});

    }
    return p=>{
      const activePods=beat===1, activeEdge=beat===2;
      graph.setAttribute("opacity",activePods?1:.76);
      hulls.forEach(h=>h.firstChild.setAttribute("stroke-dasharray",activePods&&p>.58?"none":"7 4"));
      calls.forEach((fn,i)=>fn(activePods?stage(p,.04+i*.10,.40+i*.10):1));
      lifted(activePods?stage(p,.48,.81):beat===2?1:0);
      joinLines.forEach((fn,i)=>fn(beat===0?stage(p,.15+i*.20,.51+i*.20):1));
      idEvidence.setAttribute("opacity",beat===0?1:.73);
      translate(activeEdge?stage(p,.18,.65):beat===1?.6:1);
      pair.setAttribute("opacity",activePods?.77:1);
    };
  }
  return {origins,rules,layers};
})();
