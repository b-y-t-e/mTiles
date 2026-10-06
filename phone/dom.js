// Building elements without ever parsing HTML. Text goes in through textContent and nothing else,
// because everything this page shows came from a paired machine and, before that, from a model.

/**
 * h("div.card.blocked", { onclick, title }, child, "text", [more])
 * A class list may follow the tag after dots. Attributes starting with "on" are listeners; `dataset`
 * and `style` are objects; everything else is set as a property when the element has one.
 */
export function h(spec, attrs, ...children) {
  const [tag, ...classes] = spec.split(".");
  const el = document.createElement(tag || "div");
  if (classes.length) el.className = classes.join(" ");

  if (attrs && (typeof attrs !== "object" || attrs instanceof Node || Array.isArray(attrs))) {
    children.unshift(attrs);
    attrs = null;
  }

  for (const [key, value] of Object.entries(attrs ?? {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key.startsWith("on") && typeof value === "function") el.addEventListener(key.slice(2), value);
    else if (key === "dataset") Object.assign(el.dataset, value);
    else if (key === "style") {
      for (const [prop, v] of Object.entries(value)) {
        if (prop.startsWith("--")) el.style.setProperty(prop, v);
        else el.style[prop] = v;
      }
    }
    else if (key === "class") el.className += ` ${value}`;
    else if (key in el) el[key] = value;
    else el.setAttribute(key, value === true ? "" : String(value));
  }

  append(el, children);
  return el;
}

function append(el, children) {
  for (const child of children) {
    if (child === null || child === undefined || child === false) continue;
    if (Array.isArray(child)) append(el, child);
    else if (child instanceof Node) el.append(child);
    else el.append(document.createTextNode(String(child)));
  }
}

/** Replaces an element's children. */
export function fill(el, ...children) {
  el.replaceChildren();
  append(el, children);
  return el;
}

/** The marks the desktop uses for a tile's activity: a turning arc while it works, a still alert while
 *  it waits for somebody, nothing otherwise. Drawn, not lettered, for the reason the desktop draws them. */
export function activityMark(activity) {
  if (activity === "working") {
    const svg = svgEl("svg", { viewBox: "0 0 16 16", class: "mark working", "aria-label": "working" });
    svg.append(svgEl("path", { d: "M8 2 A6 6 0 0 1 14 8" }));
    return svg;
  }
  if (activity === "blocked") {
    const svg = svgEl("svg", { viewBox: "0 0 16 16", class: "mark blocked", "aria-label": "waiting for you" });
    svg.append(svgEl("circle", { cx: 8, cy: 8, r: 6.25 }), svgEl("path", { d: "M8 4.6v4.2M8 10.9v.5" }));
    return svg;
  }
  return null;
}

/** A small line icon by name, drawn in the stroke style of the activity marks. */
export function icon(name) {
  const paths = ICONS[name] ?? ICONS.dot;
  const svg = svgEl("svg", { viewBox: "0 0 24 24", class: "icon", "aria-hidden": "true" });
  for (const d of paths) svg.append(svgEl("path", { d }));
  return svg;
}

const ICONS = {
  back: ["M15 5l-7 7 7 7"],
  mic: ["M12 3a3 3 0 0 1 3 3v6a3 3 0 0 1-6 0V6a3 3 0 0 1 3-3z", "M5 11a7 7 0 0 0 14 0", "M12 18v3"],
  send: ["M4 12h14", "M13 6l6 6-6 6"],
  stop: ["M7 7h10v10H7z"],
  keys: ["M4 7h16v10H4z", "M8 11h.01M12 11h.01M16 11h.01M8 14h8"],
  more: ["M6 12h.01M12 12h.01M18 12h.01"],
  up: ["M12 18V6", "M6 12l6-6 6 6"],
  down: ["M12 6v12", "M6 12l6 6 6-6"],
  left: ["M18 12H6", "M12 6l-6 6 6 6"],
  right: ["M6 12h12", "M12 6l6 6-6 6"],
  enter: ["M19 5v7a3 3 0 0 1-3 3H6", "M10 11l-4 4 4 4"],
  star: ["M12 3.8l2.5 5.1 5.6.8-4 3.9.9 5.6-5-2.6-5 2.6.9-5.6-4-3.9 5.6-.8z"],
  logout: ["M14 4h4a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-4", "M9 8l-4 4 4 4", "M5 12h11"],
  plus: ["M12 5v14", "M5 12h14"],
  history: ["M4 12a8 8 0 1 0 2.3-5.7", "M4 4v4h4", "M12 8v4l3 2"],
  edit: ["M4 20h4L19 9l-4-4L4 16z", "M13 7l4 4"],
  chevron: ["M8 10l4 4 4-4"],
  clip: ["M20.5 11.5l-8.2 8.2a5 5 0 0 1-7.1-7.1l8.6-8.6a3.3 3.3 0 0 1 4.7 4.7l-8.6 8.6a1.7 1.7 0 0 1-2.4-2.4l7.9-7.9"],
  check: ["M5 12.5l4.5 4.5L19 7.5"],
  dot: ["M12 12h.01"],
};

function svgEl(tag, attrs) {
  const el = document.createElementNS("http://www.w3.org/2000/svg", tag);
  for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, String(v));
  return el;
}
