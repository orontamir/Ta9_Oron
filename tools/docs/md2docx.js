// Converts the project's Markdown documents (headings, paragraphs, lists, tables, code blocks, images) into styled .docx.
// Diagrams (docs/hld/images) get their own page, landscape when wide, so they stay readable; other images
// (screenshots) stay inline in the text, scaled to the page width.
//   node md2docx.js <input.md> <output.docx> [title] [subtitle] [footer]
const fs = require('fs');
const path = require('path');
const {
  Document, Packer, Paragraph, TextRun, HeadingLevel, Table, TableRow, TableCell, WidthType, ShadingType,
  ImageRun, AlignmentType, PageOrientation, Footer, PageNumber, LevelFormat, TableOfContents,
  BorderStyle, ExternalHyperlink, PageBreak,
} = require('docx');

const [, , mdPath, outPath, TITLE = 'High-Level Design', SUBTITLE = 'TA9 Real-Time Document Processing System',
  FOOTER = 'TA9 Document Processing — High-Level Design'] = process.argv;
const baseDir = path.dirname(mdPath);
const lines = fs.readFileSync(mdPath, 'utf8').split(/\r?\n/);

// ---------- page geometry (A4, DXA) ----------
const A4 = { w: 11906, h: 16838 };
const MARGIN = 1134;                       // 2 cm
const PORTRAIT_TEXT_W = A4.w - 2 * MARGIN; // 9638
const DXA_PER_PX = 15;                     // 1 px = 15 DXA at 96 dpi
const FONT = 'Calibri';
const ACCENT = '1F4E79';

// ---------- inline markdown → runs ----------
function inlineRuns(text, base = {}) {
  const runs = [];
  const re = /(\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`|\[[^\]]+\]\([^)]+\))/g; // **bold**, *italic*, `code`, [text](url)
  let last = 0;
  let m;
  const push = (t, extra = {}) => { if (t) runs.push(new TextRun({ text: t, font: FONT, ...base, ...extra })); };
  text = text.replace(/\\\|/g, '|').replace(/&gt;/g, '>').replace(/&lt;/g, '<').replace(/&nbsp;/g, ' ');
  while ((m = re.exec(text)) !== null) {
    push(text.slice(last, m.index));
    const tok = m[0];
    if (tok.startsWith('**')) {
      runs.push(...inlineRuns(tok.slice(2, -2), { ...base, bold: true }));
    } else if (tok.startsWith('`')) {
      push(tok.slice(1, -1), { font: 'Consolas', size: (base.size || 21) - 2, color: '5B2C6F' });
    } else if (tok.startsWith('[')) {
      const [, label, url] = tok.match(/\[([^\]]+)\]\(([^)]+)\)/);
      const labelRuns = inlineRuns(label, { ...base, color: '0563C1', underline: {} });
      if (/^https?:/.test(url)) runs.push(new ExternalHyperlink({ link: url, children: labelRuns }));
      else runs.push(...labelRuns);
    } else {
      runs.push(...inlineRuns(tok.slice(1, -1), { ...base, italics: true }));
    }
    last = m.index + tok.length;
  }
  push(text.slice(last));
  return runs;
}

// ---------- tables ----------
function parseRow(line) {
  const cells = [];
  let cur = '';
  const body = line.trim().replace(/^\|/, '').replace(/\|$/, '');
  for (let i = 0; i < body.length; i++) {
    if (body[i] === '\\' && body[i + 1] === '|') { cur += '\\|'; i++; continue; }
    if (body[i] === '|') { cells.push(cur.trim()); cur = ''; continue; }
    cur += body[i];
  }
  cells.push(cur.trim());
  return cells;
}

function buildTable(rows, width) {
  const cols = rows[0].length;
  const plain = (s) => s.replace(/[*`]/g, '').replace(/\[([^\]]+)\]\([^)]+\)/g, '$1');
  const weights = Array.from({ length: cols }, (_, c) => Math.min(40, Math.max(10, ...rows.map((r) => plain(r[c] || '').length))));
  const total = weights.reduce((a, b) => a + b, 0);
  const widths = weights.map((w) => Math.floor((w / total) * width));
  widths[cols - 1] += width - widths.reduce((a, b) => a + b, 0);
  const border = { style: BorderStyle.SINGLE, size: 4, color: 'BFBFBF' };
  const borders = { top: border, bottom: border, left: border, right: border };
  return new Table({
    width: { size: width, type: WidthType.DXA },
    columnWidths: widths,
    rows: rows.map((r, ri) => new TableRow({
      tableHeader: ri === 0 ? true : undefined, // emit only on the real header row
      children: widths.map((w, ci) => new TableCell({
        width: { size: w, type: WidthType.DXA },
        borders,
        shading: ri === 0 ? { type: ShadingType.CLEAR, color: 'auto', fill: 'DCE6F2' } : undefined,
        margins: { top: 60, bottom: 60, left: 100, right: 100 },
        children: [new Paragraph({ children: inlineRuns(r[ci] || '', { size: 18, bold: ri === 0 ? true : undefined }) })],
      })),
    })),
  });
}

function pngSize(file) {
  const b = fs.readFileSync(file);
  return { w: b.readUInt32BE(16), h: b.readUInt32BE(20) };
}

// ---------- document assembly ----------
const sections = [];
let current = [];
let pendingCaption = null; // "*draw.io page ...*" line preceding an image
let tocAdded = false;

const footer = () => new Footer({
  children: [new Paragraph({
    alignment: AlignmentType.CENTER,
    children: [
      new TextRun({ text: `${FOOTER}   ·   Page `, font: FONT, size: 16, color: '808080' }),
      new TextRun({ children: [PageNumber.CURRENT], font: FONT, size: 16, color: '808080' }),
      new TextRun({ text: ' of ', font: FONT, size: 16, color: '808080' }),
      new TextRun({ children: [PageNumber.TOTAL_PAGES], font: FONT, size: 16, color: '808080' }),
    ],
  })],
});

function flushPortrait() {
  if (current.length === 0) return;
  sections.push({
    properties: { page: { size: { width: A4.w, height: A4.h }, margin: { top: MARGIN, bottom: MARGIN, left: MARGIN, right: MARGIN } } },
    footers: { default: footer() },
    children: current,
  });
  current = [];
}

function addInlineImage(alt, file) {
  const { w, h } = pngSize(file);
  const maxWpx = PORTRAIT_TEXT_W / DXA_PER_PX;
  const maxHpx = (A4.h - 2 * MARGIN - 2400) / DXA_PER_PX;
  const scale = Math.min(1, maxWpx / w, maxHpx / h);
  current.push(
    new Paragraph({
      alignment: AlignmentType.CENTER, spacing: { before: 120 },
      children: [new ImageRun({ type: 'png', data: fs.readFileSync(file), transformation: { width: Math.round(w * scale), height: Math.round(h * scale) }, altText: { title: alt, description: alt, name: alt } })],
    }),
    new Paragraph({
      alignment: AlignmentType.CENTER, spacing: { before: 60, after: 200 },
      children: [new TextRun({ text: alt, font: FONT, size: 18, color: '808080', italics: true })],
    }),
  );
}

function addImageSection(alt, file, caption) {
  const { w, h } = pngSize(file);
  const landscape = w > h * 1.15;
  const pageW = landscape ? A4.h : A4.w;
  const pageH = landscape ? A4.w : A4.h;
  const maxWpx = (pageW - 2 * MARGIN) / DXA_PER_PX;
  const maxHpx = (pageH - 2 * MARGIN - 1400) / DXA_PER_PX; // room for title and caption
  const scale = Math.min(maxWpx / w, maxHpx / h);
  flushPortrait();
  sections.push({
    properties: {
      page: {
        size: { width: A4.w, height: A4.h, orientation: landscape ? PageOrientation.LANDSCAPE : PageOrientation.PORTRAIT },
        margin: { top: MARGIN, bottom: MARGIN, left: MARGIN, right: MARGIN },
      },
    },
    footers: { default: footer() },
    children: [
      new Paragraph({ spacing: { after: 120 }, children: [new TextRun({ text: alt, font: FONT, size: 26, bold: true, color: ACCENT })] }),
      new Paragraph({
        alignment: AlignmentType.CENTER,
        children: [new ImageRun({ type: 'png', data: fs.readFileSync(file), transformation: { width: Math.round(w * scale), height: Math.round(h * scale) }, altText: { title: alt, description: alt, name: alt } })],
      }),
      new Paragraph({
        alignment: AlignmentType.CENTER, spacing: { before: 80 },
        children: inlineRuns(caption ? `${caption} · editable source: docs/hld/diagrams/ta9-hld.drawio` : 'Source: docs/hld/diagrams/ta9-hld.drawio', { size: 16, color: '808080', italics: true }),
      }),
    ],
  });
}

current.push(
  new Paragraph({ spacing: { before: 2400, after: 200 }, children: [new TextRun({ text: TITLE, font: FONT, size: 56, bold: true, color: ACCENT })] }),
  new Paragraph({ spacing: { after: 600 }, children: [new TextRun({ text: SUBTITLE, font: FONT, size: 32, color: '404040' })] }),
);

let i = 0;
let skippedTitle = false;
while (i < lines.length) {
  const t = lines[i].trim();

  if (!skippedTitle && t.startsWith('# ')) { skippedTitle = true; i++; continue; } // H1 = title block
  if (t === '' || t === '---') { i++; continue; }

  const h = t.match(/^(#{2,4}) (.*)$/);
  if (h) {
    if (h[1] === '##' && !tocAdded) {
      current.push(new Paragraph({ children: [new PageBreak()] }));
      current.push(new Paragraph({ spacing: { after: 120 }, children: [new TextRun({ text: 'Contents', font: FONT, size: 32, bold: true, color: ACCENT })] }));
      current.push(new TableOfContents('Contents', { hyperlink: true, headingStyleRange: '1-3' }));
      current.push(new Paragraph({ children: [new PageBreak()] }));
      tocAdded = true;
    }
    const level = { '##': HeadingLevel.HEADING_1, '###': HeadingLevel.HEADING_2, '####': HeadingLevel.HEADING_3 }[h[1]];
    current.push(new Paragraph({ heading: level, children: inlineRuns(h[2]) }));
    i++;
    continue;
  }

  const img = t.match(/^!\[([^\]]*)\]\(([^)]+)\)$/);
  if (img) {
    const file = path.resolve(baseDir, img[2]);
    if (/[\\/]hld[\\/]images[\\/]/.test(file)) {
      addImageSection(img[1], file, pendingCaption);
    } else {
      addInlineImage(img[1], file);
    }
    pendingCaption = null;
    i++;
    continue;
  }

  if (t.startsWith('```')) {
    const code = [];
    i++;
    while (i < lines.length && !lines[i].trim().startsWith('```')) { code.push(lines[i].replace(/\t/g, '    ')); i++; }
    i++; // closing fence
    code.forEach((c, k) => current.push(new Paragraph({
      shading: { type: ShadingType.CLEAR, color: 'auto', fill: 'F3F3F3' },
      border: k === 0 ? { top: { style: BorderStyle.SINGLE, size: 4, color: 'D9D9D9', space: 4 } }
        : (k === code.length - 1 ? { bottom: { style: BorderStyle.SINGLE, size: 4, color: 'D9D9D9', space: 4 } } : undefined),
      spacing: { before: 0, after: 0, line: 260 },
      indent: { left: 120, right: 120 },
      keepNext: k < code.length - 1,
      children: [new TextRun({ text: c.length ? c : ' ', font: 'Consolas', size: 17, color: '1F1F1F' })],
    })));
    current.push(new Paragraph({ spacing: { after: 120 }, children: [] }));
    continue;
  }

  if (/^\*draw\.io page/.test(t)) { pendingCaption = t.replace(/^\*|\*$/g, ''); i++; continue; }

  if (t.startsWith('|')) {
    const rows = [];
    while (i < lines.length && lines[i].trim().startsWith('|')) {
      const r = parseRow(lines[i]);
      if (!r.every((c) => /^:?-{3,}:?$/.test(c) || c === '')) rows.push(r);
      i++;
    }
    if (rows.length && rows[0].every((c) => c === '')) rows.shift(); // metadata tables have an empty header row
    current.push(buildTable(rows, PORTRAIT_TEXT_W));
    current.push(new Paragraph({ spacing: { after: 120 }, children: [] }));
    continue;
  }

  if (/^[-*] /.test(t)) {
    while (i < lines.length && /^\s*[-*] /.test(lines[i])) {
      const depth = lines[i].match(/^\s*/)[0].length >= 2 ? 1 : 0;
      current.push(new Paragraph({ numbering: { reference: 'bullets', level: depth }, children: inlineRuns(lines[i].trim().slice(2)) }));
      i++;
    }
    continue;
  }

  if (/^\d+\. /.test(t)) {
    while (i < lines.length && /^\d+\. /.test(lines[i].trim())) {
      current.push(new Paragraph({ numbering: { reference: 'numbers', level: 0 }, children: inlineRuns(lines[i].trim().replace(/^\d+\. /, '')) }));
      i++;
    }
    continue;
  }

  if (t.startsWith('> ')) {
    current.push(new Paragraph({
      border: { left: { style: BorderStyle.SINGLE, size: 12, color: ACCENT, space: 8 } },
      indent: { left: 240 }, children: inlineRuns(t.slice(2), { color: '404040' }),
    }));
    i++;
    continue;
  }

  const para = [t];
  i++;
  while (i < lines.length && lines[i].trim() !== '' && !/^(#|\||[-*] |\d+\. |!\[|> |---|```)/.test(lines[i].trim())) {
    para.push(lines[i].trim());
    i++;
  }
  current.push(new Paragraph({ spacing: { after: 120 }, children: inlineRuns(para.join(' ')) }));
}
flushPortrait();

const doc = new Document({
  creator: 'Oron Tamir',
  title: FOOTER,
  features: { updateFields: true },
  styles: {
    default: { document: { run: { font: FONT, size: 21 } } },
    paragraphStyles: [
      { id: 'Heading1', name: 'Heading 1', basedOn: 'Normal', next: 'Normal', quickFormat: true,
        run: { size: 32, bold: true, color: ACCENT, font: FONT }, paragraph: { spacing: { before: 360, after: 160 }, outlineLevel: 0 } },
      { id: 'Heading2', name: 'Heading 2', basedOn: 'Normal', next: 'Normal', quickFormat: true,
        run: { size: 26, bold: true, color: '2E75B6', font: FONT }, paragraph: { spacing: { before: 240, after: 120 }, outlineLevel: 1 } },
      { id: 'Heading3', name: 'Heading 3', basedOn: 'Normal', next: 'Normal', quickFormat: true,
        run: { size: 23, bold: true, color: '404040', font: FONT }, paragraph: { spacing: { before: 200, after: 100 }, outlineLevel: 2 } },
    ],
  },
  numbering: {
    config: [
      { reference: 'bullets', levels: [
        { level: 0, format: LevelFormat.BULLET, text: '•', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 540, hanging: 270 } } } },
        { level: 1, format: LevelFormat.BULLET, text: '◦', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 1080, hanging: 270 } } } },
      ] },
      { reference: 'numbers', levels: [
        { level: 0, format: LevelFormat.DECIMAL, text: '%1.', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 540, hanging: 300 } } } },
      ] },
    ],
  },
  sections,
});

Packer.toBuffer(doc).then((buf) => {
  fs.writeFileSync(outPath, buf);
  console.log(`wrote ${outPath} (${(buf.length / 1024).toFixed(0)} KB, ${sections.length} sections)`);
});
