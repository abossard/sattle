const chapters=story.map(scene=>{
  const [nav,intro,activity,sources]=lessons[scene.id];
  return {...scene,nav,intro,activity,sources};
});

const chapterNav=$("#chapters");
const chapterMenu=$("#chapter-menu"), chapterToggle=$("#chapters-toggle");
if (!chapterMenu.hidePopover) {
  chapterMenu.hidden=true;
  chapterToggle.setAttribute("aria-expanded","false");
  chapterToggle.onclick=()=>{
    chapterMenu.hidden=!chapterMenu.hidden;
    chapterToggle.setAttribute("aria-expanded",String(!chapterMenu.hidden));
  };
  chapterMenu.addEventListener("keydown",event=>{
    if(event.key==="Escape"){closeChapters();chapterToggle.focus();}
  });
}
function closeChapters() {
  if(chapterMenu.hidePopover)chapterMenu.hidePopover();
  else {chapterMenu.hidden=true;chapterToggle.setAttribute("aria-expanded","false");}
}
chapterNav.addEventListener("click",event=>{
  if(event.target.closest("a")){closeChapters();$("#lesson").focus({preventScroll:true});}
});
chapters.forEach((c,i)=>{
  const a=document.createElement("a");a.href="#"+c.id;a.innerHTML='<span>'+String(i+1).padStart(2,'0')+'</span>'+c.nav;chapterNav.append(a);
});
function show(focus=false) {
  const i=Math.max(0,chapters.findIndex(c=>c.id===location.hash.slice(1))), c=chapters[i];
  [...chapterNav.children].forEach((a,k)=>{if(k===i)a.setAttribute("aria-current","step");else a.removeAttribute("aria-current");});
  $("#eyebrow").textContent=document.body.dataset.case+" / "+String(i+1).padStart(2,"0");
  $("#title").textContent=c.title;$("#intro").textContent=c.intro;
  const chapter=ShopMap.chapters[c.id];
  $("#animation").style.setProperty("--figure-paper",chapter.tint);
  $("#animation").style.setProperty("--figure-ink",chapter.captionInk || "var(--ink)");
  ShopMap.guideBridge($("#chapter-reference"),chapter);
  player.load(c);
  player.onPosition(c.activity());
  const references=new Map(c.sources.map(([name,file])=>[file,name]));
  for(const slug of ShopMap.chapters[c.id].papers || []){
    const file="paper/"+slug+".md";
    if(!references.has(file))references.set(file,"White paper · chapter "+Number(slug.slice(2,4)));
  }
  $("#reading").innerHTML='<div class="label">Explore the source</div>'+[...references].map(([file,name])=>link(name,file)).join("");
  $("#back").disabled=i===0;$("#back").onclick=()=>location.hash=chapters[i-1].id;
  $("#next").textContent=i===chapters.length-1?"Replay from start ↺":"Next chapter →";
  $("#next").onclick=()=>location.hash=chapters[(i+1)%chapters.length].id;
  $("#count").textContent=(i+1)+" / "+chapters.length;
  document.title=c.nav+" · Sattle field guide";
  if(focus){closeChapters();$("#lesson").focus({preventScroll:true});window.scrollTo(0,0);}
}
window.addEventListener("hashchange",()=>show(true));
$(".skip").onclick=e=>{e.preventDefault();$("#lesson").focus();};
// Some basic static servers omit byte ranges. A local Blob makes their media seekable.
const mediaReady=new WeakMap();
function prepareMedia(media, source){
  if(mediaReady.has(media))return mediaReady.get(media);
  const prepare=async()=>{
    if(location.protocol==="file:")return;
    const url=source || media.currentSrc || media.src;
    const headers=await fetch(url,{method:"HEAD"});
    if(headers.headers.get("accept-ranges")?.includes("bytes")){
      if(source)media.src=source;
      return;
    }
    const response=await fetch(url);
    if(!response.ok)throw Error("Media unavailable: "+response.status);
    const blob=await response.blob();
    const loaded=new Promise((resolve,reject)=>{
      media.addEventListener("loadedmetadata",resolve,{once:true});
      media.addEventListener("error",reject,{once:true});
    });
    media.preload="metadata";media.src=URL.createObjectURL(blob);media.load();await loaded;
  };
  const ready=prepare().catch(()=>{
    mediaReady.delete(media);
    if(source)media.src=source;
  });
  mediaReady.set(media,ready);return ready;
}
const cinema=$("#cinema"), video=cinema.querySelector("video");
const mediaFolder=cinema.dataset.folder || "";
const posterPath=video.getAttribute("poster");
const filmSource=video.querySelector("source"), filmCaptions=$("#film-captions");
const filePlayback=location.protocol==="file:";

function localTrack(captions) {
  const previous=filmCaptions.getAttribute("src");
  filmCaptions.src=URL.createObjectURL(new Blob([captions],{type:"text/vtt"}));
  if(previous?.startsWith("blob:"))URL.revokeObjectURL(previous);
}
if(filePlayback){
  const payload=[...document.scripts].find(script=>script.sattleFilm)?.sattleFilm;
  if(payload)localTrack(payload.captions);
}else filmCaptions.src=mediaFolder+"captions.vtt";

let localVersion;
async function refreshLocalFilm() {
  // file:// blocks fetch of a sibling manifest. A small classic script carries
  // the movie digest and the matching captions. Load it fresh before opening.
  const script=document.createElement("script");
  const probe=new URL(mediaFolder+"captions-local.js",document.baseURI);
  probe.searchParams.set("check",String(Date.now()));
  script.src=probe.href;
  const edition=await new Promise(resolve=>{
    script.onload=()=>resolve(script.sattleFilm);
    script.onerror=()=>resolve(null);
    document.head.append(script);
  });
  script.remove();
  if(!edition||!edition.version||edition.version===localVersion)return;
  const movie=new URL(filmSource.src), poster=new URL(posterPath,document.baseURI);
  movie.searchParams.set("v",edition.version);
  poster.searchParams.set("v",edition.version);
  filmSource.src=movie.href;
  cinema.querySelector("a[download]").href=movie.href;
  video.poster=poster.href;
  localTrack(edition.captions);
  video.load();
  localVersion=edition.version;
}

async function prepareFilm(){
  if(filePlayback)return refreshLocalFilm();
  // An open guide can outlive a render. Check the small digest before reusing
  // a movie from the browser cache or the blob kept for servers without ranges.
  let version;
  try{
    const response=await fetch(mediaFolder+"film-version.txt",{cache:"no-store"});
    if(response.ok)version=(await response.text()).trim();
  }catch{/* Some static hosts serve only the media files. */}
  if(!/^[a-f0-9]{64}$/.test(version || ""))version=String(Date.now());

  const url=new URL(filmSource.src);
  url.searchParams.set("v",version);
  const changed=filmSource.src!==url.href, oldBlob=video.getAttribute("src");
  if(changed)mediaReady.delete(video);
  // Choose a native stream or a blob before changing the <source>. Starting
  // both requests for the same movie can block playback on a no-range server.
  await prepareMedia(video,url.href);
  if(changed){
    filmSource.src=url.href;
    cinema.querySelector("a[download]").href=url.href;
    filmCaptions.src=mediaFolder+"captions.vtt?v="+version;
    const poster=new URL(posterPath,document.baseURI);
    poster.searchParams.set("v",version);
    video.poster=poster.href;
    if(oldBlob?.startsWith("blob:")&&oldBlob!==video.src)URL.revokeObjectURL(oldBlob);
  }
}
const watch=$("#watch");
watch.onclick=async()=>{
  player.pause();watch.disabled=true;
  try{await prepareFilm();cinema.showModal();}
  finally{watch.disabled=false;}
};
$("#close").onclick=()=>{video.pause();cinema.close();};
cinema.addEventListener("cancel",()=>video.pause());
cinema.addEventListener("close",()=>video.pause());

const player=(()=>{
  const audio=$("#narrator"), range=$("#scene-position"), listen=$("#listen");
  let scene, activeBeat=-1, map, position=0, frame=0, ghost=0, pendingPlay=0, positionListener;
  function pause() {
    pendingPlay++;
    audio.pause();cancelAnimationFrame(frame);cancelAnimationFrame(ghost);
    listen.textContent="▶ Play with narration";
    if(audio.muted)listen.textContent="▶ Play animation";
  }
  function showTime(time) {
    position=Math.max(scene.beats[0].start,Math.min(time,scene.beats.at(-1).end));
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
    showTime(audio.currentTime);
    if(position>=scene.beats.at(-1).end-.06){pause();return;}
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
      await prepareMedia(audio);
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
    if(audio.paused)listen.textContent=audio.muted?"▶ Play animation":"▶ Play with narration";
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
    // Brief silent preview. The narration always starts at the chapter opening.
    if(!matchMedia("(prefers-reduced-motion: reduce)").matches)preview(.6);
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
    viewport.style.background=ShopMap.chapters[live.dataset.figure].tint;
    dialog.showModal();
    viewport.scrollTo(0,0);
    viewport.focus({preventScroll:true});
  };
  dialog.querySelector("button").onclick=()=>dialog.close();
  dialog.addEventListener("close",()=>drawing.replaceChildren());
  window.addEventListener("hashchange",()=>{if(dialog.open)dialog.close();});
}
installFigureZoom();
prepareMedia($("#narrator"));
show();
