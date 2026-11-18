// The film and browser lesson use the same map and seekable, deterministic animations.
const ShopMap = (() => {
  const ns="http://www.w3.org/2000/svg";
  const ink="#14233b", text="#f4f7ff", muted="#c0cfde", mint="#6ce5b4", gold="#ffd16f", red="#ff98a8", sky="#b9a3ff";
  const clamp=(x,a=0,b=1)=>Math.max(a,Math.min(b,x));
  const stage=(p,a,b)=>clamp((p-a)/(b-a));
  const nodes={
    gateway:[101,73,"Gateway","apim-shop","gateway"], web:[101,164,"Web","service","web"],
    api:[364,114,"API","service","api"], database:[618,114,"Database","service","api"],
    pg:[871,72,"pg-shop","PostgreSQL","db"], cache:[871,171,"shopcache","Azure Redis","cache"],
    storage:[1048,219,"shopassets","Azure Storage","storage"], offers:[1051,67,"offers-cache","new Redis","cache"]
  };
  const edges={
    ga:["gateway","api","M175 73 C231 73 239 114 290 114"],
    wa:["web","api","M175 164 C229 164 237 114 290 114"],
    ad:["api","database","M438 114 L544 114"],
    dp:["database","pg","M692 114 C739 114 752 72 797 72"],
    wc:["web","cache","M175 173 C272 207 431 196 560 196 S735 171 797 171"],
    ws:["web","storage","M163 184 C254 250 512 244 717 243 S915 220 974 220"],
    ac:["api","cache","M437 129 C539 159 638 166 797 166"],
    ao:["api","offers","M411 92 C483 28 632 30 738 30 L943 30 Q972 30 991 43"]
  };
  const icons={
    gateway:"M-12 -10H3L12 0 3 10H-12 M-6 -5L0 0 -6 5",
    web:"M-12 -10H12V6H-12Z M-5 10H5 M0 6V10",
    api:"M-5 -8L-12 0 -5 8 M5 -8L12 0 5 8 M2 -10L-2 10",
    db:"M-11 -7C-11 -14 11 -14 11 -7S-11 0 -11 -7 M-11 -7V7C-11 14 11 14 11 7V-7 M-11 0C-11 7 11 7 11 0",
    cache:"M2 -12L-9 2H-1L-3 12 10 -3H2Z",storage:"M-12 -5V10H12V-5Z M-12 -5V-10H-2L2 -6H12"
  };
  const focusSets={origins:[["web","api","pg"],["gateway","web","pg","cache"],["web","api","database","pg"]],rules:[["web","api","database"],["pg"],["web","api"]],layers:[["database","pg"],["web","api"],["database","pg"]],incident:[["pg"],["pg","database","api","gateway","web"],["pg"]],dependency:[["cache","web"],["api","cache"],["gateway","api","cache"]],azure:[["api"],["offers","api"],["offers"]],replay:[["pg","api"],["pg","database","api","web"],["web","api","pg"]],inspect:[["pg"],["pg","database","api"],["gateway","api","pg","cache"]]};
  const routes={origins:[["wa","ad","dp"],["ws","wc","dp"],["ga","wa","ad","dp"]],rules:[["wa","ad"],["dp"],["wa","ad"]],layers:[["dp"],["wa"],["dp"]],incident:[["dp"],["dp","ad","wa","ga"],["dp","ad","wa","ga"]],dependency:[["wc"],["ac"],["ac","ga","wa"]],azure:[[],["ao"],["ao"]],replay:[["ad","dp"],["dp","ad","ga"],["wa","ad","dp"]],inspect:[["dp"],["dp","ad"],["wc","wa","ad","dp"]]};
  function element(tag,attrs={},value=""){
    const e=document.createElementNS(ns,tag);
    for(const [k,v] of Object.entries(attrs))e.setAttribute(k,v);
    if(value)e.textContent=value;
    return e;
  }
  function add(parent,tag,attrs,value){const e=element(tag,attrs,value);parent.append(e);return e;}
  function label(parent,x,y,value,size=16,color=text,attrs={}){
    return add(parent,"text",{x,y,fill:color,"font-size":size,"font-family":"Arial, sans-serif",...attrs},value);
  }
  function dot(parent,r,color){return add(parent,"circle",{r,fill:color});}
  // The same two work surfaces sit side by side on film and stack on phones.
  // Coordinates in each are 540 × 160. Everything is a pure function of progress.
  const evidenceInk="#192943", evidenceMuted="#4a556b", paper="#f8f7ef";
  const blue="#126196", iris="#6949b0", teal="#087d70", ochre="#995306", rust="#a42f48";
  const health={
    Healthy:{fill:"#064e42",edge:"#65e8ae",ink:"#096746",pale:"#d3f6e4",symbol:"✓"},
    Degraded:{fill:"#694310",edge:"#ffd368",ink:"#855000",pale:"#ffedb3",symbol:"▲"},
    Unhealthy:{fill:"#75263b",edge:"#ff8d9d",ink:"#a42949",pale:"#ffdee3",symbol:"×"},
    Unknown:{fill:"#4c347a",edge:"#d9b2ff",ink:"#653a9a",pale:"#edddff",symbol:"?"}
  };
  function evidenceView(svg,topic,beat,compact,assets,evidenceFirst){
    const y=evidenceFirst?1:compact?336:292;
    const w=evidenceFirst?680:compact?494:1158, h=evidenceFirst?471:compact?297:181;
    add(svg,"rect",{x:1,y,width:w,height:h,rx:11,fill:paper,stroke:"#b2b9c2"});
    const places=evidenceFirst?[[14,15,1.22],[14,244,1.22]]:
      compact?[[13,y+3,.87],[13,y+150,.87]]:[[18,y+4,1],[594,y+4,1]];
    const [left,right]=places.map(([x,y,s])=>add(svg,"g",{transform:`translate(${x} ${y}) scale(${s})`}));
    const seam=evidenceFirst?"M19 235H659":compact?`M16 ${y+149}h461`:`M576 ${y+15}v150`;
    add(svg,"path",{d:seam,stroke:"#c2c6c4","stroke-width":1.5});
    const updates=[];
    function drawText(g,x,y,value,size=20,color=evidenceInk,attrs={}){
      return label(g,x,y,value,size,color,attrs);
    }
    function title(g,value){
      drawText(g,6,26,value,20,evidenceInk,{"font-weight":700,"data-evidence-heading":""});
      line(g,"M6 36H530","#c2c6c4",1);
    }
    function line(g,d,color,width=2,dash){
      return add(g,"path",{d,fill:"none",stroke:color,"stroke-width":width,
        "stroke-linecap":"round","stroke-linejoin":"round",...(dash?{"stroke-dasharray":dash}:{})});
    }
    function box(g,x,y,w,h,fill,stroke="none",radius=7){
      return add(g,"rect",{x,y,width:w,height:h,rx:radius,fill,stroke,"stroke-width":1.6});
    }
    function circle(g,x,y,r,fill,stroke="none"){
      return add(g,"circle",{cx:x,cy:y,r,fill,stroke,"stroke-width":2});
    }
    function tag(g,x,y,value,color,fill,width){
      const w=width || value.length*9.5+22;
      box(g,x,y-19,w,27,fill,"none",5);
      drawText(g,x+10,y,value,16,color,{"font-weight":700});
    }
    function status(g,x,y,name,w=133){
      const s=health[name];
      tag(g,x,y,s.symbol+"  "+name,s.ink,s.pale,w);
    }
    function spot(g,x,y,name,color=blue,anchor="middle"){
      circle(g,x,y,7,paper,color);
      drawText(g,x,y+30,name,17,evidenceInk,{"text-anchor":anchor,"font-weight":650});
    }
    function reveal(g,a,b,dim=.15){
      updates.push(p=>g.setAttribute("opacity",dim+(1-dim)*stage(p,a,b)));
    }
    function travel(g,d,color,a,b){
      line(g,d,"#c2c8cc",3);
      const overlay=line(g,d,color,4), length=overlay.getTotalLength();
      overlay.setAttribute("stroke-dasharray",length+" "+length);
      const head=circle(g,0,0,5,color);
      updates.push(p=>{
        const t=stage(p,a,b), xy=overlay.getPointAtLength(length*t);
        overlay.setAttribute("stroke-dashoffset",length*(1-t));
        head.setAttribute("cx",xy.x);head.setAttribute("cy",xy.y);
        head.setAttribute("opacity",t>0&&t<1?1:0);
      });
    }
    function row(g,y,field,value,color=blue,offset=155){
      drawText(g,8,y,field,17,evidenceMuted);
      drawText(g,offset,y,value,19,color,{"font-weight":650});
    }
    function nodeRow(g,y,name,state){
      drawText(g,13,y,name,20,evidenceInk,{"font-weight":600});
      status(g,324,y,state,164);
    }
    function scaleChart(g,values,firstMinute,limit,unit,color){
      const x0=46,dx=108,base=135;
      const threshold=base-70/limit*62;
      line(g,`M37 ${threshold}H512`,ochre,1.5,"4 4");
      drawText(g,510,threshold-5,"70%",15,ochre,{"text-anchor":"end"});
      line(g,"M37 60V135H513","#9ba8ae",1.6);
      values.forEach((n,i)=>{
        const x=x0+i*dx,ht=(n/limit)*62;
        const bar=box(g,x,base-Math.max(3,ht),53,Math.max(3,ht),color);
        drawText(g,x+26,base-Math.max(4,ht)-6,String(n)+unit,17,evidenceInk,{"text-anchor":"middle"});
        drawText(g,x+26,157,"10:"+String(firstMinute+i).padStart(2,"0"),15,evidenceMuted,{"text-anchor":"middle"});
        reveal(bar,.06+i*.10,.22+i*.10,.36);
      });
    }
    function sampleTrace(g,indexAt){
      const values=[1,1,1,0,0,0,1,1,1,1], x=i=>42+i*51, y=v=>v?73:116;
      for(const v of [0,1]){
        line(g,`M36 ${y(v)}H512`,"#bdc8c6",1,"3 5");
        drawText(g,12,y(v)+4,String(v),16,evidenceMuted);
      }
      const d="M"+x(0)+" "+y(1)+values.slice(1).map((v,i)=>"H"+x(i+1)+"V"+y(v)).join("");
      line(g,d,teal,3);
      [0,3,6,9].forEach(i=>drawText(g,x(i),151,"10:"+String(i+8).padStart(2,"0"),15,evidenceMuted,{"text-anchor":"middle"}));
      const cursor=line(g,"",iris,2,"3 4"), dot=circle(g,0,0,6,iris);
      updates.push(p=>{
        const col=indexAt(p);
        cursor.setAttribute("d",`M${x(col)} 52V131`);
        dot.setAttribute("cx",x(col));dot.setAttribute("cy",y(values[Math.floor(col)]));
      });
    }
    function ruleMemory(g){
      const states=["Unhealthy","Unhealthy","Healthy"], nums=["0 / 2","1 / 2","2 / 2"];
      ["10:13","10:14","10:15"].forEach((time,i)=>{
        const x=16+i*174, item=add(g,"g");
        drawText(item,x,65,time,17,evidenceMuted);
        drawText(item,x,100,nums[i],28,health[states[i]].ink,{"font-weight":700});
        status(item,x,134,states[i],153);reveal(item,.04+i*.2,.22+i*.2,.38);
      });
    }
    function sourceId(g,y,source,color){
      drawText(g,8,y,source,17,evidenceMuted);
      box(g,145,y-20,363,28,"#e5e7ec");
      drawText(g,156,y,"…/flexibleservers/pg-shop",18,color,{"font-family":"Menlo, monospace","font-weight":600});
    }
    function hostName(g,y,name){
      drawText(g,9,y,name,22,iris,{"font-family":"Menlo, monospace","font-weight":700});
      drawText(g,9,y+28,".redis.cache.windows.net",20,evidenceMuted,{"font-family":"Menlo, monospace"});
    }
    function twoCallers(g){
      travel(g,"M66 78H184L248 104H422",blue,.07,.61);
      travel(g,"M66 133H184L248 104",teal,.23,.68);
      spot(g,66,78,"Gateway",blue);
      drawText(g,66,157,"Web",17,evidenceInk,{"text-anchor":"middle","font-weight":650});
      circle(g,66,133,7,paper,teal);spot(g,422,104,"API",iris);
    }
    function miniChain(g,items,color,reverse=false){
      const points=items.map((_,i)=>50+i*(425/Math.max(1,items.length-1)));
      for(let i=1;i<points.length;i++){
        const from=reverse?points[i]:points[i-1], to=reverse?points[i-1]:points[i];
        const n=reverse?points.length-i-1:i-1;
        travel(g,`M${from} 96H${to}`,color,.04+n*.15,.34+n*.15);
      }
      items.forEach((v,i)=>spot(g,points[i],96,v,color));
    }
    function graphLayer(g,start,y,color,nodes,edges){
      const layer=add(g,"g",{transform:`translate(${start} ${y})`});
      edges.forEach(([a,b])=>line(layer,`M${nodes[a][0]} ${nodes[a][1]}L${nodes[b][0]} ${nodes[b][1]}`,color,2));
      nodes.forEach(([x,y])=>circle(layer,x,y,6,paper,color));
      return layer;
    }
    function comparison(g,labels,original,changed){
      drawText(g,133,66,"Original",17,evidenceMuted);
      drawText(g,329,66,"Changed",17,evidenceMuted);
      labels.forEach((v,i)=>{
        const y=100+i*40;drawText(g,8,y,v,19,evidenceInk,{"font-weight":650});
        status(g,128,y,original[i],151);
        const outcome=add(g,"g");status(outcome,317,y,changed[i],151);reveal(outcome,.17+i*.16,.38+i*.16,.12);
      });
    }
    switch(topic+"/"+beat){
      case "origins/0":
        title(left,"Two ways into the API");twoCallers(left);
        title(right,"The rest of the request");miniChain(right,["API","database","pg-shop"],iris);
        break;
      case "origins/1":{
        title(left,"Who owns each record?");
        [["Kubernetes","service / database",blue],["Network flows","web pod → API pod",teal],["Azure inventory","server / pg-shop",iris]].forEach(([a,b,c],i)=>{
          const y=66+i*38;box(left,8,y-16,5,22,c);
          drawText(left,24,y,a,18,evidenceInk);drawText(left,203,y,b,18,c,{"font-weight":650});
        });
        title(right,"One minute in Azure Monitor");
        tag(right,8,70,"pg-shop",iris,"#ece4ff",104);
        row(right,106,"meter","is_db_alive",teal,139);
        row(right,146,"10:11 UTC","0   /   no answer",rust,139);
        break;
      }
      case "origins/2":{
        title(left,"Master graph → workload structure");
        const cloud=[[4,48],[44,11],[50,83],[101,35],[125,75],[155,9],[170,64]];
        graphLayer(left,23,60,blue,cloud,[[0,1],[0,2],[1,3],[2,3],[3,4],[3,5],[4,6],[5,6]]);
        travel(left,"M218 106H302",iris,.12,.55);
        graphLayer(left,332,76,iris,[[0,30],[55,10],[110,30],[151,53]],[[0,1],[1,2],[2,3]]);
        title(right,"The health query adds states");
        miniChain(right,["pg-shop","database","API"],rust);
        status(right,179,64,"Unhealthy",147);
        break;
      }
      case "rules/0":{
        title(left,"Choose the workload boundary");
        box(left,9,49,508,105,"#e8f1fe","#84a6c9");
        drawText(left,22,72,"Cluster   aks-synth",17,blue,{"font-weight":650});
        box(left,125,85,377,49,"#d8f3e8","#74a998");
        drawText(left,140,116,"Namespace   shop",22,evidenceInk);
        title(right,"Give Sattle read access");
        row(right,69,"Kubernetes","services and pods",blue);
        row(right,105,"Network","calls between pods",teal);
        row(right,141,"Azure","inventory and meters",iris);
        break;
      }
      case "rules/1":
        title(left,"Your thresholds for pg-shop");
        row(left,78,"Raise alarm","1 minute at or below 0.5",rust,142);
        row(left,124,"Clear alarm","2 minutes at or above 0.9",teal,142);
        title(right,"How the rule counts");
        [["Bad reading",["0"],"Alarm on",rust],["Good readings",["1","1"],"Alarm off",teal]].forEach(([name,values,action,c],i)=>{
          const y=83+i*52;drawText(right,8,y,name,18,evidenceInk);
          values.forEach((v,j)=>{box(right,155+j*46,y-24,35,35,"#f2efe6",c);drawText(right,173+j*46,y,v,20,c,{"font-weight":700,"text-anchor":"middle"});});
          travel(right,`M259 ${y-8}H334`,c,.1+i*.20,.45+i*.20);
          drawText(right,360,y,action,22,c,{"font-weight":700});
        });break;
      case "rules/2":{
        title(left,"A required call");miniChain(left,["API","database","pg-shop"],rust);
        title(right,"An optional call · design example");
        const d="M52 95H462";line(right,d,iris,3,"7 8");
        spot(right,52,95,"API",iris);spot(right,462,95,"Cache",iris);
        tag(right,166,66,"Skip in health rule",iris,"#e9e1fa",186);
        break;
      }
      case "layers/0":{
        title(left,"Two records carry the same key");
        sourceId(left,85,"Inventory",blue);sourceId(left,133,"Meter target",teal);
        title(right,"Both observations describe one server");
        travel(right,"M48 72H161Q210 72 246 104H429",blue,.05,.52);
        travel(right,"M48 138H161Q209 138 246 104",teal,.22,.63);
        circle(right,48,72,7,paper,blue);circle(right,48,138,7,paper,teal);
        drawText(right,64,67,"ID",16,blue);drawText(right,64,132,"ID",16,teal);
        spot(right,429,104,"pg-shop",iris);break;
      }
      case "layers/1":{
        title(left,"Calls between running copies");
        ["web pod 1","web pod 2","web pod 3"].forEach((v,i)=>{
          const y=62+i*40;
          drawText(left,11,y+6,v,18,blue);
          travel(left,`M111 ${y}C215 ${y} 252 102 353 102`,i===1?teal:blue,.03+i*.10,.41+i*.10);
        });
        spot(left,361,102,"API pods",iris);
        title(right,"One link in the service view");
        miniChain(right,["web","API"],blue);
        tag(right,192,70,"TalksTo",blue,"#dcecfb",100);break;
      }
      case "layers/2":{
        title(left,"Kinds of things and links");
        tag(left,7,107,"Service",blue,"#deedfb",94);
        tag(left,350,107,"Platform resource",iris,"#eae1fb",170);
        travel(left,"M107 101H342",iris,.12,.57);
        drawText(left,223,81,"BackedBy",17,iris,{"font-weight":700,"text-anchor":"middle"});
        title(right,"The same pattern in this shop");
        miniChain(right,["database","pg-shop"],iris);
        drawText(right,256,75,"BackedBy",17,iris,{"font-weight":700,"text-anchor":"middle"});break;
      }
      case "incident/0":{
        title(left,"The first zero on the meter");sampleTrace(left,p=>1.1+1.9*stage(p,.14,.42));
        title(right,"One reading, two clocks");
        row(right,77,"Sample time","10:11:00",rust,194);
        const arrival=add(right,"g"), lag=add(right,"g");
        row(arrival,124,"Capture time","10:12:01",blue,194);reveal(arrival,.52,.63,0);
        line(lag,"M335 71H359V119H335",ochre,2);drawText(lag,377,102,"61 seconds",19,ochre);
        reveal(lag,.61,.73,0);break;
      }
      case "incident/1":{
        title(left,"The alarm travels back from the server");miniChain(left,["API","database","pg-shop"],rust,true);
        title(right,"The outage reaches both entry points");
        travel(right,"M94 101H223L303 67H439",rust,.18,.68);
        travel(right,"M94 101H223L303 132H439",rust,.23,.73);
        spot(right,94,101,"API",rust);circle(right,439,67,7,paper,rust);circle(right,439,132,7,paper,rust);
        drawText(right,426,61,"Gateway",17,evidenceInk,{"text-anchor":"end"});
        drawText(right,419,154,"Web",17,evidenceInk);break;
      }
      case "incident/2":
        title(left,"pg-shop answers at 10:14");sampleTrace(left,p=>5.5+.5*stage(p,.04,.16)+stage(p,.49,.65));
        title(right,"Good minutes needed to clear");ruleMemory(right);break;
      case "dependency/0":
        title(left,"shopcache server load");scaleChart(left,[22,23,85,85],2,100,"%",ochre);
        title(right,"Original workload at 10:06:30");
        nodeRow(right,69,"web","Degraded");nodeRow(right,110,"API","Healthy");nodeRow(right,151,"Gateway","Healthy");break;
      case "dependency/1":{
        title(left,"API deployment · configuration change");
        drawText(left,9,72,"+ CACHE_HOST",20,teal,{"font-family":"Menlo, monospace","font-weight":700});
        hostName(left,108,"shopcache");
        title(right,"Existing Azure inventory entry");
        tag(right,8,68,"Redis",iris,"#e9dffb",79);
        hostName(right,106,"shopcache");
        const underline=line(right,"M10 145H288",teal,4);reveal(underline,.27,.63,.05);break;
      }
      case "dependency/2":{
        title(left,"Two runs · 10:06:30 UTC");
        row(left,76,"Cache meter","85% in both runs",ochre,160);
        row(left,112,"Alarm rule","Same threshold",teal,160);
        row(left,147,"One edit","+ API → shopcache",iris,160);
        title(right,"Only the new dependency differs");
        comparison(right,["API","Gateway"],["Healthy","Healthy"],["Degraded","Degraded"]);break;
      }
      case "azure/0":{
        title(left,"Address in the API setting");hostName(left,91,"offers-cache");
        title(right,"Azure inventory · first run");
        row(right,77,"Known cache","shopcache",iris,185);
        line(right,"M9 94H510","#c1c6c9",1);
        row(right,128,"offers-cache","No matching row",evidenceMuted,185);
        break;
      }
      case "azure/1":{
        title(left,"New row from Azure Resource Graph");
        row(left,76,"name","offers-cache",iris,122);
        row(left,111,"type","microsoft.cache/redis",blue,122);
        drawText(left,9,147,"The type supplies the Redis host suffix.",17,evidenceMuted);
        title(right,"The hostname joins the records");
        travel(right,"M48 97H459",iris,.12,.69);
        spot(right,48,97,"API",blue);spot(right,459,97,"offers-cache",iris);
        tag(right,146,68,"CACHE_HOST match",teal,"#d8f2e6",198);break;
      }
      case "azure/2":{
        title(left,"What the new record supplies");
        row(left,80,"Inventory row","✓  present",teal,207);
        row(left,122,"Linked service","API",blue,207);
        title(right,"Readings for offers-cache");
        line(right,"M21 121H315","#a4a6b3",2,"5 6");
        drawText(right,20,90,"No meter samples",20,evidenceMuted);
        status(right,351,103,"Unknown",146);
        break;
      }
      case "replay/0":{
        title(left,"An edit sent to the Canvas");
        tag(left,9,71,"Health patch",iris,"#e9dffa",139);
        row(left,106,"pg-shop","Healthy → Unhealthy",rust,155);
        row(left,143,"shopcache","Healthy throughout",teal,155);
        title(right,"A gap in source reports");
        [0,1,2,3,4].forEach(i=>{const x=45+i*103;circle(right,x,95,7,i<3?blue:paper,i<3?blue:"#9d9da7");});
        line(right,"M44 95H249",blue,3);line(right,"M261 95H462","#aeb3bc",2,"5 5");
        drawText(right,12,137,"Reports arrive",17,blue);drawText(right,296,137,"Coverage gap",17,evidenceMuted);break;
      }
      case "replay/1":{
        title(left,"Reconstruct a saved version");
        box(left,9,72,147,60,"#e1edf8",blue);
        drawText(left,82,98,"Saved map",18,blue,{"text-anchor":"middle"});
        drawText(left,82,121,"revision 15",16,evidenceMuted,{"text-anchor":"middle"});
        for(let i=0;i<3;i++){
          const x=188+i*114, g=add(left,"g");travel(left,`M${x-27} 101H${x-3}`,iris,.03+i*.17,.2+i*.17);
          box(g,x,76,80,52,"#ede4f9",iris);drawText(g,x+40,107,"+"+(16+i),20,iris,{"text-anchor":"middle","font-weight":700});
          reveal(g,.07+i*.17,.24+i*.17,.23);
        }
        drawText(left,191,150,"Patches in order",16,evidenceMuted);
        title(right,"Database state at each revision");
        const revs=["15","16","17","18"], states=["Healthy","Unhealthy","Unhealthy","Healthy"];
        revs.forEach((v,i)=>{
          const x=14+i*128,g=add(right,"g");
          box(g,x,64,111,50,health[states[i]].pale,health[states[i]].ink);
          drawText(g,x+55,97,health[states[i]].symbol+" "+v,22,health[states[i]].ink,{"text-anchor":"middle","font-weight":700});
          drawText(g,x+55,141,states[i],17,health[states[i]].ink,{"text-anchor":"middle"});
          reveal(g,.05+i*.12,.21+i*.12,.38);
        });break;
      }
      case "replay/2":{
        title(left,"Two calculations read the same input");
        ["Apply edits","Full rebuild"].forEach((name,r)=>{
          const y=75+r*56;drawText(left,8,y+7,name,18,evidenceInk);
          for(let i=0;i<6;i++){
            const tile=box(left,157+i*53,y-16,40,30,r?iris:teal);
            reveal(tile,(r?.25:.03)+(r?.025:.075)*i,(r?.47:.22)+(r?.025:.075)*i,.16);
          }
        });
        title(right,"Compare the final maps");
        const coords=[[6,41],[65,13],[65,68],[130,41]], es=[[0,1],[0,2],[1,3],[2,3]];
        graphLayer(right,26,61,teal,coords,es);graphLayer(right,356,61,iris,coords,es);
        const eq=drawText(right,261,120,"=",47,teal,{"text-anchor":"middle","font-weight":700});reveal(eq,.51,.72,.08);break;
      }
      case "inspect/0":{
        title(left,"Canvas · selected origin at revision 16");
        add(left,"image",{x:0,y:43,width:535,height:121,href:assets+"origin.webp",preserveAspectRatio:"xMidYMid meet"});
        title(right,"Origin or affected caller?");
        row(right,79,"pg-shop","Its own meter tripped",rust,147);
        row(right,127,"web","Depends on the API",blue,147);break;
      }
      case "inspect/1":{
        title(left,"Follow the health result upstream");
        miniChain(left,["Health","Structure","Records"],blue);
        title(right,"The records behind pg-shop");
        row(right,77,"Meter","is_db_alive = 0",rust,158);
        row(right,114,"Service","database",blue,158);
        row(right,151,"Azure server","pg-shop",iris,158);break;
      }
      case "inspect/2":{
        title(left,"Canvas · projection editor");
        add(left,"image",{x:9,y:42,width:505,height:122,href:assets+"editor-kql.webp",preserveAspectRatio:"xMidYMid meet"});
        title(right,"Example preview · outage revision");
        const draft=drawText(right,9,71,"| project name, state",19,iris,{"font-family":"Menlo, monospace"});
        nodeRow(right,111,"pg-shop","Unhealthy");nodeRow(right,151,"web","Unhealthy");
        reveal(draft,.05,.35,.45);
        break;
      }
    }
    return p=>updates.forEach(update=>update(p));
  }
  function mount(host,topic,beat){
    const bounds=host.dataset.assetBase||"assets/";
    const compact=host.dataset.responsive==="true" && matchMedia("(max-width:640px)").matches;
    const evidenceFirst=!compact && host.dataset.layout==="evidence";
    const narrowMap=compact || evidenceFirst;
    const mobileNodes={gateway:[74,68],web:[74,166],api:[241,68],database:[414,68],pg:[414,154],cache:[245,223],storage:[76,263],offers:[414,250]};
    const mobilePaths={ga:"M140 68H174",wa:"M139 157Q155 151 177 86",ad:"M307 68H347",dp:"M414 92V129",
      wc:"M140 176Q189 191 207 202",ws:"M75 190V238",ac:"M242 92V199",ao:"M299 83C335 116 335 193 377 228"};
    const svg=element("svg",{viewBox:compact?"0 0 496 635":"0 0 1160 474",role:"img"});
    host.replaceChildren(svg);
    const svgId="arr-"+topic+"-"+beat;
    const defs=add(svg,"defs");
    const arrow=add(defs,"marker",{id:svgId,viewBox:"0 0 8 8",refX:7,refY:4,markerWidth:5,markerHeight:5,orient:"auto-start-reverse"});
    add(arrow,"path",{d:"M0 0 L8 4 0 8",fill:"#849f98"});
    const overview=add(svg,"g",evidenceFirst?{transform:"translate(696 77) scale(.927)"}:{});
    if(evidenceFirst){
      add(svg,"rect",{x:695,y:2,width:463,height:470,rx:11,fill:ink,stroke:"#53647c"});
      svg.append(overview);
      label(svg,713,47,"Whole-shop context",24,text,{"font-weight":650});
      label(svg,713,428,"Arrows point to the service or resource",18,muted);
      label(svg,713,455,"that each caller needs.",18,muted);
    }
    add(overview,"rect",{x:1,y:1,width:narrowMap?494:1158,height:narrowMap?326:280,rx:11,fill:ink,stroke:"#436067"});
    label(overview,20,23,"SHOP ON AZURE",15,muted,{"letter-spacing":1.5,"font-weight":700});
    const shop=label(overview,narrowMap?479:1137,23,"",15,mint,{"text-anchor":"end"});
    const links={};
    for(const [id,[a,b,route]] of Object.entries(edges)){
      const d=narrowMap?mobilePaths[id]:route;
      const g=add(overview,"g");
      const base=add(g,"path",{d,fill:"none",stroke:"#57736f","stroke-width":2.5,"marker-end":"url(#"+svgId+")"});
      const trace=add(g,"path",{d,fill:"none",stroke:mint,"stroke-width":3.4});
      const pulse=dot(g,5,mint);
      const len=base.getTotalLength();
      trace.setAttribute("stroke-dasharray",len+" "+len);
      links[id]={g,base,trace,pulse,len,a,b};
    }
    const glyphs={};
    for(const [id,[wideX,wideY,name,role,icon]] of Object.entries(nodes)){
      const [x,y]=narrowMap?mobileNodes[id]:[wideX,wideY];
      const g=add(overview,"g",{transform:`translate(${x},${y}) scale(${narrowMap?.9:1})`});
      const rect=add(g,"rect",{x:-73,y:-24,width:146,height:50,rx:9,fill:"#243d40",stroke:"#5c7773"});
      const pict=add(g,"path",{d:icons[icon],transform:"translate(-51 0)",fill:"none",stroke:mint,"stroke-width":2,"stroke-linecap":"round","stroke-linejoin":"round"});
      label(g,-31,-1,name,17,text,{"font-weight":700});
      const state=label(g,-31,17,role,13,muted,{"font-weight":600});
      glyphs[id]={g,rect,pict,state,role};
    }
    // Symbols, labels, borders and fills all carry state; hue is a second cue.
    for(const [i,[name,style]] of Object.entries(health).entries()){
      const x=narrowMap?[16,139,269,395][i]:[586,710,851,1004][i], y=narrowMap?314:269;
      add(overview,"rect",{x,y:y-17,width:23,height:21,rx:5,fill:style.fill,stroke:style.edge});
      label(overview,x+11.5,y-1,style.symbol,16,style.edge,{"text-anchor":"middle","font-weight":700});
      label(overview,x+29,y,name,narrowMap?14:16,text);
    }
    if(!narrowMap)label(overview,20,269,"Arrow direction    caller → dependency",15,muted);
    const insetUpdate=evidenceView(svg,topic,beat,compact,bounds,evidenceFirst);
    const headings=[...svg.querySelectorAll("[data-evidence-heading]")].map(e=>e.textContent);
    svg.setAttribute("aria-label","The shop on Azure. "+headings.join(". ")+". Health states have colors and symbols.");
    const ordered=["pg","database","api","web","gateway"];
    const focus=focusSets[topic][beat], active=routes[topic][beat];
    function seek(progress){
      const p=clamp(progress);
      const flowBack=topic==="incident"||topic==="dependency"&&beat!==1||topic==="replay"&&beat===1;
      const states={};
      let shopState="";
      // Times and paths here correspond to the two recorded replay experiments.
      if(topic==="incident" || topic==="replay"&&beat===1 || topic==="inspect"&&beat===0){
        let failed=0;
        if(topic==="incident"){
          if(beat===0)failed=p>=.42?1:0;
          if(beat===1)failed=1+[.46,.61,.71,.76].filter(point=>p>=point).length;
          if(beat===2)failed=5-Math.floor(stage(p,.62,.87)*5);
        }else if(topic==="replay"){
          failed=p>=.28&&p<.70?5:0;
        }else failed=5;
        for(const [i,id] of ordered.entries())states[id]=i<failed?"Unhealthy":"Healthy";
        states.cache=states.storage="Healthy";shopState=states.web;
      }
      if(topic==="dependency"||topic==="azure"){
        Object.keys(nodes).forEach(id=>states[id]="Healthy");
        states.cache=states.web="Degraded";shopState="Degraded";
        if(topic==="dependency"&&beat===2){
          states.api=p>.25?"Degraded":"Healthy";states.gateway=p>.48?"Degraded":"Healthy";
        }
        if(topic==="azure")states.offers="Unknown";
      }
      shop.textContent=shopState?"Shop   "+health[shopState].symbol+"  "+shopState:"Namespace   shop";
      shop.setAttribute("fill",shopState?health[shopState].edge:muted);
      for(const [id,{g,rect,pict,state,role}] of Object.entries(glyphs)){
        const represented=id!=="offers"||topic==="azure"&&(beat>1||beat===1&&p>.34);
        g.setAttribute("opacity",represented?1:0);
        const status=health[states[id]], selected=focus.includes(id);
        const platform=["gateway","pg","cache","offers","storage"].includes(id);
        const tint=status?.edge||(platform?"#a5d5ff":"#82dbe5");
        const fill=status?.fill||(platform?"#2d4566":"#244552");
        rect.setAttribute("fill",fill);rect.setAttribute("stroke",tint);
        rect.setAttribute("stroke-width",selected?3:1.5);
        pict.setAttribute("stroke",tint);state.setAttribute("fill",tint);
        state.textContent=status?status.symbol+" "+states[id]:role;
      }
      for(const [id,{g,base,trace,pulse,len}] of Object.entries(links)){
        const extra=id==="ac"||id==="ao";
        const show=!extra||id==="ac"&&topic==="dependency"&&beat>=1||id==="ao"&&topic==="azure"&&beat>=1;
        g.setAttribute("opacity",show?1:0);
        if(!show)continue;
        const k=active.indexOf(id);
        const delay=id==="ao"&&beat===1?.52:k<0?0:.035+.09*k;
        const fraction=k<0?0:stage(p,delay,Math.min(.92,delay+.42));
        const addition=extra&&(beat===1);base.setAttribute("opacity",addition?fraction:1);
        const tint=topic==="incident"?beat===2?mint:red:topic==="dependency"?gold:topic==="azure"?sky:mint;
        trace.setAttribute("stroke",tint);
        trace.setAttribute("stroke-dashoffset",(flowBack?-1:1)*len*(1-fraction));
        pulse.setAttribute("opacity",k>=0&&p>delay&&p<.98?1:0);
        if(k>=0){
          const phase=((p-delay)*2.3)%1;const point=base.getPointAtLength(clamp(flowBack?1-phase:phase)*len);
          pulse.setAttribute("cx",point.x);pulse.setAttribute("cy",point.y);pulse.setAttribute("fill",tint);
        }
      }
      insetUpdate(p);
    }
    seek(0);
    return {seek};
  }
  return {mount};
})();
