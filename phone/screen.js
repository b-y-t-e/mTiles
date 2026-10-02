// How one line of a terminal's screen is set on a phone.
//
// The screen is the computer's, many columns wider than a phone, so its lines wrap here. Three things
// make that readable rather than a wall of text: a wrapped line goes on under its own text, not at the
// left edge; a rule drawn across the terminal is drawn as a rule, not as dashes that wrap; and the few
// marks an agent's TUI begins its lines with — its answer, the user's prompt, its status — say what
// kind of line it is, so the page can set them apart without colours the screen text does not carry.

/** A line of nothing but box-drawing dashes: a rule drawn across the terminal, not text to fit. */
export const isRule = (line) => /^[\s─━═┄┈╌\-_]+$/.test(line) && /[─━═┄┈╌\-_]{3,}/.test(line);

/**
 * What a line is and how far its wrapped continuation is indented.
 * `indent` is the leading spaces; `hang` the marker's own width after them, so a continuation lines up
 * under the text rather than under the mark; `kind` one of: rule, prompt (what the user typed), answer
 * (the agent speaking), status (its working/done line), tool (a tool's output), footer (the mode bar),
 * text.
 */
export function classify(line) {
  if (isRule(line)) return { kind: "rule", indent: 0, hang: 0, text: "" };
  const indent = line.length - line.trimStart().length;
  const text = line.slice(indent);
  const marker = /^(❯|>|●|⏺|✻|✶|✳|✢|·|\*|⎿|└|•|-|\d+\.)\s+/.exec(text);
  const hang = marker ? [...marker[0]].length : 0;
  let kind = "text";
  if (/^(❯|>)\s/.test(text)) kind = "prompt";
  else if (/^(●|⏺)\s/.test(text)) kind = "answer";
  else if (/^(✻|✶|✳|✢)\s/.test(text)) kind = "status";
  else if (/^(⎿|└)\s/.test(text)) kind = "tool";
  else if (/^(⏵|⏸|\?\s+for shortcuts)|shift\+tab to cycle|esc to interrupt/.test(text)) kind = "footer";
  return { kind, indent, hang, text };
}

/** The TUI marks a phone's fonts may not carry — Android's often lack the media and bracket symbols —
 *  as the nearest ones they do, rather than as empty boxes. */
const STANDINS = { "⏵": "▸", "⏸": "‖", "⏺": "●", "⎿": "└", "✻": "*", "✶": "*", "✳": "*", "✢": "*" };
export const displayable = (text) => text.replace(/[⏵⏸⏺⎿✻✶✳✢]/gu, (c) => STANDINS[c] ?? c);

/** Blank lines at the top and the bottom of a screen are the terminal's height, not content. */
export function trimBlank(lines) {
  let start = 0;
  let end = lines.length;
  while (start < end && !lines[start].trim()) start++;
  while (end > start && !lines[end - 1].trim()) end--;
  return lines.slice(start, end);
}
