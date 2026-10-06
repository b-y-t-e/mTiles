// Where each tile of a workspace goes in the phone's miniature of it. Pure, and tested under node.
//
// The tree is the desktop's own split tree, as mTiles sends it: a split has a direction ("row" when its
// children stand side by side, "column" when one is above the other) and the first child's share.

/**
 * Lays a layout tree out in a rectangle, with `gap` between siblings.
 * @returns {{tileId:string, leaf:object, x:number, y:number, w:number, h:number}[]}
 */
export function layoutRects(node, rect, gap = 8) {
  const out = [];
  place(node, rect, gap, out);
  return out;
}

function place(node, rect, gap, out) {
  if (!node) return;
  if (node.type === "leaf") {
    out.push({ tileId: node.tileId, leaf: node, x: rect.x, y: rect.y, w: rect.w, h: rect.h });
    return;
  }

  const ratio = Math.min(0.9, Math.max(0.1, Number(node.ratio) || 0.5));
  if (node.direction === "row") {
    const room = Math.max(0, rect.w - gap);
    const first = Math.round(room * ratio);
    place(node.first, { x: rect.x, y: rect.y, w: first, h: rect.h }, gap, out);
    place(node.second, { x: rect.x + first + gap, y: rect.y, w: room - first, h: rect.h }, gap, out);
  } else {
    const room = Math.max(0, rect.h - gap);
    const first = Math.round(room * ratio);
    place(node.first, { x: rect.x, y: rect.y, w: rect.w, h: first }, gap, out);
    place(node.second, { x: rect.x, y: rect.y + first + gap, w: rect.w, h: room - first }, gap, out);
  }
}

/**
 * Whether a tree drawn in this rectangle leaves every tile big enough to tap. A desktop layout of eight
 * tiles is a grid of stamps on a phone; below this the page lists the tiles instead of drawing them.
 */
export function isLegible(rects, minSide = 56) {
  return rects.every((r) => r.w >= minSide && r.h >= minSide);
}

/** The tiles in reading order: top to bottom, then left to right. */
export function readingOrder(rects) {
  return [...rects].sort((a, b) => (a.y - b.y) || (a.x - b.x));
}
