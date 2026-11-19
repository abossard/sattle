const chapters=story.map(scene=>{
  const [nav,intro,activity,sources]=lessons[scene.id];
  return {...scene,nav,intro,activity,sources};
});

const chapterNav=$("#chapters");
const chapterMenu=$("#chapter-menu"), chapterToggle=$("#chapters-toggle");
function closeChapters() {
  if (!chapterMenu) return;
  if (chapterMenu.hidePopover) chapterMenu.hidePopover();
  else {
    chapterMenu.hidden=true;
    chapterToggle.setAttribute("aria-expanded","false");
  }
}
if (chapterMenu) {
  for (const [index, chapter] of chapters.entries()) {
    const a=document.createElement("a"), number=document.createElement("span");
    a.href="#"+chapter.id;
    number.className="chapter-number";
    number.textContent=String(index+1).padStart(2,"0");
    a.append(number,chapter.nav);
    chapterNav.append(a);
  }
  if (!chapterMenu.hidePopover) {
    closeChapters();
    chapterToggle.onclick=()=>{
      chapterMenu.hidden=!chapterMenu.hidden;
      chapterToggle.setAttribute("aria-expanded",String(!chapterMenu.hidden));
    };
    chapterMenu.addEventListener("keydown",event=>{
      if(event.key==="Escape"){closeChapters();chapterToggle.focus();}
    });
  }
  chapterNav.addEventListener("click",event=>{
    if (event.target.closest("a")?.hash===location.hash) {
      closeChapters();
      $("#title").focus();
    }
  });
}
const chapterLinks=[...chapterNav.children];
function show(focus=false) {
  const i=Math.max(0,chapters.findIndex(c=>c.id===location.hash.slice(1))), c=chapters[i];
  document.body.dataset.chapter=c.id;
  chapterLinks.forEach((a,k)=>{if(k===i)a.setAttribute("aria-current","step");else a.removeAttribute("aria-current");});
  $("#eyebrow").textContent=document.body.dataset.case+" / "+String(i+1).padStart(2,"0");
  $("#title").textContent=c.title;$("#intro").textContent=c.intro;
  const chapter=ShopMap.chapters[c.id];
  ShopMap.guideBridge($("#chapter-reference"),chapter);
  player.load(c);
  player.onPosition(c.activity());
  const references=new Map(c.sources.map(([name,file])=>[file,name]));
  for(const slug of ShopMap.chapters[c.id].papers || []){
    const file="paper/"+slug+".md";
    if(!references.has(file))references.set(file,"White paper · chapter "+Number(slug.slice(2,4)));
  }
  $("#reading").innerHTML='<div class="label">Explore the source</div>'+[...references].map(([file,name])=>link(name,file)).join("");
  const back=$("#back"), next=$("#next"), last=i===chapters.length-1;
  back.hidden=i===0;
  if (i>0) back.href="#"+chapters[i-1].id;
  else back.removeAttribute("href");
  const destinationIndex=last ? 0 : i+1;
  const destination=chapters[destinationIndex];
  next.href="#"+destination.id;
  next.querySelector(".next-label").textContent=last ? "Start again" : "Next chapter";
  next.querySelector(".next-title").textContent=chapterLinks[destinationIndex].querySelector(".chapter-name")?.textContent ?? destination.nav;
  next.querySelector(".next-arrow").textContent=last ? "↺" : "→";
  $("#count").textContent="Chapter "+(i+1)+" of "+chapters.length;
  document.title=c.nav+" · Sattle field guide";
  if(focus){closeChapters();$("#title").focus({preventScroll:true});window.scrollTo(0,0);}
}
window.addEventListener("hashchange",()=>show(true));
$(".skip").onclick=e=>{e.preventDefault();$("#lesson").focus();};
// A local Blob lets the narrator seek on static servers that omit byte ranges.
let narrationReady;
function prepareNarration(audio){
  if (location.protocol==="file:") return;
  if (narrationReady) return narrationReady;
  const url=audio.src;
  async function prepare(){
    const headers=await fetch(url,{method:"HEAD"});
    if(headers.headers.get("accept-ranges")?.includes("bytes"))return;
    const response=await fetch(url);
    if(!response.ok)throw Error("Media unavailable: "+response.status);
    const blob=await response.blob();
    const loaded=new Promise((resolve,reject)=>{
      audio.addEventListener("loadedmetadata",resolve,{once:true});
      audio.addEventListener("error",reject,{once:true});
    });
    audio.preload="metadata";audio.src=URL.createObjectURL(blob);audio.load();await loaded;
  }
  narrationReady=prepare().catch(()=>{narrationReady=null;});
  return narrationReady;
}

const player=(()=>{
  const audio=$("#narrator"), range=$("#scene-position"), listen=$("#listen");
  let scene, activeBeat=-1, map, position=0, frame=0, ghost=0, pendingPlay=0, positionListener;
  const pager=$(".chapter-pager"), status=$("#chapter-status");
  let complete=false;
  function pausedLabel(){
    if(complete)return audio.muted ? "↺ Replay animation" : "↺ Replay narration";
    return audio.muted ? "▶ Play animation" : "▶ Play with narration";
  }
  function pause() {
    pendingPlay++;
    audio.pause();cancelAnimationFrame(frame);cancelAnimationFrame(ghost);
    listen.textContent=pausedLabel();
  }
  function showTime(time) {
    position=Math.max(scene.beats[0].start,Math.min(time,scene.beats.at(-1).end));
    const atEnd=position>=scene.beats.at(-1).end;
    if(complete!==atEnd){
      complete=atEnd;
      pager.classList.toggle("complete",complete);
      status.textContent=complete ? "Chapter complete" : "";
      if (chapterNav.classList.contains("chapter-strip")) {
        for (const [index,link] of chapterLinks.entries()) {
          const upNext=complete && link.hash===$("#next").hash;
          link.toggleAttribute("data-up-next",upNext);
          link.querySelector(".chapter-number").textContent=upNext
            ? (index===0 ? "Restart ↺" : "Next →")
            : String(index+1).padStart(2,"0");
        }
      }
      if(audio.paused)listen.textContent=pausedLabel();
    }
    let i=scene.beats.findIndex(b=>position<b.end-.001);if(i<0)i=scene.beats.length-1;
    const beat=scene.beats[i];
    if(i!==activeBeat){
      activeBeat=i;map=ShopMap.mount($("#map"),scene.id,i);
      const [term, meaning]=ShopTerms[scene.id][i];
      $("#concept-term").textContent=term;
      $("#concept-meaning").textContent=meaning;
      [...$("#beat-buttons").children].forEach((b,k)=>b.setAttribute("aria-pressed",String(k===i)));
      const spoken=$("#spoken");
      spoken.replaceChildren();
      const answer=Array.isArray(beat.answer) ? beat.answer.join(" ") : beat.answer;
      for(const [role,words] of [["Listener",beat.question],["Guide",answer]]){
        const turn=document.createElement("p"), label=document.createElement("strong");
        turn.className="spoken-turn";
        label.textContent=role;
        turn.append(label,document.createTextNode(words));
        spoken.append(turn);
      }
    }
    const fraction=Math.max(0,Math.min(1,(position-beat.start)/(beat.end-beat.start)));
    map.seek(fraction);
    if(positionListener)positionListener(i,fraction);
    const seconds=position-scene.beats[0].start;
    range.value=String(Math.round(1000*seconds/(scene.beats.at(-1).end-scene.beats[0].start)));
    const clock=Math.floor(seconds/60)+":"+String(Math.floor(seconds)%60).padStart(2,"0");
    $("#elapsed").value=clock;range.setAttribute("aria-valuetext",clock+" · "+beat.focus);
  }
  matchMedia("(max-width:640px)").addEventListener("change",()=>{
    if(!scene || activeBeat<0)return;
    map=ShopMap.mount($("#map"),scene.id,activeBeat);
    showTime(position);
  });
  function tick() {
    const end=scene.beats.at(-1).end;
    if(audio.currentTime>=end-.06){showTime(end);pause();return;}
    showTime(audio.currentTime);
    if(!audio.paused)frame=requestAnimationFrame(tick);
  }
  function jump(i,fraction=0) {
    cancelAnimationFrame(ghost);
    const beat=scene.beats[i];showTime(beat.start+(beat.end-beat.start)*fraction);
    audio.currentTime=position;
  }
  function preview(end) {
    if(matchMedia("(prefers-reduced-motion: reduce)").matches){map.seek(end);return;}
    const start=performance.now();
    function draw(now){
      const progress=Math.min(1,(now-start)/2200);
      map.seek(end*progress);
      if(progress<1)ghost=requestAnimationFrame(draw);
    }
    ghost=requestAnimationFrame(draw);
  }
  listen.onclick=async()=>{
    if(!audio.paused){pause();return;}
    cancelAnimationFrame(ghost);
    const token=++pendingPlay;
    if(position>=scene.beats.at(-1).end-.08)showTime(scene.beats[0].start);
    try{
      await prepareNarration(audio);
      if(token!==pendingPlay)return;
      audio.currentTime=position;
      await audio.play();
      if(token!==pendingPlay)return;
      listen.textContent="Ⅱ Pause";frame=requestAnimationFrame(tick);
    }catch{if(token===pendingPlay)listen.textContent="Retry playback";}
  };
  audio.addEventListener("ended",()=>{showTime(scene.beats.at(-1).end);pause();});
  range.oninput=()=>{
    cancelAnimationFrame(ghost);
    const start=scene.beats[0].start;
    showTime(start+(scene.beats.at(-1).end-start)*Number(range.value)/1000);audio.currentTime=position;
  };
  $("#mute").onclick=()=>{
    audio.muted=!audio.muted;
    $("#mute").setAttribute("aria-pressed",String(audio.muted));$("#mute").textContent=audio.muted?"Unmute":"Mute";
    if(audio.paused)listen.textContent=pausedLabel();
  };
  function load(nextScene){
    pause();positionListener=null;scene=nextScene;activeBeat=-1;$("#beat-buttons").replaceChildren();
    for(const [i,beat] of scene.beats.entries()){
      const button=document.createElement("button");button.className="pick";button.textContent=beat.focus;
      button.type="button";
      button.onclick=()=>{
        jump(i);
        if(scene.id==="estate" && audio.paused)preview(i===scene.beats.length-1 ? .1 : 1);
      };
      $("#beat-buttons").append(button);
    }
    showTime(scene.beats[0].start);
    // A silent preview can introduce the question without showing a later answer.
    const opening=scene.beats[0];
    const previewEnd=opening.spokenCues ? (opening.answerStarts[0]-opening.start)/(opening.end-opening.start) : .6;
    if(!matchMedia("(prefers-reduced-motion: reduce)").matches)preview(previewEnd);
  }
  return {load,jump,pause,onPosition(listener){positionListener=typeof listener==="function" ? listener : null;}};
})();

function installFigureZoom() {
  const enlarge=$(".enlarge-figure");

  const dialog=document.createElement("dialog");
  dialog.className="figure-zoom";
  dialog.setAttribute("aria-labelledby","figure-zoom-title");
  dialog.innerHTML=`<header><div><p class="figure-zoom-context"></p><h2 id="figure-zoom-title"></h2></div><button type="button">Close</button></header>
    <div class="figure-zoom-window" tabindex="0" role="region" aria-label="Enlarged diagram" aria-describedby="figure-pan-help"><div class="figure-zoom-art"></div></div>
    <div class="figure-zoom-caption"><strong></strong> <span></span></div>
    <p id="figure-pan-help">Scroll or swipe to see the whole figure. You can also use the arrow keys.</p>`;
  document.body.append(dialog);
  const viewport=dialog.querySelector(".figure-zoom-window"), art=dialog.querySelector(".figure-zoom-art");
  // SVG clipping paths and masks resolve inside this root, independently of
  // the live diagram. The copy holds its frame even if the guide relayouts.
  const drawing=art.attachShadow({mode:"open"});
  enlarge.onclick=()=>{
    const live=$("#map > svg");
    if(!live)return;
    player.pause();
    const copy=live.cloneNode(true), nativeWidth=live.viewBox.baseVal.width;
    copy.style.display="block";
    copy.style.width="100%";
    copy.style.height="auto";
    art.style.minWidth=Math.ceil(nativeWidth*1.2)+"px";
    drawing.replaceChildren(copy);
    dialog.querySelector(".figure-zoom-context").textContent=$("#eyebrow").textContent;
    dialog.querySelector("h2").textContent=live.querySelector("title").textContent;
    dialog.querySelector(".figure-zoom-caption strong").textContent=$("#concept-term").textContent+".";
    dialog.querySelector(".figure-zoom-caption span").textContent=$("#concept-meaning").textContent;
    dialog.showModal();
    viewport.scrollTo(0,0);
    viewport.focus({preventScroll:true});
  };
  dialog.querySelector("button").onclick=()=>dialog.close();
  dialog.addEventListener("close",()=>drawing.replaceChildren());
  window.addEventListener("hashchange",()=>{if(dialog.open)dialog.close();});
}
installFigureZoom();
prepareNarration($("#narrator"));
show();
