// How long ago, in the fewest characters that still read: a card in the miniature is often 80px wide.

/** "" under a minute — a tile that moved seconds ago is still moving as far as a glance cares. */
export function ago(ms) {
  if (!(ms >= 60_000)) return "";
  const minutes = Math.floor(ms / 60_000);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h`;
  return `${Math.floor(hours / 24)}d`;
}
