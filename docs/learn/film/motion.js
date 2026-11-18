const timeline = gsap.timeline({paused:true});
// Enlarge the evidence when the lesson asks the viewer to read a chart,
// compare records, or follow a query. Tracing a dependency uses the wide map.
const evidenceShots = new Set([
  "rules-1", "layers-0", "layers-1", "layers-2", "incident-0", "incident-2",
  "dependency-2", "azure-1", "replay-1", "replay-2", "inspect-1", "inspect-2"
]);
for (const host of document.querySelectorAll(".story-board")) {
  const scene = host.closest("section");
  const topic = host.dataset.topic, beat = Number(host.dataset.beat);
  const [term, definition, title] = ShopTerms[topic][beat];
  scene.querySelector("h1").textContent = title;
  scene.querySelector(".term").textContent = term;
  scene.querySelector(".definition").textContent = definition;
  if(evidenceShots.has(scene.id))host.dataset.layout="evidence";
  const view = ShopMap.mount(host, topic, beat);
  const clock = {progress:0};
  timeline.to(clock, {progress:1, duration:Number(scene.dataset.duration), ease:"none",
    onUpdate:()=>view.seek(clock.progress)}, Number(scene.dataset.start));
}
window.__timelines = window.__timelines || {};
window.__timelines["shop"] = timeline;
