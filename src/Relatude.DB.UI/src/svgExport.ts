// Saving an on-screen SVG as a file of its own.
//
// What is on screen leans on the page: a stylesheet full of classes and theme variables, animations,
// icon components that render as nested <svg> elements. A file has none of that, so the copy is
// made to stand alone: the stylesheet is resolved into attributes on every element, in the light
// palette (the file is for paper, and a dark theme prints as white on white), the nested icons are
// flattened into groups (some viewers and Office skip a nested <svg>), the text halo is dropped
// (paint-order is not everywhere), and a white page is put behind it all.

/** What the stylesheet's variables resolve to in the file: the light palette. */
const printPalette: Record<string, string> = {
  "--bg": "#f5f4f2",
  "--panel": "#ffffff",
  "--border": "#c6c1b9",
  "--text": "#1d1c1a",
  "--text-muted": "#6f6c66",
  "--text-soft": "#45433f",
  "--text-faint": "#a6a39d",
  "--accent": "#0960b2",
  "--accent-soft": "rgba(9, 96, 178, 0.1)",
  "--hover": "rgba(0, 0, 0, 0.045)",
};

/** Paint properties the stylesheet gives an element, written onto it so the file needs no stylesheet. */
const paintProps = ["fill", "stroke", "stroke-width", "stroke-dasharray", "stroke-linecap", "stroke-linejoin", "opacity", "fill-opacity", "stroke-opacity"];
const textProps = ["font-size", "font-family", "font-weight", "letter-spacing"];
const hostId = "svg-export-host";

/** The svg as it is on screen - its size, its view - saved as `fileName`. */
export function downloadSvg(svg: SVGSVGElement, fileName: string, title: string): void {
  const text = svgDocument(svg, title);
  const url = URL.createObjectURL(new Blob([text], { type: "image/svg+xml;charset=utf-8" }));
  try {
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    link.click();
  } finally {
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
}

/** The text of the file: the svg copied, resolved and made to stand alone. */
export function svgDocument(svg: SVGSVGElement, title: string): string {
  const rect = svg.getBoundingClientRect();
  const w = Math.max(1, Math.round(rect.width));
  const h = Math.max(1, Math.round(rect.height));
  const ns = "http://www.w3.org/2000/svg";
  const clone = svg.cloneNode(true) as SVGSVGElement;
  clone.setAttribute("xmlns", ns);
  clone.setAttribute("width", String(w));
  clone.setAttribute("height", String(h));
  clone.setAttribute("viewBox", `0 0 ${w} ${h}`);
  clone.removeAttribute("class");
  clone.removeAttribute("style");
  const caption = document.createElementNS(ns, "title");
  caption.textContent = title;
  clone.insertBefore(caption, clone.firstChild);
  const paper = document.createElementNS(ns, "rect");
  paper.setAttribute("width", "100%");
  paper.setAttribute("height", "100%");
  paper.setAttribute("fill", "#ffffff");
  clone.insertBefore(paper, caption.nextSibling);

  // The clone is parked in the document under the print palette, and every element is asked what
  // it computes to. Animations restart on an element that has just been inserted and would be read
  // at their first frame - a node that fades in would come out invisible - so they are switched off
  // for the copy, and transitions with them.
  const host = document.createElement("div");
  host.id = hostId;
  host.style.cssText = `position:fixed;left:-100000px;top:0;width:${w}px;height:${h}px;color-scheme:light`;
  for (const [name, value] of Object.entries(printPalette)) host.style.setProperty(name, value);
  const still = document.createElement("style");
  still.textContent = `#${hostId} * { animation: none !important; transition: none !important; }`;
  host.appendChild(still);
  host.appendChild(clone);
  document.body.appendChild(host);
  try {
    inlineStyles(clone);
  } finally {
    host.remove();
  }
  for (const style of clone.querySelectorAll("style")) style.remove();
  for (const nested of [...clone.querySelectorAll("svg")]) flatten(nested);
  for (const el of clone.querySelectorAll("[class]")) el.removeAttribute("class");
  return '<?xml version="1.0" encoding="UTF-8"?>\n' + new XMLSerializer().serializeToString(clone);
}

function inlineStyles(root: Element) {
  const walk = (el: Element) => {
    const cs = getComputedStyle(el);
    const isText = el.tagName === "text" || el.tagName === "tspan";
    for (const prop of paintProps) {
      const value = cs.getPropertyValue(prop);
      if (!value || value === "normal") continue;
      el.setAttribute(prop, value.replace(/px/g, ""));
    }
    if (isText) {
      // the halo a label wears on screen needs paint-order, which not every viewer has; on paper the
      // white page does the job
      el.setAttribute("stroke", "none");
      el.setAttribute("style", textProps.map((prop) => `${prop}:${cs.getPropertyValue(prop)}`).join(";"));
    } else el.removeAttribute("style");
    for (const child of [...el.children]) walk(child);
  };
  walk(root);
}

/**
 * An icon component renders as an <svg> of its own inside the drawing, sized by width and height
 * and drawn in a 24 by 24 view box. The same picture as a group: moved to where the icon was and
 * scaled from the view box to the size, with the icon's paint carried over.
 */
function flatten(nested: SVGSVGElement) {
  const parent = nested.parentNode;
  if (!parent) return;
  const box = (nested.getAttribute("viewBox") ?? "0 0 24 24").split(/[\s,]+/).map(Number);
  const [vx, vy, vw, vh] = box.length === 4 && box.every((n) => Number.isFinite(n)) ? box : [0, 0, 24, 24];
  const width = Number(nested.getAttribute("width")) || vw;
  const height = Number(nested.getAttribute("height")) || vh;
  const x = Number(nested.getAttribute("x")) || 0;
  const y = Number(nested.getAttribute("y")) || 0;
  const g = document.createElementNS("http://www.w3.org/2000/svg", "g");
  g.setAttribute("transform", `translate(${x} ${y}) scale(${width / (vw || 1)} ${height / (vh || 1)}) translate(${-vx} ${-vy})`);
  for (const attr of [...nested.attributes]) {
    if (["xmlns", "width", "height", "viewBox", "x", "y", "class", "style"].includes(attr.name)) continue;
    g.setAttribute(attr.name, attr.value);
  }
  while (nested.firstChild) g.appendChild(nested.firstChild);
  parent.replaceChild(g, nested);
}
