// The workspace list's two rules: an order that puts what is open first, and a filter that matches every
// typed word anywhere, in any order — so the phone narrows a list the way the panel on the computer does.

/** Three groups, each alphabetical: the workspaces open on the computer, then the pinned ones that are
 *  not, then the rest. On a phone the open ones are where the work is — the ones that can be zoomed into
 *  without starting anything — so they lead, whatever is pinned. */
export function orderWorkspaces(workspaces) {
  const group = (ws) => (ws.loaded === true ? 0 : ws.favorite === true ? 1 : 2);
  const byName = new Intl.Collator(undefined, { sensitivity: "base", numeric: true });
  return workspaces
    .map((ws, i) => ({ ws, i }))
    .sort((a, b) => group(a.ws) - group(b.ws) || byName.compare(a.ws.name ?? "", b.ws.name ?? "") || a.i - b.i)
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
