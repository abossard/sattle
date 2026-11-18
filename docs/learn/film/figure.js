// Each chapter owns a figure. The film and guide share its drawing and seek function.
function createChapterFigures(chapters, drawings, caseLabel) {
  const ns = "http://www.w3.org/2000/svg";
  const colors = {
    ink:"#172d3b", muted:"#466071", paper:"#fbf7ec", blue:"#126388", teal:"#087165",
    purple:"#674795", amber:"#8b5206", red:"#a12c4a", green:"#166045", rule:"#b3c4c3"
  };
  const mounted = new WeakMap();
  function el(parent, tag, attrs={}, text="") {
    const node = document.createElementNS(ns, tag);
    for (const [name,value] of Object.entries(attrs)) node.setAttribute(name,value);
    if (text !== "") node.textContent = text;
    parent.append(node);
    return node;
  }
  const txt = (parent,x,y,text,attrs={}) => el(parent,"text",{x,y,fill:colors.ink,"font-size":19,...attrs},text);
  const line = (parent,x1,y1,x2,y2,attrs={}) => el(parent,"line",{x1,y1,x2,y2,stroke:colors.rule,"stroke-width":2,...attrs});
  const box = (parent,x,y,width,height,attrs={}) => el(parent,"rect",{x,y,width,height,fill:"none",...attrs});
  const dot = (parent,cx,cy,r,attrs={}) => el(parent,"circle",{cx,cy,r,fill:colors.ink,...attrs});
  const stage = (p,start,end) => Math.max(0,Math.min(1,(p-start)/(end-start)));

  function filmBridge(svg,chapter,x,y,width) {
    const g=el(svg,"g",{"data-principle":chapter.principle});
    const ink=chapter.captionInk || colors.ink;
    const secondary=chapter.captionInk || colors.muted;
    const middle=Math.round(width*.49);
    line(g,x,y,width-x,y,{stroke:secondary,"stroke-opacity":.6,"stroke-width":1});
    const heading={"font-size":15,"font-weight":650,fill:secondary};
    const body={"font-size":18,"font-weight":500,fill:ink};
    txt(g,x+2,y+19,caseLabel+"  ·  "+chapter.context,heading);
    txt(g,x+2,y+42,chapter.example,body);
    el(g,"path",{d:`M${middle-29},${y+28} h18 m-6,-6 6,6 -6,6`,fill:"none",stroke:secondary,"stroke-width":2.3});
    txt(g,middle+8,y+19,chapter.principle+"  ·  White paper §"+chapter.section,heading);
    txt(g,middle+8,y+42,chapter.meaning,body);
  }

  function guideBridge(host,chapter) {
    const bridge=document.createElement("div");
    bridge.className="chapter-bridge";
    bridge.dataset.principle=chapter.principle;
    const example=document.createElement("div"), principle=document.createElement("div");
    const context=document.createElement("span"), source=document.createElement("a");
    context.textContent=caseLabel+" · "+chapter.context;
    source.textContent=chapter.principle+" · White paper §"+chapter.section;
    source.href="../../paper/"+chapter.reading;
    const observation=document.createElement("p"), meaning=document.createElement("p");
    observation.textContent=chapter.example;
    meaning.textContent=chapter.meaning;
    example.append(context,observation);
    principle.append(source,meaning);
    bridge.append(example,principle);
    if (chapter.captionInk) bridge.classList.add("on-dark");
    host.append(bridge);
  }

  function create(host, topic, mobile) {
    const chapter = chapters[topic];
    const W = mobile ? 480 : 1120, H = mobile ? 505 : 405;
    const gutter = mobile ? 8 : 14;
    const inGuide=host.dataset.responsive==="true";
    const width = W + 2*gutter, height = H + (inGuide ? 12 : 61);
    host.replaceChildren();
    const svg = el(host,"svg",{
      viewBox:`0 0 ${width} ${height}`,role:"img","aria-label":chapter.description,
      "data-figure":topic,"font-family":"'Avenir Next', Arial, sans-serif"
    });
    el(svg,"title",{},chapter.figure);
    el(svg,"desc",{},[chapter.description,chapter.example,chapter.principle+".",chapter.meaning].join(" "));
    box(svg,0,0,width,height,{fill:chapter.tint});
    const plot=el(svg,"g",{transform:`translate(${gutter} 7)`});
    if (inGuide) guideBridge(host,chapter);
    else filmBridge(svg,chapter,gutter,H+11,width);
    return {topic,mobile,svg,plot,W,H,beats:[],active:-1};
  }

  function mount(host,topic,beat) {
    const mobile=host.dataset.responsive==="true" && matchMedia("(max-width:640px)").matches;
    let view=mounted.get(host);
    if (!view || view.topic!==topic || view.mobile!==mobile) {
      view=create(host,topic,mobile);
      mounted.set(host,view);
    }
    if (!view.beats[beat]) {
      const g=el(view.plot,"g",{"data-figure-step":beat});
      const seek=drawings[topic]({
        svg:view.svg,g,el,txt,line,box,dot,stage,colors,W:view.W,H:view.H,mobile,beat,
        assetBase:host.dataset.assetBase||"assets/"
      });
      view.beats[beat]={g,seek};
    }
    if (view.active!==beat) {
      if (view.active>=0) view.beats[view.active].g.setAttribute("display","none");
      view.beats[beat].g.removeAttribute("display");
      view.active=beat;
      view.svg.setAttribute("aria-label",chapters[topic].description+" Current concept: "+ShopTerms[topic][beat][0]+".");
    }
    const current=view.beats[beat];
    current.seek(0);
    return {seek:p=>current.seek(Math.max(0,Math.min(1,p)))};
  }
  return {chapters,mount};
}
