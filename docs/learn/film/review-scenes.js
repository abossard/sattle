// Both views use the synthetic estate's workload export. The revision-zero
// snapshot is empty; revisions 16 and 18 each have seven entity edits.
const ReviewScenes = (() => {
  const mono = "Menlo, Consolas, monospace";
  const paper = "#f9f4e4";
  const navy = "#122637";
  const soft = "#c2d7db";
  const mint = "#85e9ae";
  const coral = "#ff93a3";
  const gold = "#ffd378";

  function replay({g, el, txt, line, box, dot, stage, W, H, mobile, beat}) {
    const frames = [
      {rev:15, good:true}, {rev:16, good:false},
      {rev:17, good:false}, {rev:18, good:true}
    ];
    const title = (parent,x,y,value,size=17,fill=soft,extra={}) =>
      txt(parent,x,y,value,{"font-size":size,fill,...extra});
    const curve = (parent,d,color,extra={}) =>
      el(parent,"path",{d,fill:"none",stroke:color,"stroke-width":3,"stroke-linecap":"round",...extra});

    box(g,0,0,W,H,{rx:14,fill:navy});
    title(g,22,31,"WORKLOAD HISTORY",mobile?16:18,gold,{"letter-spacing":2,"font-weight":800});
    title(g,W-22,31,"pg-shop",mobile?18:21,"#ffffff",{"text-anchor":"end","font-weight":750});

    // The desktop filmstrip turns into a vertical contact sheet on a phone.
    const left = mobile?30:22, top = mobile?60:73;
    const frameWidth = mobile?W-60:(W-44)/4, frameHeight = mobile?73:145;
    const stripHeight = mobile?292:171;
    box(g,mobile?17:14,top-12,W-(mobile?34:28),stripHeight+6,{rx:8,fill:"#243e4f"});
    const holeCount = mobile?17:38;
    for(let i=0;i<holeCount;i++){
      if(mobile){
        const y=top-6+i*17.5;
        box(g,19,y,6,9,{rx:2,fill:"#91a7ae"});box(g,W-25,y,6,9,{rx:2,fill:"#91a7ae"});
      }else{
        const x=25+i*(W-66)/(holeCount-1);
        box(g,x,top-7,13,5,{rx:2,fill:"#adc1c5"});box(g,x,top+frameHeight+6,13,5,{rx:2,fill:"#adc1c5"});
      }
    }
    const bright = [], locations = [];
    frames.forEach(({rev,good},i)=>{
      const x=mobile?left:left+i*frameWidth;
      const y=mobile?top+i*frameHeight:top;
      const w=mobile?frameWidth:frameWidth-5, h=frameHeight-3;
      box(g,x,y,w,h,{rx:4,fill:"#162d3a"});
      bright.push(box(g,x+1,y+1,w-2,h-2,{rx:4,fill:"none",stroke:gold,"stroke-width":3}));
      const color=good?mint:coral;
      if(mobile){
        title(g,x+12,y+27,"r"+rev,22,"#ffffff",{"font-family":mono,"font-weight":750});
        title(g,x+12,y+53,good?"✓ healthy":"× unhealthy",16,color,{"font-weight":750});
      }else{
        title(g,x+13,y+31,"r"+rev,25,"#ffffff",{"font-family":mono,"font-weight":800});
        title(g,x+w-13,y+28,good?"✓ Healthy":"× Unhealthy",17,color,{"text-anchor":"end","font-weight":750});
      }
      // These three nodes are a dependency path. Cache is a separate,
      // unchanged resource and is intentionally outside this miniature.
      const nx=mobile?[x+173,x+257,x+345]:[x+42,x+w/2,x+w-42];
      const ny=mobile?y+26:y+79;
      line(g,nx[0]+10,ny,nx[2]-10,ny,{stroke:"#7e9da8","stroke-width":2});
      for(const [j,name] of ["API","database","pg-shop"].entries()){
        dot(g,nx[j],ny,9,{fill:good?"#1d614b":"#853a48",stroke:color,"stroke-width":2});
        if(good)curve(g,`M${nx[j]-4} ${ny}l3 3 5-6`,mint,{"stroke-width":1.7});
        else curve(g,`M${nx[j]-3} ${ny-3}l6 6m0-6-6 6`,coral,{"stroke-width":1.7});
        title(g,nx[j],ny+(mobile?24:30),name,mobile?15:17,"#e2edf0",{"text-anchor":"middle"});
      }
      locations.push(mobile?[x-5,y+h/2]:[x+w/2,y-14]);
    });
    const playhead=el(g,"path",{d:mobile?"M-9 -6L0 0 -9 6Z":"M-7 -7H7L0 1Z",fill:gold});

    const routeY = mobile?385:267;
    const routeStart=mobile?37:55, routeEnd=W-(mobile?38:62);
    const routeWidth=routeEnd-routeStart;
    const section = title(g,mobile?25:28,routeY-10,"",mobile?16:19,"#ffffff",{"font-weight":750});
    const compare=beat===2;
    const inputLabel=mobile?"Records · clock · rules":"Same records, clock events and rules";
    const first=routeStart+6, last=routeEnd-6;
    let bead, fastBead;
    if(compare){
      const sy=routeY+(mobile?41:50), peak=mobile?18:24;
      const incremental=curve(g,`M${first} ${sy}C${first+routeWidth*.24} ${sy},${first+routeWidth*.23} ${sy-peak},${first+routeWidth*.45} ${sy-peak}S${last-routeWidth*.22} ${sy},${last} ${sy}`,mint);
      const rebuilt=curve(g,`M${first} ${sy}C${first+routeWidth*.24} ${sy},${first+routeWidth*.23} ${sy+peak},${first+routeWidth*.45} ${sy+peak}S${last-routeWidth*.22} ${sy},${last} ${sy}`,"#a2baff");
      dot(g,first,sy,6,{fill:gold});dot(g,last,sy,7,{fill:gold});
      title(g,(first+last)/2,sy-peak-6,"Update as records arrive",mobile?14:17,mint,{"text-anchor":"middle"});
      title(g,(first+last)/2,sy+peak+18,"Rebuild from scratch",mobile?14:17,"#bacaff",{"text-anchor":"middle"});
      title(g,first,sy+peak+(mobile?37:46),inputLabel,mobile?14:17,soft);
      title(g,last,sy+peak+(mobile?37:46),"Same graph",mobile?14:17,soft,{"text-anchor":"end"});
      bead=dot(g,first,sy,5,{fill:"#fff"});
      fastBead=dot(g,first,sy,5,{fill:"#fff"});
      section.textContent="Two routes through the same input";
      return p=>{
        const t=stage(p,.12,.76);
        const index=Math.min(3,Math.floor(t*4));
        bright.forEach((r,i)=>r.setAttribute("opacity",i===index?1:0));
        playhead.setAttribute("transform",`translate(${locations[index]})`);
        for(const [path,marker] of [[incremental,bead],[rebuilt,fastBead]]){
          const point=path.getPointAtLength(path.getTotalLength()*t);
          marker.setAttribute("cx",point.x);marker.setAttribute("cy",point.y);
        }
      };
    }
    const railY=routeY+(mobile?41:57);
    const stops=[first, first+routeWidth*.40,first+routeWidth*.57,first+routeWidth*.74,last];
    line(g,first,railY,last,railY,{stroke:"#6a8792","stroke-width":3});
    const track=line(g,first,railY,first,railY,{stroke:gold,"stroke-width":4});
    const names=["r0","… 15","+16","+17","18"];
    stops.forEach((x,i)=>{
      dot(g,x,railY,i===0||i===4?9:6,{fill:navy,stroke:i===4?mint:gold,"stroke-width":2});
      title(g,x,railY-14,names[i],mobile?15:18,soft,{"font-family":mono,"text-anchor":"middle"});
    });
    bead=dot(g,first,railY,5,{fill:"#fff"});
    section.textContent=beat===0?"A patch travels from engine to Canvas":"Ordered patches rebuild a saved map.";
    const note=title(g,22,H-18,"",mobile?15:19,gold,{"font-weight":650});
    return p=>{
      const t=stage(p,.10,.78);
      const progress=beat===0?1+Math.min(1,t*2):Math.min(2,t*4.6)+2*stage(t,.55,.91);
      const stop=Math.floor(progress), fraction=progress-stop;
      const pos=stops[stop]+(stops[Math.min(4,stop+1)]-stops[stop])*fraction;
      const index=stop-1;
      bright.forEach((r,i)=>r.setAttribute("opacity",i===index?1:0));
      playhead.setAttribute("opacity",index<0?0:1);
      if(index>=0)playhead.setAttribute("transform",`translate(${locations[index]})`);
      track.setAttribute("x2",pos);bead.setAttribute("cx",pos);
      if(beat===0)note.textContent=t<.75?"Patch 15 → 16 carries seven edits.":"A reporting gap leaves the last known resource in place.";
      else note.textContent=index<1?"The exported snapshot starts empty.":index===3?"Revision 18 restores the database and its callers.":"Revisions 16 and 17 retain the database alarm.";
    };
  }

  function inspect({g,el,txt,line,box,dot,stage,colors,W,H,mobile,beat,assetBase}) {
    const {ink,muted,red,blue,purple,teal}=colors;
    const text=(group,x,y,value,size,fill=ink,attrs={})=>
      txt(group,x,y,value,{"font-size":size,fill,...attrs});
    const stroke=(group,d,color,width=3,extra={})=>
      el(group,"path",{d,fill:"none",stroke:color,"stroke-width":width,"stroke-linecap":"round",...extra});
    const heading=(group,x,y,name,color=muted)=>
      text(group,x,y,name,mobile?14:16,color,{"font-weight":800,"letter-spacing":1.3});
    const fx=mobile?104:481, fy=mobile?191:142, radius=mobile?58:85;
    const trace=[];
    function pencil(d,color,lo,hi){
      stroke(g,d,"#c6beb0",2);
      const foreground=stroke(g,d,color,3.5);
      const length=foreground.getTotalLength();
      foreground.setAttribute("stroke-dasharray",length+" "+length);
      trace.push(p=>{
        const t=stage(p,lo,hi);
        foreground.setAttribute("stroke-dashoffset",length*(1-t));
      });
    }
    box(g,0,0,W,H,{rx:12,fill:paper});
    // A ruled evidence sheet keeps the original symptom, its origin and its
    // source records on the same physical page throughout the investigation.
    const tear=mobile?282:258;
    for(let y=tear+22;y<H;y+=27)line(g,18,y,W-18,y,{stroke:"#e2dcca","stroke-width":1});
    heading(g,22,29,"CANVAS CASE FILE");
    text(g,W-22,29,"WORKLOAD  /  r16",mobile?15:17,purple,{"font-family":mono,"text-anchor":"end","font-weight":750});
    line(g,21,40,W-21,40,{stroke:"#bdb4a1","stroke-width":1.5});

    const chain=mobile?[[42,91],[157,91],[289,91]]:[[59,109],[176,163],[299,112]];
    const [web,api,db]=chain;
    pencil(`M${web}L${api}L${db}`,red,.04,.45);
    const toOrigin=mobile?`M${db}C${db[0]} 135,${fx+86} 133,${fx+39} ${fy-38}`:
      `M${db}C${db[0]+47} ${db[1]},${fx-radius-25} ${fy},${fx-radius} ${fy}`;
    pencil(toOrigin,red,.35,.60);
    chain.forEach(([x,y],i)=>{
      dot(g,x,y,10,{fill:"#f5cfd0",stroke:red,"stroke-width":2});
      stroke(g,`M${x-3.5} ${y-3.5}l7 7m0-7-7 7`,red,2);
      text(g,x,y-(mobile?18:23),["web","API","database"][i],mobile?17:21,ink,{"font-weight":700,"text-anchor":"middle"});
    });
    if(!mobile)text(g,32,220,"Each caller depends on the next service.",18,muted);
    const branchX=mobile?226:719, topY=mobile?153:94, lowerY=mobile?226:172;
    const branchFrom=fx+radius-5;
    pencil(`M${branchFrom} ${fy}C${branchFrom+32} ${fy},${branchX-51} ${topY},${branchX} ${topY}`,purple,.21,.57);
    pencil(`M${branchFrom} ${fy}C${branchFrom+29} ${fy},${branchX-49} ${lowerY},${branchX} ${lowerY}`,blue,.44,.77);
    // The magnifier is centered on the component whose own signal fired.
    stroke(g,`M${fx+radius*.65} ${fy+radius*.67}l${mobile?24:39} ${mobile?22:34}`,"#85613c",mobile?10:14);
    dot(g,fx,fy,radius,{fill:"#f9ddd2",stroke:"#84512f","stroke-width":5});
    const lens=dot(g,fx,fy,radius-7,{fill:"none",stroke:red,"stroke-width":2});
    text(g,fx,fy-(mobile?12:20),"ORIGIN",mobile?13:16,red,{"text-anchor":"middle","font-weight":800,"letter-spacing":2});
    text(g,fx,fy+(mobile?12:14),"pg-shop",mobile?24:34,ink,{"text-anchor":"middle","font-weight":800,"font-family":"Georgia, serif"});
    text(g,fx,fy+(mobile?31:41),"own alarm",mobile?13:18,red,{"text-anchor":"middle","font-weight":700});

    // Distinguish evidence of the outage from evidence supporting the link.
    for(const [i,y,c,kicker,detail] of [
      [0,topY,purple,"METER RECORD","is_db_alive = 0"],
      [1,lowerY,blue,"SERVICE RECORD","database → pg-shop"]
    ]){
      dot(g,branchX,y,5,{fill:c});
      heading(g,branchX+13,y-7,kicker,c);
      text(g,branchX+13,y+17,detail,mobile?16:22,ink,{"font-family":i===0?mono:"Arial, sans-serif","font-weight":650});
      if(!mobile)text(g,branchX+13,y+42,i===0?"10:11 UTC · first low minute":"Kubernetes ExternalName service",17,muted);
    }
    line(g,21,tear,W-21,tear,{stroke:"#a79d89","stroke-width":1.5,"stroke-dasharray":"6 5"});
    const headingY=tear+26;
    if(beat===0){
      heading(g,24,headingY,"EXHIBIT  /  WORKLOAD TREE",red);
      // Crop the original Canvas image to its pg-shop row. Preserve the
      // actual origin marker and the type and name alongside it.
      const clip=el(g,"svg",{
        x:mobile?22:27,y:headingY+(mobile?18:13),
        width:mobile?430:790,height:mobile?64:76,
        viewBox:mobile?"67 111 359 38":"65 111 505 38",preserveAspectRatio:"xMinYMid meet"
      });
      el(clip,"image",{x:0,y:0,width:796,height:251,href:assetBase+"origin.webp"});
      text(g,24,H-19,"The web service inherits the failure from pg-shop.",mobile?16:20,ink,{"font-weight":650});
    }else if(beat===1){
      heading(g,24,headingY,"EXHIBIT  /  THE NAMES BEHIND THE LINK",blue);
      const x=mobile?26:37, y=headingY+(mobile?32:29);
      text(g,x,y,"Kubernetes externalName",mobile?15:17,muted,{"font-weight":650});
      text(g,x,y+(mobile?27:26),"pg-shop.postgres.database.azure.com",mobile?17:23,ink,{"font-family":mono});
      const right=mobile?x:W*.54, ry=mobile?y+69:y;
      text(g,right,ry,"Azure inventory ID ends in",mobile?15:17,muted,{"font-weight":650});
      text(g,right,ry+26,"…/flexibleservers/pg-shop",mobile?17:23,ink,{"font-family":mono});
      const underline=stroke(g,`M${x} ${y+31}h${mobile?72:99}`,blue,2);
      trace.push(p=>underline.setAttribute("opacity",stage(p,.36,.64)));
    }else{
      heading(g,24,headingY,"EXHIBIT  /  QUERY DRAFT",teal);
      const x=mobile?26:36, y=headingY+(mobile?34:33);
      text(g,x,y,'graph("structure")',mobile?17:21,purple,{"font-family":mono});
      text(g,x,y+(mobile?25:27),'| graph-match (c) where c.name == "pg-shop"',mobile?15:20,ink,{"font-family":mono});
      text(g,x,y+(mobile?50:54),'  project name = c.name',mobile?16:20,ink,{"font-family":mono});
      const output=el(g,"g");
      const ox=mobile?x:W*.67, oy=mobile?y+94:y+4;
      text(output,ox,oy,"Preview row",mobile?16:19,teal,{"font-weight":750});
      text(output,mobile?ox+136:ox,mobile?oy:oy+34,"name   pg-shop",mobile?17:22,ink,{"font-family":mono});
      trace.push(p=>output.setAttribute("opacity",.2+.8*stage(p,.43,.7)));
    }
    return p=>{
      const readTrace=beat===0?p:1;
      trace.forEach((fn,i)=>fn(i<2?readTrace:beat===0?.08:p));
      lens.setAttribute("stroke-width",2+.8*Math.sin(Math.PI*stage(p,.19,.62)));
    };
  }

  return {replay,inspect};
})();
