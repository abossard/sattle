// The Markdown of a document graph (engine/PROTOCOL.md, Version 3.4) as blocks. The engine writes a fixed subset: headings, paragraphs,
// bulleted lists and tables, with every punctuation character of graph data escaped, so there is no inline syntax to read, only the
// backslash escapes to undo.
export type DocBlock =
  | { kind: "heading"; level: number; text: string }
  | { kind: "paragraph"; text: string }
  | { kind: "list"; items: string[] }
  | { kind: "table"; header: string[]; rows: string[][] };

const unescape = (s: string): string => s.replace(/\\(.)/g, "$1");

/** Cells of a table line `| a | b |`: split at the pipes that are not escaped. */
function cells(line: string): string[] {
  const out: string[] = [];
  let cur = "";
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (ch === "\\" && i + 1 < line.length) { cur += ch + line[++i]; continue; }
    if (ch === "|") { out.push(cur); cur = ""; continue; }
    cur += ch;
  }
  out.push(cur);
  return out.slice(1, -1).map((c) => unescape(c.trim()));
}

const separator = (line: string) => /^\|(\s*:?-{3,}:?\s*\|)+\s*$/.test(line);

export function parseDocument(markdown: string): DocBlock[] {
  const lines = markdown.split("\n");
  const blocks: DocBlock[] = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (!line.trim()) continue;
    const h = /^(#{1,6}) (.*)$/.exec(line);
    if (h) { blocks.push({ kind: "heading", level: h[1].length, text: unescape(h[2]) }); continue; }
    if (line.startsWith("- ")) {
      const items = [unescape(line.slice(2))];
      while (i + 1 < lines.length && lines[i + 1].startsWith("- ")) items.push(unescape(lines[++i].slice(2)));
      blocks.push({ kind: "list", items });
      continue;
    }
    if (line.startsWith("|") && i + 1 < lines.length && separator(lines[i + 1])) {
      const header = cells(line), rows: string[][] = [];
      i++;
      while (i + 1 < lines.length && lines[i + 1].startsWith("|")) rows.push(cells(lines[++i]));
      blocks.push({ kind: "table", header, rows });
      continue;
    }
    const para = [line];
    while (i + 1 < lines.length && lines[i + 1].trim() && !/^(#{1,6} |- |\|)/.test(lines[i + 1])) para.push(lines[++i]);
    blocks.push({ kind: "paragraph", text: unescape(para.join(" ")) });
  }
  return blocks;
}

/** The head of a document for a small view: the first blocks, each table cut to its first rows. `cut` says whether anything was left out. */
export function previewBlocks(blocks: DocBlock[], limits: { blocks: number; tableRows: number } = { blocks: 8, tableRows: 4 }): { blocks: DocBlock[]; cut: boolean } {
  let cut = blocks.length > limits.blocks;
  const shown = blocks.slice(0, limits.blocks).map((b): DocBlock => {
    if (b.kind === "table" && b.rows.length > limits.tableRows) { cut = true; return { ...b, rows: b.rows.slice(0, limits.tableRows) }; }
    if (b.kind === "list" && b.items.length > 6) { cut = true; return { ...b, items: b.items.slice(0, 6) }; }
    return b;
  });
  return { blocks: shown, cut };
}
