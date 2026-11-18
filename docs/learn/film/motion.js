const timeline = gsap.timeline({paused:true});
for (const host of document.querySelectorAll(".story-board")) {
  const scene = host.closest("section");
  const view = ShopMap.mount(host, host.dataset.topic, Number(host.dataset.beat));
  const clock = {progress:0};
  timeline.to(clock, {progress:1, duration:Number(scene.dataset.duration), ease:"none",
    onUpdate:()=>view.seek(clock.progress)}, Number(scene.dataset.start));
}
window.__timelines = window.__timelines || {};
window.__timelines["shop"] = timeline;
