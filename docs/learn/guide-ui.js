"use strict";

const $ = (selector) => document.querySelector(selector);
function sourceHref(file) {
  if (file.startsWith("https:")) return file;
  if (/^docs\/learn\/[^/]+\.html(?:#|$)/.test(file)) return "../../" + file;
  return (document.documentElement.dataset.sourceBase || "../../") + file;
}
function plate(name, context, content) {
  return '<div class="plate"><div class="plate-inner"><div class="plate-top"><span>'+name+'</span><span class="chip">'+context+'</span></div>'+content+'</div></div>';
}
function choices(labels, onChange, container, initial=0) {
  const buttons = labels.map((label,index) => {
    const button = document.createElement("button");
    button.type = "button"; button.className = "pick"; button.textContent = label;
    button.addEventListener("click", () => select(index));
    container.append(button); return button;
  });
  function select(index) {
    buttons.forEach((button,i) => button.setAttribute("aria-pressed", String(i===index)));
    onChange(index);
  }
  select(initial);
}
function radioArea() { return '<div class="options" id="choices"></div>'; }
function setActivity(html) { $("#activity").innerHTML = html; }
function link(label, file) { return '<a href="'+sourceHref(file)+'">'+label+'</a>'; }
