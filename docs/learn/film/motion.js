const timeline = gsap.timeline({paused:true});
for (const scene of document.querySelectorAll(".shot")) {
  const host = scene.querySelector(".story-board");
  const topic = host.dataset.topic;
  const ends = scene.dataset.beatEnds.split(",").map(Number);
  const termLabel = scene.querySelector(".term"), meaning = scene.querySelector(".definition");
  let active = -1, figure;
  function draw(time) {
    const index = ends.findIndex(end => time < end);
    const beat = index < 0 ? ends.length - 1 : index;
    if (beat !== active) {
      active = beat;
      figure = ShopMap.mount(host, topic, beat);
      const [term, definition] = ShopTerms[topic][beat];
      termLabel.textContent = term;
      meaning.textContent = definition;
    }
    const start = beat ? ends[beat-1] : 0;
    figure.seek((time-start)/(ends[beat]-start));
  }
  draw(0);
  const clock = {elapsed:0};
  timeline.to(clock, {elapsed:ends.at(-1), duration:ends.at(-1), ease:"none",
    onUpdate:()=>draw(clock.elapsed)}, Number(scene.dataset.start));
}
window.__timelines = window.__timelines || {};
window.__timelines.shop = timeline;
