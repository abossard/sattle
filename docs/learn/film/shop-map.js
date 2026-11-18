// The film and browser lesson use the same map and seekable, deterministic animations.
const ShopMap = (() => {
  const ns="http://www.w3.org/2000/svg";
  const ink="#122c32", text="#e5eee4", muted="#a8bfb9", mint="#a4e9bf", gold="#f8ce7e", red="#ff9a83", sky="#9bcdf1";
  const clamp=(x,a=0,b=1)=>Math.max(a,Math.min(b,x));
  const stage=(p,a,b)=>clamp((p-a)/(b-a));
  const nodes={
    gateway:[101,73,"Gateway","apim-shop","gateway"], web:[101,164,"Web","service","web"],
    api:[364,114,"API","service","api"], database:[618,114,"Database","service","api"],
    pg:[871,72,"pg-shop","Azure PostgreSQL","db"], cache:[871,171,"shopcache","Azure Redis","cache"],
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
  const detail={
    origins:[
      ["Service path","Web request","API call","PostgreSQL"],
      ["Inputs to the lab","Kubernetes watch","Network flows","Azure inventory + meters"],
      ["Two projections","Master graph","workloadStructure","workloadHealth"]],
    rules:[
      ["Team chooses scope","Source access","Namespace: shop","Cluster: aks-synth"],
      ["Team sets database-down","Meter: is_db_alive","1 low minute → trip","2 good minutes → clear"],
      ["Team defines meaning","Observed pod call","Caller needs callee?","Include in health roll-up"]],
    layers:[
      ["Resolve one resource","Inventory: resource ID","One server key","Meter: resource ID"],
      ["Lift pod calls","web · pod 1 / 2 / 3","Group by service","web → api"],
      ["Two kinds of change","Meter samples","Threshold with memory","Graph revision"]],
    incident:[["Meter samples"],["Impact travels back to callers"],["Recovery waits for a second good minute"]],
    dependency:[
      ["Original run · 10:06:30 UTC","Cache: degraded","API: healthy","Gateway: healthy"],
      ["Changed recording · 10:04:00 UTC","API: CACHE_HOST","Match Redis host","Add API → cache"],
      ["Changed run · 10:06:30 UTC","Cache: degraded","API: degraded","Gateway: degraded"]],
    azure:[
      ["What-if · deployment update","API: CACHE_HOST","offers-cache host","Await matching inventory"],
      ["What-if · Azure inventory update","Resource Graph row","Match type + host","Add offers-cache"],
      ["Topology and health","Resource: present","Matching dependency","Health: unknown"]],
    replay:[
      ["Live path","Source envelope","Engine patch","WebSocket → Canvas"],
      ["Static workload export","Revision 15: healthy","Revision 16: unhealthy","Revision 18: healthy"],
      ["Replay check","Same recorded input","Incremental / full rebuild","Compare final graphs"]],
    inspect:[
      ["Canvas · pg-shop selected","Workload tree","Own signal: unhealthy","Inspect its evidence"],
      ["Trace this conclusion","Health projection","Structure projection","Source observations"],
      ["Use the local lab","Change configuration","Add Azure inventory","Compare the results"]]
  };
  const focusSets={origins:[["web","api","pg"],["gateway","web","pg","cache"],["web","api","database","pg"]],rules:[["web","api","database"],["pg"],["web","api"]],layers:[["database","pg"],["web","api"],["pg"]],incident:[["pg"],["pg","database","api","gateway","web"],["pg"]],dependency:[["cache","web"],["api","cache"],["gateway","api","cache"]],azure:[["api"],["offers","api"],["offers"]],replay:[["pg","api"],["pg","database","api","web"],["web","api","pg"]],inspect:[["pg"],["pg","database","api"],["gateway","api","pg","cache"]]};
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
  function mount(host,topic,beat){
    const bounds=host.dataset.assetBase||"assets/";
    const compact=host.dataset.responsive==="true" && matchMedia("(max-width:640px)").matches;
    const mobileNodes={gateway:[74,68],web:[74,166],api:[241,68],database:[414,68],pg:[414,154],cache:[245,223],storage:[76,263],offers:[414,250]};
    const mobilePaths={ga:"M140 68H174",wa:"M139 157Q155 151 177 86",ad:"M307 68H347",dp:"M414 92V129",
      wc:"M140 176Q189 191 207 202",ws:"M75 190V238",ac:"M242 92V199",ao:"M299 83C335 116 335 193 377 228"};
    const svg=element("svg",{viewBox:compact?"0 0 496 379":"0 0 1160 474",role:"img","aria-label":"The whole shop and a detail of "+detail[topic][beat][0]});
    host.replaceChildren(svg);
    const svgId="arr-"+topic+"-"+beat;
    const defs=add(svg,"defs");
    const arrow=add(defs,"marker",{id:svgId,viewBox:"0 0 8 8",refX:7,refY:4,markerWidth:5,markerHeight:5,orient:"auto-start-reverse"});
    add(arrow,"path",{d:"M0 0 L8 4 0 8",fill:"#849f98"});
    add(svg,"rect",{x:1,y:1,width:compact?494:1158,height:compact?306:278,rx:11,fill:ink,stroke:"#436067"});
    label(svg,21,23,"SHOP ON AZURE   /   WORKLOAD MAP",14,muted,{"letter-spacing":1.8});
    const shop=label(svg,compact?479:1137,23,"",15,mint,{"text-anchor":"end"});
    const links={};
    for(const [id,[a,b,route]] of Object.entries(edges)){
      const d=compact?mobilePaths[id]:route;
      const g=add(svg,"g");
      const base=add(g,"path",{d,fill:"none",stroke:"#57736f","stroke-width":2.5,"marker-end":"url(#"+svgId+")"});
      const trace=add(g,"path",{d,fill:"none",stroke:mint,"stroke-width":3.4});
      const pulse=dot(g,5,mint);
      const len=base.getTotalLength();
      trace.setAttribute("stroke-dasharray",len+" "+len);
      links[id]={g,base,trace,pulse,len,a,b};
    }
    const glyphs={};
    for(const [id,[wideX,wideY,name,role,icon]] of Object.entries(nodes)){
      const [x,y]=compact?mobileNodes[id]:[wideX,wideY];
      const g=add(svg,"g",{transform:`translate(${x},${y}) scale(${compact?.9:1})`});
      const rect=add(g,"rect",{x:-73,y:-24,width:146,height:50,rx:9,fill:"#243d40",stroke:"#5c7773"});
      const pict=add(g,"path",{d:icons[icon],transform:"translate(-51 0)",fill:"none",stroke:mint,"stroke-width":2,"stroke-linecap":"round","stroke-linejoin":"round"});
      label(g,-31,-1,name,17,text,{"font-weight":650});
      const state=label(g,-31,16,role,12,muted);
      glyphs[id]={g,rect,pict,state,role};
    }
    label(svg,20,compact?298:269,"Arrows point from a caller to what it needs.",14,muted);
    const source=label(svg,compact?483:1137,compact?298:269,"",14,muted,{"text-anchor":"end"});
    const firstDetail=add(svg,"rect",{x:1,y:292,width:1158,height:181,rx:11,fill:"#eef0e2"});
    const lineColor="#4f7770", paperInk="#19383c";
    const words=detail[topic][beat];
    label(svg,25,321,words[0],19,paperInk,{"font-weight":650});
    let insetUpdate;
    if(topic==="incident" || (topic==="layers" && beat===2) || (topic==="rules" && beat===1)){
      const vals=[1,1,1,0,0,0,1,1,1,1];
      const x=i=>112+i*89, y=v=>v?354:406;
      const path="M"+x(0)+" "+y(vals[0])+vals.slice(1).map((v,k)=>"H"+x(k+1)+"V"+y(v)).join("");
      for(const [i,v] of [[0,1],[1,0]]){label(svg,58,y(v)+5,""+v,18,paperInk);add(svg,"path",{d:"M86 "+y(v)+"H944",stroke:"#bdd0bd","stroke-dasharray":"3 5"});}
      add(svg,"path",{d:path,fill:"none",stroke:"#286452","stroke-width":4});
      for(const i of [0,3,6,9])label(svg,x(i),446,"10:"+String(8+i).padStart(2,"0"),16,paperInk,{"text-anchor":"middle"});
      const marker=dot(svg,8,"#b54932");
      const ruler=add(svg,"path",{stroke:"#8f6546","stroke-width":1.5,"stroke-dasharray":"4 4"});
      const reading=label(svg,1048,374,"",28,paperInk,{"text-anchor":"middle"});
      const health=label(svg,1048,406,"",17,paperInk,{"text-anchor":"middle"});
      insetUpdate=p=>{
        const range=topic==="incident"?[[1,3.8],[3.8,5.7],[5.7,8.4]][beat]:[0,8.5];
        const col=range[0]+(range[1]-range[0])*p,i=Math.floor(col);
        marker.setAttribute("cx",x(col));marker.setAttribute("cy",y(vals[i]));
        ruler.setAttribute("d",`M${x(col)} 341V419`);
        reading.textContent="10:"+String(i+8).padStart(2,"0");
        health.textContent=i>=3&&i<7?"Unhealthy":"Healthy";
      };
    }else if((topic==="layers"&&beat<2)||(topic==="dependency"&&beat===1)||(topic==="azure"&&beat===1)||(topic==="origins"&&beat===1)){
      const content = topic==="layers" ? (beat===0?["Inventory ID","Meter target ID","Same key","One pg-shop node"]:["Pod A → API pod","Pod B → API pod","Group by service","One web → API link"])
        : topic==="dependency"?["API: CACHE_HOST","Azure cache host","Match host","API → shopcache"]
        : topic==="azure"?["Existing API setting","New Azure inventory row","Match Redis type + host","API → offers-cache"]
        : ["Kubernetes services","Azure inventory + meters","Adapters","Typed observations"];
      const paths=["M225 356 C342 356 333 388 472 388","M225 423 C342 423 333 388 472 388","M681 388H831"];
      const feeds=paths.map((d,i)=>{
        const path=add(svg,"path",{d,fill:"none",stroke:"#75958c","stroke-width":2.5});
        const dotEl=dot(svg,7,i===1?"#9a621f":"#167052");
        return {path,len:path.getTotalLength(),dotEl};
      });
      for(const [i,name] of content.entries()){
        const x=[123,123,578,967][i], y=[356,423,388,388][i], w=[201,201,216,273][i];
        add(svg,"rect",{x:x-w/2,y:y-19,width:w,height:40,rx:8,stroke:"#8aaba0",fill:i===3?"#c2e5c7":"#f6f8ec"});
        label(svg,x,y+6,name,i===2?17:18,paperInk,{"text-anchor":"middle","font-weight":600});
      }
      insetUpdate=p=>{
        for(const [i,f] of feeds.entries()){
          const phase=(p*2.15)%1, q=i<2?stage(phase,i*.05,.54):stage(phase,.54,.95);
          const pos=f.path.getPointAtLength(q*f.len);
          f.dotEl.setAttribute("cx",pos.x);f.dotEl.setAttribute("cy",pos.y);
          f.dotEl.setAttribute("opacity",q>0&&q<1?1:0);
        }
      };
    }else if(topic==="replay"&&beat===2){
      label(svg,28,369,"Stream updates",19,paperInk);label(svg,28,432,"Full rebuild",19,paperInk);
      const blocks=[];
      for(let r=0;r<2;r++)for(let i=0;i<8;i++){
        blocks.push(add(svg,"rect",{x:218+i*69,y:344+r*60,width:53,height:29,rx:5,fill:"#c8d6ca"}));
      }
      const verdict=label(svg,813,401,"",22,paperInk,{"font-weight":650});
      insetUpdate=p=>{blocks.forEach((e,i)=>e.setAttribute("fill",p>(i<8?i*.075:.18+(i%8)*.07)?"#39826c":"#c8d6ca"));verdict.textContent=p>.73?"Equal final graph":"Comparing…";};
    }else if(topic==="inspect"&&beat===0){
      add(svg,"image",{x:40,y:333,width:372,height:122,href:bounds+"origin.webp",preserveAspectRatio:"xMidYMid meet"});
      add(svg,"image",{x:457,y:321,width:196,height:142,href:bounds+"inspector.webp",preserveAspectRatio:"xMidYMid meet"});
      label(svg,683,377,"pg-shop is the origin",27,paperInk);label(svg,683,416,"Own state:  Healthy → Unhealthy",21,lineColor);
      insetUpdate=()=>{};
    }else{
      // Three stations show the concrete transformation underneath the estate map.
      const cards=[], centers=[194,580,966];
      for(let i=0;i<3;i++){
        const x=centers[i], group=add(svg,"g");
        const box=add(group,"rect",{x:x-161,y:348,width:322,height:80,rx:9,stroke:"#a9beb0",fill:"#f7f8ee"});
        const name=label(group,x,395,words[i+1],words[i+1].length>22?19:22,paperInk,{"text-anchor":"middle","font-weight":600});
        cards.push({box,name});
        if(i<2)add(svg,"path",{d:`M${x+161} 388h62m-8 -6 8 6-8 6`,fill:"none",stroke:lineColor,"stroke-width":2});
      }
      const pulse=dot(svg,6,"#0d7353");
      const track=add(svg,"path",{d:"M48 447H1110",stroke:"#aac4b5","stroke-width":2});
      svg.insertBefore(track,pulse);
      insetUpdate=p=>{
        const distance=stage(p,.04,.9);
        pulse.setAttribute("cx",48+distance*1062);pulse.setAttribute("cy",447);
        cards.forEach(({box},i)=>{box.setAttribute("fill",distance>=[0,.42,.8][i]?"#c9e8cd":"#f7f8ee");});
      };
    }
    if(compact){
      for(let child=firstDetail;child;child=child.nextElementSibling)child.setAttribute("display","none");
      add(svg,"rect",{x:1,y:314,width:494,height:64,rx:8,fill:"#eef0e2"});
      label(svg,14,340,words[0],16,paperInk,{"font-weight":650});
      const live=label(svg,14,364,"",17,paperInk);
      const originalUpdate=insetUpdate;
      insetUpdate=p=>{
        originalUpdate(p);
        live.textContent=words.length>1?words[1+Math.min(2,Math.floor(p*3))]:
          (topic==="incident"&&beat===2?"10:14 first good minute → 10:15 clears":"Meter drops → rule trips → callers affected");
      };
    }
    const ordered=["pg","database","api","web","gateway"];
    const focus=focusSets[topic][beat], active=routes[topic][beat];
    const counter=topic==="dependency"&&beat>0||topic==="azure";
    source.textContent=compact?"":counter?"Modified replay · compared with original":"Synthetic estate · Sattle";
    function seek(progress){
      const p=clamp(progress);
      const flowBack=topic==="incident"||topic==="dependency"&&beat!==1||topic==="replay"&&beat===1;
      const states={};
      let shopState="";
      if(topic==="incident"||topic==="replay"&&beat===1){
        const first=topic==="incident"?beat===0?stage(p,.68,1)*.17:beat===1?.17+stage(p,.05,.77)*.83:1:stage(p,.12,.42);
        const recovery=topic==="incident"?beat===2?stage(p,.48,.90):0:stage(p,.64,.90);
        for(const [k,id] of ordered.entries())states[id]=first>(k+.1)/5&&recovery<(k+.45)/5?"Unhealthy":"Healthy";
        states.cache=states.storage="Healthy";shopState=states.web;
      }
      if(topic==="dependency"){
        Object.keys(nodes).forEach(id=>states[id]="Healthy");
        states.cache=states.web="Degraded";shopState="Degraded";
        if(beat===2){states.api=p>.23?"Degraded":"Healthy";states.gateway=p>.50?"Degraded":"Healthy";}
      }
      if(topic==="azure"){states.offers="Unknown";}
      shop.textContent=compact?(shopState||"shop"):shopState?"Shop health  /  "+shopState:"Scope  /  namespace shop";
      for(const [id,{g,rect,pict,state,role}] of Object.entries(glyphs)){
        const represented=id!=="offers"||topic==="azure"&&(beat>1||beat===1&&p>.32);
        g.setAttribute("opacity",represented?1:0);
        const status=states[id], tint=status==="Unhealthy"?red:status==="Degraded"?gold:status==="Unknown"?sky:focus.includes(id)?mint:"#a7bdb4";
        rect.setAttribute("fill",status==="Unhealthy"?"#633b36":status==="Degraded"?"#594b2f":focus.includes(id)?"#265349":"#243d40");
        rect.setAttribute("stroke",tint);pict.setAttribute("stroke",tint);state.setAttribute("fill",tint);state.textContent=status||role;
      }
      for(const [id,{g,base,trace,pulse,len}] of Object.entries(links)){
        const extra=id==="ac"||id==="ao";
        const show=!extra||id==="ac"&&topic==="dependency"&&beat>=1||id==="ao"&&topic==="azure"&&beat>=1;
        g.setAttribute("opacity",show?1:0);
        if(!show)continue;
        const k=active.indexOf(id), delay=k<0?0:.035+.09*k;
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
