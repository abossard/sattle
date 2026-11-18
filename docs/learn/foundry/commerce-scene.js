// An order docket stays on the desk as the parcel, events and money take separate paths.
function commerceScene({g,el,txt,box,line,stage,W,H,mobile,beat}) {
  const ink="#203b43", muted="#50636a", teal="#116658", ochre="#865215", violet="#695182", red="#8b3d40";
  const text=(parent,x,y,s,size=18,fill=ink,weight=550,anchor="start")=>
    txt(parent,x,y,s,{"font-size":size,fill,"font-weight":weight,"text-anchor":anchor});
  const path=(parent,d,attrs)=>el(parent,"path",{d,...attrs});
  const docketW=mobile?W-22:270, docketH=mobile?91:329, x=mobile?11:18,y=mobile?9:33;
  const notch=9,teeth=Array.from({length:Math.floor(docketW/(2*notch))},(_,i)=>
    (x+docketW-(i*2+1)*notch)+","+(y+docketH-5)+" "+(x+docketW-(i*2+2)*notch)+","+(y+docketH)).join(" ");
  el(g,"polygon",{points:[x+","+y,(x+docketW)+","+y,(x+docketW)+","+(y+docketH),teeth,x+","+(y+docketH)].join(" "),fill:"#fffaf0",stroke:"#b9ac92","stroke-width":1.6});
  text(g,x+17,y+(mobile?23:34),"ORDER DOCKET",mobile?14:15,muted,700);
  text(g,x+17,y+(mobile?62:94),"O-104",mobile?33:45,ink,720);
  if(mobile) {
    text(g,x+docketW-18,y+31,"$80 merchandise",18,ink,650,"end");
    text(g,x+docketW-18,y+65,"Return R-104 · two items",17,muted,530,"end");
  } else {
    line(g,x+17,y+117,x+docketW-17,y+117,{stroke:"#a3967d","stroke-dasharray":"4 4"});
    text(g,x+17,y+155,"Sale",17,muted);text(g,x+docketW-18,y+155,"$80",25,ink,700,"end");
    text(g,x+17,y+195,"Return",17,muted);text(g,x+docketW-18,y+195,"R-104",22,ink,700,"end");
    text(g,x+17,y+234,"Returned items",17,muted);text(g,x+docketW-18,y+234,"2",23,ink,700,"end");
    text(g,x+17,y+296,["Packing record","Event routing","Settled amounts"][beat],18,teal,700);
  }
  const left=mobile?19:320, right=W-(mobile?20:25), top=mobile?121:35, wide=right-left;
  const header=["From the shelf to the carrier","One order, separate subscribers","Which amount belongs in sales?"][beat];
  text(g,left,top+21,header,mobile?24:30,ink,700);
  const motions=[];
  if(beat===0) {
    const cx=[.13,.50,.85].map(f=>left+f*wide), cy=top+(mobile?148:125);
    line(g,cx[0],cy,cx[2]+9,cy,{stroke:"#aaa18d","stroke-width":5});
    ["Reserve stock","Pack parcel","Hand to carrier"].forEach((name,i)=>{
      const px=cx[i];
      el(g,"circle",{cx:px,cy,r:11,fill:"#fffaf0",stroke:teal,"stroke-width":3});
      text(g,px,cy+49,name,mobile?16:21,ink,690,"middle");
      const details=mobile?["Reserved","Packed","Tracking ID"]:["Order lines reserved","Warehouse scans items","Tracking ID issued"];
      text(g,px,cy+78,details[i],mobile?14:17,muted,500,"middle");
    });
    const parcel=el(g,"g");box(parcel,-18,-34,36,29,{fill:"#d9984a",stroke:ochre,"stroke-width":2,rx:3});box(parcel,-3,-33,6,17,{fill:"#fae7b5"});
    text(g,left,top+(mobile?301:286),"The carrier scan links the physical parcel to O-104.",mobile?16:21,teal,650);
    motions.push(p=>parcel.setAttribute("transform","translate("+(cx[0]+(cx[2]-cx[0])*stage(p,.12,.75))+" "+cy+")"));
  } else if(beat===1) {
    const y1=top+(mobile?82:74), y2=top+(mobile?182:176);
    const rows=[
      [y1,"Support","Carrier scan → CRM","The revised date reaches the support case.",teal],
      [y2,"Marketing","Order event → journey","Opt-out suppresses the promotion.",violet]
    ];
    for(const [i,[ry,l,r,detail,color]] of rows.entries()){
      text(g,left,ry,l,mobile?17:19,color,700);
      text(g,mobile?left:left+155,ry+(mobile?29:0),r,mobile?22:24,ink,650);
      line(g,left,ry+(mobile?39:19),right,ry+(mobile?39:19),{stroke:color,"stroke-opacity":.43});
      text(g,left,ry+(mobile?63:51),detail,mobile?16:20,muted);
      const mark=el(g,"circle",{r:5,cy:ry+(mobile?39:19),fill:color});
      motions.push(p=>mark.setAttribute("cx",left+(wide-8)*stage(p,.07+i*.25,.46+i*.31)));
    }
    text(g,left,top+(mobile?302:283),mobile?"Function → Service Bus → Logic Apps → Journeys":"Azure Function checks event IDs → Service Bus → Logic Apps → Journeys",mobile?16:19,ochre,650);
  } else {
    const entries=[["Items sold",80,"#377989"],["Settled refund",30,"#9c5a3e"],["Merchandise kept",50,"#2b765e"]];
    entries.forEach(([s,n,color],i)=>{
      const by=top+(mobile?85:82)+i*(mobile?76:70);
      const barX=mobile?left:left+188, barY=by+(mobile?11:-19), avail=mobile?wide-68:wide-281;
      text(g,left,by,s,mobile?18:20,ink,600);box(g,barX,barY,avail,20,{fill:"#e0d8c7",rx:3});
      const bar=box(g,barX,barY,0,20,{fill:color,rx:3});
      text(g,right,by+(mobile?26:0),"$"+n,24,color,720,"end");
      motions.push(p=>bar.setAttribute("width",avail*n/80*stage(p,.04+i*.12,.35+i*.16)));
    });
    text(g,left,top+(mobile?305:294),"$80 − ($18 + $12) = $50",mobile?24:30,teal,700);
    text(g,left,top+(mobile?337:325),"Merchandise only · shipping and tax excluded",mobile?16:18,muted);
  }
  return p=>motions.forEach(update=>update(p));
}
