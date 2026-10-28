// A small markdown reader for what an agent writes, and a renderer that builds DOM nodes.
//
// Never innerHTML. Everything shown here is text a model chose, and this page's origin holds a pairing
// that can type into somebody's terminals: an injected script is a shell on the paired machine. So the
// parser produces a tree of plain objects (pure, and tested under node), and the renderer turns that tree
// into elements whose text is only ever set through textContent. Links are drawn only for http and https.

/** Parses markdown into blocks. Pure. */
export function parse(source) {
  const lines = String(source ?? "").replace(/\r\n?/g, "\n").split("\n");
  const blocks = [];
  let i = 0;

  while (i < lines.length) {
    const line = lines[i];

    if (/^\s*$/.test(line)) { i++; continue; }

    const fence = line.match(/^\s*(```+|~~~+)\s*([\w+-]*)/);
    if (fence) {
      const close = fence[1];
      const body = [];
      i++;
      while (i < lines.length && !lines[i].trimStart().startsWith(close)) body.push(lines[i++]);
      i++; // the closing fence, or the end
      blocks.push({ type: "code", lang: fence[2] || null, text: body.join("\n") });
      continue;
    }

    const heading = line.match(/^(#{1,6})\s+(.*)$/);
    if (heading) {
      blocks.push({ type: "heading", level: heading[1].length, inline: inline(heading[2].replace(/\s+#+\s*$/, "")) });
      i++;
      continue;
    }

    if (/^\s*([-*_])(\s*\1){2,}\s*$/.test(line)) { blocks.push({ type: "rule" }); i++; continue; }

    if (/^\s*>/.test(line)) {
      const body = [];
      while (i < lines.length && /^\s*>/.test(lines[i])) body.push(lines[i++].replace(/^\s*>\s?/, ""));
      blocks.push({ type: "quote", blocks: parse(body.join("\n")) });
      continue;
    }

    // A table is kept as the rows it was written as: on a phone a grid of columns is wider than the
    // screen, and a pipe-separated row reads well enough in a monospace face.
    if (/^\s*\|.*\|\s*$/.test(line)) {
      const rows = [];
      while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) {
        if (!/^\s*\|[\s:|-]+\|\s*$/.test(lines[i])) rows.push(lines[i].trim());
        i++;
      }
      blocks.push({ type: "code", lang: "table", text: rows.join("\n") });
      continue;
    }

    const item = listItem(line);
    if (item) {
      const ordered = item.ordered;
      const items = [];
      while (i < lines.length) {
        const next = listItem(lines[i]);
        if (next && next.ordered === ordered && next.indent <= item.indent + 1) {
          items.push([next.text]);
          i++;
        } else if (items.length && /^\s{2,}\S/.test(lines[i]) && !listItem(lines[i])) {
          items[items.length - 1].push(lines[i].trim());
          i++;
        } else if (items.length && next && next.indent > item.indent + 1) {
          items[items.length - 1].push(lines[i].trim());
          i++;
        } else {
          break;
        }
      }
      blocks.push({ type: "list", ordered, start: item.start, items: items.map((t) => inline(t.join(" "))) });
      continue;
    }

    const para = [];
    while (i < lines.length && !/^\s*$/.test(lines[i]) && !startsBlock(lines[i])) para.push(lines[i++].trim());
    blocks.push({ type: "paragraph", inline: inline(para.join("\n")) });
  }

  return blocks;
}

function listItem(line) {
  const m = line.match(/^(\s*)([-*+]|(\d{1,9})[.)])\s+(.*)$/);
  if (!m) return null;
  return { indent: m[1].length, ordered: m[3] !== undefined, start: m[3] ? Number(m[3]) : 1, text: m[4] };
}

function startsBlock(line) {
  return /^\s*(```|~~~)/.test(line) || /^#{1,6}\s/.test(line) || /^\s*>/.test(line) || listItem(line) !== null
    || /^\s*\|.*\|\s*$/.test(line);
}

/** Parses the inline part of a block into spans. Pure. */
export function inline(text) {
  const out = [];
  let rest = String(text ?? "");

  const rules = [
    { re: /^`([^`]+)`/, make: (m) => ({ type: "code", text: m[1] }) },
    { re: /^\*\*([^*]+?)\*\*/, make: (m) => ({ type: "strong", children: inline(m[1]) }) },
    { re: /^__([^_]+?)__/, make: (m) => ({ type: "strong", children: inline(m[1]) }) },
    { re: /^\*([^*\s][^*]*?)\*/, make: (m) => ({ type: "em", children: inline(m[1]) }) },
    { re: /^_([^_\s][^_]*?)_(?![A-Za-z0-9])/, make: (m) => ({ type: "em", children: inline(m[1]) }) },
    { re: /^\[([^\]]+)\]\(([^)\s]+)(?:\s+"[^"]*")?\)/, make: (m) => link(m[1], m[2]) },
    { re: /^<(https?:\/\/[^>\s]+)>/, make: (m) => address(m[1]) },
    // A bare address, as an agent writes one: a link a thumb can open, not text to copy out by hand.
    { re: BARE_URL_AT_START, make: (m) => address(m[0]) },
    { re: /^\n/, make: () => ({ type: "break" }) },
  ];

  let text_ = "";
  const flush = () => { if (text_) { out.push({ type: "text", text: text_ }); text_ = ""; } };

  outer: while (rest.length) {
    for (const rule of rules) {
      const m = rest.match(rule.re);
      if (m) {
        flush();
        out.push(rule.make(m));
        rest = rest.slice(m[0].length);
        continue outer;
      }
    }
    // Plain text runs up to the next character that could start a span.
    const next = rest.slice(1).search(/[`*_[<\n]|https?:\/\//);
    const take = next < 0 ? rest.length : next + 1;
    text_ += rest.slice(0, take);
    rest = rest.slice(take);
  }
  flush();
  return out;
}

/**
 * A bare http(s) address. It ends before whitespace, a quote or a bracket, and never on the punctuation a
 * sentence puts after an address — "see https://x.y/z." links to https://x.y/z.
 */
const BARE_URL = /https?:\/\/[^\s<>"'`]+[^\s<>"'`.,;:!?)\]}]/g;
const BARE_URL_AT_START = new RegExp(`^${BARE_URL.source}`);

/**
 * The rest of a broken address at the start of a line: it has to carry a character only an address would
 * (a slash, a query, a dot inside it). The widest line of a screen is only a guess at the terminal's width,
 * and an address ending the widest line followed by "press q to quit" must not take "press" into its link.
 */
const ADDRESS_CONTINUATION = /^(?=[^\s<>"'`]*[\/?#=&%~.][^\s<>"'`]*[^\s<>"'`.,;:!?)\]}])[^\s<>"'`]*[^\s<>"'`.,;:!?)\]}]/;

/** Plain text with its bare addresses taken out as links — for text that is not markdown: a terminal's
 *  screen, a message typed by the user. */
export function linkSpans(text) {
  const out = [];
  let at = 0;
  for (const m of String(text ?? "").matchAll(BARE_URL)) {
    if (m.index > at) out.push({ type: "text", text: text.slice(at, m.index) });
    out.push(address(m[0]));
    at = m.index + m[0].length;
  }
  if (at < String(text ?? "").length) out.push({ type: "text", text: text.slice(at) });
  return out;
}

/**
 * {@link linkSpans} for a terminal's lines, where a long address is broken across several of them at the
 * terminal's width. An address running to the end of a line that `filled` the width goes on at the start
 * of the next, and every piece links to the whole address rather than to the part on its own line.
 */
export function linkSpansAcross(lines, filled) {
  const out = lines.map((line) => linkSpans(line));
  let pieces = [];
  const finish = () => {
    const href = pieces.map((p) => p.children[0].text).join("").match(BARE_URL_AT_START)?.[0] ?? null;
    for (const piece of pieces) piece.href = href;
    pieces = [];
  };
  out.forEach((spans, i) => {
    const text = String(lines[i] ?? "");
    if (pieces.length) {
      const whole = /^[^\s<>"'`]+/.exec(text);
      const runsOn = whole && whole[0].length === text.length && filled[i];
      const go = runsOn ? whole : ADDRESS_CONTINUATION.exec(text);
      if (!go) finish();
      else {
        const piece = { type: "link", href: null, children: [{ type: "text", text: go[0] }] };
        pieces.push(piece);
        const rest = text.slice(go[0].length);
        out[i] = [piece, ...linkSpans(rest)];
        if (rest.length || !filled[i]) finish();
        return;
      }
    }
    const last = spans.at(-1);
    if (last?.type === "link" && filled[i] && text.endsWith(last.children[0].text)) pieces.push(last);
  });
  if (pieces.length) finish();
  return out;
}

/** Spans built as DOM. */
export function renderSpans(spans, doc = globalThis.document) {
  return renderInline(spans, doc);
}

/** {@link linkSpans}, built as DOM. */
export function renderLinked(text, doc = globalThis.document) {
  return renderInline(linkSpans(text), doc);
}

/** An address shown as itself — its label is the address as text, never parsed again. */
function address(url) {
  const safe = /^https?:\/\//i.test(url) ? url : null;
  return { type: "link", href: safe, children: [{ type: "text", text: url }] };
}

function link(label, href) {
  // Only the web. A `javascript:` or `data:` link is the one way a markdown link could run something.
  const safe = /^https?:\/\//i.test(href) ? href : null;
  return { type: "link", href: safe, children: withoutLinks(inline(label)) };
}

/** A link's label with any address in it kept as text: a link inside a link is not HTML, and a tap on its
 *  label must open the link's own address. */
function withoutLinks(spans) {
  return spans.flatMap((span) => span.type === "link" ? withoutLinks(span.children)
    : span.children ? [{ ...span, children: withoutLinks(span.children) }] : [span]);
}

/** Builds the DOM for parsed blocks. `doc` is the document, so this module stays importable in node. */
export function render(blocks, doc = globalThis.document) {
  const fragment = doc.createDocumentFragment();
  for (const block of blocks) fragment.append(renderBlock(block, doc));
  return fragment;
}

function renderBlock(block, doc) {
  switch (block.type) {
    case "heading": {
      const el = doc.createElement(`h${Math.min(6, block.level + 2)}`);
      el.append(renderInline(block.inline, doc));
      return el;
    }
    case "code": {
      const pre = doc.createElement("pre");
      const code = doc.createElement("code");
      code.textContent = block.text;
      if (block.lang === "diff") {
        // Coloured by line, the way the desktop colours a diff: the one kind of code whose meaning is in
        // its first character.
        code.textContent = "";
        for (const line of block.text.split("\n")) {
          const span = doc.createElement("span");
          span.className = line.startsWith("+") ? "diff-add" : line.startsWith("-") ? "diff-del" : line.startsWith("@@") ? "diff-hunk" : "";
          span.textContent = line + "\n";
          code.append(span);
        }
      }
      pre.append(code);
      return pre;
    }
    case "quote": {
      const el = doc.createElement("blockquote");
      el.append(render(block.blocks, doc));
      return el;
    }
    case "rule":
      return doc.createElement("hr");
    case "list": {
      const el = doc.createElement(block.ordered ? "ol" : "ul");
      if (block.ordered && block.start !== 1) el.start = block.start;
      for (const item of block.items) {
        const li = doc.createElement("li");
        li.append(renderInline(item, doc));
        el.append(li);
      }
      return el;
    }
    default: {
      const p = doc.createElement("p");
      p.append(renderInline(block.inline, doc));
      return p;
    }
  }
}

function renderInline(spans, doc) {
  const fragment = doc.createDocumentFragment();
  for (const span of spans) {
    switch (span.type) {
      case "text": fragment.append(doc.createTextNode(span.text)); break;
      case "break": fragment.append(doc.createElement("br")); break;
      case "code": {
        const el = doc.createElement("code");
        el.append(renderInline(linkSpans(span.text), doc));
        fragment.append(el);
        break;
      }
      case "strong":
      case "em": {
        const el = doc.createElement(span.type);
        el.append(renderInline(span.children, doc));
        fragment.append(el);
        break;
      }
      case "link": {
        if (!span.href) { fragment.append(renderInline(span.children, doc)); break; }
        const a = doc.createElement("a");
        a.href = span.href;
        a.target = "_blank";
        a.rel = "noopener noreferrer";
        a.append(renderInline(span.children, doc));
        fragment.append(a);
        break;
      }
    }
  }
  return fragment;
}
