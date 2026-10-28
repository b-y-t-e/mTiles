// The list of computers' rules, pure: what a paired computer is doing as far as the list says it, and
// what an invitation is out of whatever the user pasted.

const UP = new Set(["connected", "reconnecting"]);

/** Whether a computer's link is up, so what it last said about its workspaces still counts. */
export const isUp = (m) => UP.has(m.connState);

/** How many of a computer's workspaces are waiting for you, and how many are working. A computer out of
 *  reach counts as neither: what it said before it went is no longer true. */
export function attentionOf(m) {
  if (!isUp(m)) return { activity: null, blocked: 0, working: 0 };
  const blocked = m.workspaces.filter((w) => w.activity === "blocked").length;
  const working = m.workspaces.filter((w) => w.activity === "working").length;
  return { activity: blocked ? "blocked" : working ? "working" : null, blocked, working };
}

const CONNECTION_STATUS = {
  connecting: "Connecting…",
  reconnecting: "Reconnecting…",
  refused: "Not paired any more",
  failed: "Not reachable",
  "page-old": "Reload this page to reach it",
  "app-old": "mTiles there needs updating",
};

const counted = (n, one, many) => (n === 1 ? one : `${n} ${many}`);

/** What a computer's row says under its name. */
export function computerStatus(m) {
  const connection = CONNECTION_STATUS[m.connState];
  if (connection) return connection;
  const { blocked, working } = attentionOf(m);
  if (blocked && working) return `${blocked} waiting for you, ${working} working`;
  if (blocked) return counted(blocked, "1 workspace waiting for you", "workspaces waiting for you");
  if (working) return counted(working, "1 workspace working", "workspaces working");
  return m.workspaces.length ? "Nothing running" : "No workspaces";
}

/** The invitation code in what the user pasted: the whole link, or only the part after its `#`. Null
 *  for nothing at all, or for text that does not decode. */
export function invitationCodeFrom(text) {
  const trimmed = text?.trim();
  if (!trimmed) return null;
  const raw = trimmed.includes("#") ? trimmed.slice(trimmed.indexOf("#") + 1) : trimmed;
  try { return decodeURIComponent(raw.trim()) || null; } catch { return null; }
}
