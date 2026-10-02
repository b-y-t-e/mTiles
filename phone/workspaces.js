// The workspace list's two rules, the desktop's own: pinned rows first, and a filter that matches every
// typed word anywhere, in any order — so the phone narrows a list the way the panel on the computer does.

/** Pinned first, otherwise in the order mTiles sent them, which is already the panel's order. */
export function orderWorkspaces(workspaces) {
  return workspaces
    .map((ws, i) => ({ ws, i }))
    .sort((a, b) => (b.ws.favorite === true) - (a.ws.favorite === true) || a.i - b.i)
    .map((x) => x.ws);
}

/** Every word of the query somewhere in the name or the branch, ignoring case. */
export function matchesQuery(ws, query) {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length === 0) return true;
  const haystack = `${ws.name ?? ""} ${ws.branch ?? ""}`.toLowerCase();
  return words.every((w) => haystack.includes(w));
}

/** The panel shows its filter past three rows; so does the phone. */
export const FILTER_THRESHOLD = 3;
