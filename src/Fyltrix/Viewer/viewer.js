/* Fyltrix viewer – renders files inside the preview WebView2.
   Query string:
     mode=view&src=<fs url>&kind=<kind>&name=<file name>&lang=<hljs language>
       optional: props=<metadata api>, render=<png rendition api>, thumb=<thumbnail api>, subs=<subtitle fs url>, api=<archive api>
     mode=diff&a=<fs url>&b=<fs url>&an=<name>&bn=<name>
     mode=msg&title=..&text=..
   Messages to the host: escape, space, prev, next, copy-image, image-tools, media-unsupported
*/
'use strict';

const q = new URLSearchParams(location.search);
const root = document.getElementById('root');
const toolbar = document.getElementById('toolbar');

const TEXT_MAX = 20 * 1024 * 1024;      // never load more than 20 MB of text
const HIGHLIGHT_MAX = 1.5 * 1024 * 1024; // syntax highlight only below this
const TABLE_ROWS_MAX = 5000;
const WAVEFORM_MAX = 80 * 1024 * 1024;  // decode audio for a waveform only below this

let activeMedia = null;   // <video>/<audio> currently shown: Space plays/pauses it
let keyHandler = null;    // per-view keyboard handling (image zoom, frame stepping)

/* Libraries load when a preview needs them, not on every page: plain text needs none, Markdown needs marked + DOMPurify,
   a spreadsheet the 880 KB SheetJS, etc. (they were all parsed for every file before). Each loads once per page. */
const LIBS = {
  marked: 'lib/marked.js', purify: 'lib/purify.min.js', hljs: 'lib/highlight.min.js', jszip: 'lib/jszip.min.js',
  docx: 'lib/docx-preview.min.js', xlsx: 'lib/xlsx.full.min.js', papa: 'lib/papaparse.min.js', diff: 'lib/diff.min.js',
};
const libLoads = {};
function need(...names) {
  return Promise.all(names.map(n => libLoads[n] ??= new Promise((ok, fail) => {
    const s = document.createElement('script');
    s.src = LIBS[n];
    s.onload = ok;
    s.onerror = () => fail(new Error(`Couldn't load the ${n} viewer library.`));
    document.head.append(s);
  })));
}

function post(msg) { try { window.chrome?.webview?.postMessage(msg); } catch { } }
document.addEventListener('keydown', e => {
  const tag = document.activeElement?.tagName || '';
  if (/INPUT|TEXTAREA|SELECT/.test(tag)) { if (e.key === 'Escape') post('escape'); return; }
  if (keyHandler && keyHandler(e)) { e.preventDefault(); return; }
  if (e.key === 'Escape') post('escape');
  if (e.key === ' ' && activeMedia) { e.preventDefault(); activeMedia.paused ? activeMedia.play() : activeMedia.pause(); return; }
  if (e.key === ' ' && tag !== 'BUTTON') post('space');
});

function el(tag, attrs, ...children) {
  const n = document.createElement(tag);
  if (attrs) for (const [k, v] of Object.entries(attrs)) {
    if (k === 'class') n.className = v;
    else if (k === 'text') n.textContent = v;
    else if (k.startsWith('on')) n.addEventListener(k.substring(2), v);
    else n.setAttribute(k, v);
  }
  for (const c of children) if (c != null) n.append(c);
  return n;
}

function message(title, text, isError) {
  root.replaceChildren(el('div', { class: 'msg' + (isError ? ' error' : '') }, el('h2', { text: title }), text ? el('div', { text }) : null));
}

function bar(title, ...items) {
  toolbar.hidden = false;
  toolbar.replaceChildren(el('span', { class: 'title', text: title || '' }), ...items.filter(Boolean));
}

function button(label, onclick, on) {
  const b = el('button', { text: label, class: on ? 'on' : '' });
  b.addEventListener('click', () => onclick(b));
  return b;
}

async function fetchBytes(url, max) {
  const u = max ? url + (url.includes('?') ? '&' : '?') + 'max=' + max : url;
  const r = await fetch(u);
  if (!r.ok) throw new Error(`Couldn't read the file (${r.status} ${r.statusText}).`);
  const total = Number(r.headers.get('X-File-Size') || 0);
  const buf = await r.arrayBuffer();
  return { buf, total: total || buf.byteLength };
}

function decodeText(buf) {
  const b = new Uint8Array(buf);
  if (b[0] === 0xEF && b[1] === 0xBB && b[2] === 0xBF) return new TextDecoder('utf-8').decode(b.subarray(3));
  if (b[0] === 0xFF && b[1] === 0xFE) return new TextDecoder('utf-16le').decode(b.subarray(2));
  if (b[0] === 0xFE && b[1] === 0xFF) return new TextDecoder('utf-16be').decode(b.subarray(2));
  try { return new TextDecoder('utf-8', { fatal: true }).decode(b); }
  catch { return new TextDecoder('windows-1252').decode(b); }
}

function looksBinary(buf) {
  const b = new Uint8Array(buf, 0, Math.min(buf.byteLength, 8000));
  if (b[0] === 0xFF && b[1] === 0xFE || b[0] === 0xFE && b[1] === 0xFF) return false; // UTF-16 text
  let odd = 0;
  for (let i = 0; i < b.length; i++) {
    if (b[i] === 0) return true;
    if (b[i] < 9 || (b[i] > 13 && b[i] < 32)) odd++;
  }
  return b.length > 0 && odd / b.length > 0.1;
}

async function loadText(src) {
  const { buf, total } = await fetchBytes(src, TEXT_MAX);
  return { text: decodeText(buf), truncated: total > buf.byteLength, total };
}

function fmtSize(n) {
  if (n < 1024) return n + ' B';
  const u = ['KB', 'MB', 'GB']; let i = -1;
  do { n /= 1024; i++; } while (n >= 1024 && i < u.length - 1);
  return (n < 10 ? n.toFixed(1) : Math.round(n)) + ' ' + u[i];
}

function escapeHtml(s) {
  return s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/* ------------------------------------------------------------------ code / text */
function codeView(text, lang, opts = {}) {
  let wrap = !!opts.wrap;
  const lines = text.split(/\r\n|\r|\n/);
  const container = el('div');
  const draw = () => {
    const gutter = el('div', { class: 'gutter' });
    gutter.textContent = lines.map((_, i) => i + 1).join('\n');
    const pre = el('pre');
    const code = el('code', { class: 'hljs' });
    let highlighted = false;
    const wantsHighlight = lang !== 'plaintext' && text.length <= HIGHLIGHT_MAX && (lang || opts.autoDetect);
    if (wantsHighlight && !window.hljs) need('hljs').then(draw, () => { });
    if (wantsHighlight && window.hljs) {
      try {
        const res = lang && hljs.getLanguage(lang) ? hljs.highlight(text, { language: lang, ignoreIllegals: true })
          : (opts.autoDetect ? hljs.highlightAuto(text.slice(0, 200000)) : null);
        if (res && (lang || res.relevance > 5)) { code.innerHTML = res.value; highlighted = true; }
      } catch { }
    }
    if (!highlighted) code.textContent = text;
    pre.append(code);
    container.replaceChildren(el('div', { class: 'code' + (wrap ? ' wrap' : '') }, gutter, pre));
  };
  draw();
  const wrapBtn = button('Wrap', b => { wrap = !wrap; b.classList.toggle('on', wrap); draw(); }, wrap);
  return { node: container, wrapBtn, lineCount: lines.length };
}

async function renderCode(src, name, lang, plain) {
  const [{ text, truncated, total }] = await Promise.all([loadText(src), plain ? null : need('hljs')]);
  const v = codeView(text, plain ? 'plaintext' : lang, { wrap: plain, autoDetect: !plain && !lang });
  bar(`${v.lineCount.toLocaleString()} lines · ${fmtSize(total)}`, v.wrapBtn);
  root.replaceChildren(...(truncated ? [el('div', { class: 'banner', text: `Showing the first ${fmtSize(TEXT_MAX)} only.` })] : []), v.node);
}

/* ------------------------------------------------------------------ markdown */
async function renderMarkdown(src, name) {
  const [{ text }] = await Promise.all([loadText(src), need('marked', 'purify')]);
  if (/^\s*(```|~~~|    \S)/m.test(text)) await need('hljs'); // code blocks
  let raw = false;
  const base = document.createElement('base');
  base.href = src.substring(0, src.lastIndexOf('/') + 1);
  const draw = () => {
    if (raw) {
      root.replaceChildren(codeView(text, 'markdown', { wrap: true }).node);
      return;
    }
    const html = marked.parse(text, { gfm: true, breaks: false });
    const body = el('article', { class: 'markdown-body' });
    if (!document.head.contains(base)) document.head.prepend(base);
    body.innerHTML = DOMPurify.sanitize(html, { USE_PROFILES: { html: true } });
    body.querySelectorAll('pre code').forEach(c => {
      const m = /language-([\w+#-]+)/.exec(c.className);
      if (!window.hljs) return;
      try {
        if (m && hljs.getLanguage(m[1])) c.innerHTML = hljs.highlight(c.textContent, { language: m[1], ignoreIllegals: true }).value;
        else hljs.highlightElement(c);
      } catch { }
      c.classList.add('hljs');
    });
    root.replaceChildren(body);
  };
  draw();
  bar('Markdown', button('Source', b => { raw = !raw; b.classList.toggle('on', raw); draw(); }));
}

/* ------------------------------------------------------------------ json */
function jsonValue(v) {
  if (v === null) return el('span', { class: 'null', text: 'null' });
  switch (typeof v) {
    case 'string': return el('span', { class: 'str', text: JSON.stringify(v) });
    case 'number': return el('span', { class: 'num', text: String(v) });
    case 'boolean': return el('span', { class: 'bool', text: String(v) });
    default: return el('span', { text: String(v) });
  }
}

function jsonNode(key, value, depth, last, expandDepth) {
  const comma = last ? '' : ',';
  const keyPart = key === undefined ? null :
    el('span', {}, el('span', { class: 'key', text: typeof key === 'number' ? '' : JSON.stringify(key) }), typeof key === 'number' ? null : el('span', { class: 'p', text: ': ' }));
  const isArr = Array.isArray(value);
  if (value !== null && typeof value === 'object') {
    const entries = isArr ? value.map((v, i) => [i, v]) : Object.entries(value);
    const [open, close] = isArr ? ['[', ']'] : ['{', '}'];
    if (entries.length === 0) return el('div', { class: 'row' }, el('span', { class: 'tg' }), keyPart, el('span', { class: 'p', text: open + close + comma }));
    const node = el('div', { class: 'node' });
    const tg = el('span', { class: 'tg', text: '▾' });
    const sum = el('span', { class: 'sum', text: ` ${entries.length} ${isArr ? (entries.length === 1 ? 'item' : 'items') : (entries.length === 1 ? 'key' : 'keys')} ${close}${comma}` });
    const kids = el('div', { class: 'kids' });
    let built = false;
    const build = () => {
      if (built) return; built = true;
      entries.forEach(([k, v], i) => kids.append(jsonNode(k, v, depth + 1, i === entries.length - 1, expandDepth)));
    };
    const setCollapsed = c => { node.classList.toggle('c', c); tg.textContent = c ? '▸' : '▾'; if (!c) build(); };
    node._set = setCollapsed;
    tg.addEventListener('click', () => setCollapsed(!node.classList.contains('c')));
    sum.addEventListener('click', () => setCollapsed(false));
    node.append(el('div', { class: 'row' }, tg, keyPart, el('span', { class: 'p', text: open }), sum), kids,
      el('div', { class: 'row tail' }, el('span', { class: 'tg' }), el('span', { class: 'p', text: close + comma })));
    setCollapsed(depth >= expandDepth);
    return node;
  }
  return el('div', { class: 'row' }, el('span', { class: 'tg' }), keyPart, jsonValue(value), el('span', { class: 'p', text: comma }));
}

function setAllJson(container, collapsed) {
  // expanding builds children lazily, so walk repeatedly until stable
  for (let pass = 0; pass < 50; pass++) {
    const nodes = [...container.querySelectorAll('.node')].filter(n => n.classList.contains('c') !== collapsed);
    if (nodes.length === 0) break;
    nodes.forEach(n => n._set(collapsed));
    if (collapsed) break;
  }
}

async function renderJson(src, name) {
  const { text, total } = await loadText(src);
  let data;
  try { data = JSON.parse(text.replace(/^﻿/, '')); }
  catch (e) {
    const v = codeView(text, 'json', {});
    bar(`Invalid JSON: ${e.message}`, v.wrapBtn);
    root.replaceChildren(el('div', { class: 'banner', text: 'This file is not valid JSON: ' + e.message }), v.node);
    return;
  }
  let raw = false;
  const tree = el('div', { class: 'json' });
  tree.append(jsonNode(undefined, data, 0, true, total > 2_000_000 ? 1 : 3));
  const pretty = () => codeView(JSON.stringify(data, null, 2), 'json', {}).node;
  const draw = () => root.replaceChildren(raw ? pretty() : tree);
  draw();
  bar(`JSON · ${fmtSize(total)}`,
    button('Expand all', () => { if (!raw) setAllJson(tree, false); }),
    button('Collapse all', () => { if (!raw) setAllJson(tree, true); }),
    button('Raw', b => { raw = !raw; b.classList.toggle('on', raw); draw(); }));
}

/* ------------------------------------------------------------------ csv */
async function renderCsv(src, name) {
  const [{ text, total }] = await Promise.all([loadText(src), need('papa')]);
  const res = Papa.parse(text.replace(/^﻿/, ''), { skipEmptyLines: 'greedy', delimiter: /\.tsv$/i.test(name) ? '\t' : '' });
  const rows = res.data;
  let header = true;
  const draw = () => {
    const table = el('table', { class: 'grid' });
    const start = header ? 1 : 0;
    if (header && rows.length) {
      table.append(el('thead', {}, el('tr', {}, el('th', { class: 'rn', text: '' }), ...rows[0].map(h => el('th', { text: h, title: h })))));
    }
    const tb = el('tbody');
    const end = Math.min(rows.length, start + TABLE_ROWS_MAX);
    for (let i = start; i < end; i++) {
      tb.append(el('tr', {}, el('td', { class: 'rn', text: String(i - start + 1) }), ...rows[i].map(c => el('td', { text: c, title: c.length > 40 ? c : '' }))));
    }
    table.append(tb);
    const parts = [];
    if (rows.length - start > TABLE_ROWS_MAX) parts.push(el('div', { class: 'banner', text: `Showing the first ${TABLE_ROWS_MAX.toLocaleString()} of ${(rows.length - start).toLocaleString()} rows.` }));
    root.replaceChildren(...parts, table);
  };
  draw();
  bar(`${Math.max(0, rows.length - 1).toLocaleString()} rows · ${(rows[0] || []).length} columns · ${fmtSize(total)}`,
    button('Header row', b => { header = !header; b.classList.toggle('on', header); draw(); }, true));
}

/* ------------------------------------------------------------------ docx */
async function renderDocx(src, name) {
  const [{ buf }] = await Promise.all([fetchBytes(src), need('jszip').then(() => need('docx'))]); // docx-preview uses JSZip
  const host = el('div', { class: 'docx-host' });
  const styles = el('div');
  root.replaceChildren(styles, host);
  await docx.renderAsync(buf, host, styles, {
    className: 'docx', inWrapper: true, ignoreWidth: false, ignoreHeight: false, ignoreFonts: false,
    breakPages: true, ignoreLastRenderedPageBreak: true, experimental: true, renderHeaders: true, renderFooters: true,
    renderFootnotes: true, renderEndnotes: true, useBase64URL: true,
  });
  const pages = host.querySelectorAll('section.docx').length;
  let zoom = 1;
  const setZoom = z => { zoom = z; host.style.zoom = z; zl.textContent = Math.round(z * 100) + '%'; };
  const zl = el('span', { class: 'stat', text: '100%' });
  bar(`Word document · ${pages} page${pages === 1 ? '' : 's'}`,
    button('−', () => setZoom(Math.max(0.3, zoom - 0.1))), zl, button('+', () => setZoom(Math.min(3, zoom + 0.1))),
    button('Fit width', () => {
      const page = host.querySelector('section.docx');
      if (page) setZoom(Math.max(0.3, Math.min(3, (root.clientWidth - 40) / (page.offsetWidth || 800))));
    }));
  // start at fit-width when the pane is narrower than a page
  const page = host.querySelector('section.docx');
  if (page && page.offsetWidth > root.clientWidth - 40) setZoom(Math.max(0.3, (root.clientWidth - 40) / page.offsetWidth));
}

/* ------------------------------------------------------------------ pdf, page by page (Compare) */
// Edge's PDF viewer is a plugin that pages can't scroll, so Compare draws the pages itself (Windows' PDF engine renders each
// one as an image when it comes near the view). Then both sides are ordinary scrolling pages and can scroll together.
async function renderPdfPages(name) {
  const res = await fetch(q.get('api'));
  if (!res.ok) { post('pdf-native'); return; } // password-protected or damaged: the standard viewer explains it
  const pages = (await res.json()).pages;
  if (!pages.length) { message(name, 'This PDF has no pages.'); return; }
  const pageUrl = q.get('page');
  const widest = Math.max(...pages.map(p => p.w));
  const host = el('div', { class: 'pdf-pages' });
  const boxes = pages.map((p, i) => {
    const box = el('div', { class: 'pdf-page', title: `Page ${i + 1}` });
    box.dataset.i = i;
    return box;
  });
  host.append(...boxes);
  root.replaceChildren(host);

  let zoom = 0; // 0 = fit width
  const zl = el('span', { class: 'stat', text: 'Fit' });
  const scale = () => zoom || Math.max(0.1, (root.clientWidth - 32) / widest);
  const draw = box => {
    const want = Math.min(4096, Math.ceil(box.clientWidth * devicePixelRatio / 200) * 200); // steps, so small resizes reuse the image
    if (+box.dataset.w >= want) return;
    box.dataset.w = want;
    const img = box.querySelector('img') || box.appendChild(el('img', { alt: box.title }));
    img.src = `${pageUrl}&i=${box.dataset.i}&w=${want}`;
  };
  const seen = new IntersectionObserver(es => es.forEach(e => { if (e.isIntersecting) draw(e.target); }), { root, rootMargin: '1200px 0px' });
  const layout = () => {
    const s = scale();
    boxes.forEach((box, i) => { box.style.width = Math.round(pages[i].w * s) + 'px'; box.style.height = Math.round(pages[i].h * s) + 'px'; });
    zl.textContent = zoom ? Math.round(zoom * 100) + '%' : 'Fit';
    boxes.forEach(box => { if (box.dataset.w) draw(box); }); // sharper after zooming in
  };
  const setZoom = z => { zoom = z; layout(); };
  bar(`PDF · ${pages.length} page${pages.length === 1 ? '' : 's'}`,
    button('−', () => setZoom(Math.max(0.25, Math.round((scale() - 0.1) * 10) / 10))), zl,
    button('+', () => setZoom(Math.min(4, Math.round((scale() + 0.1) * 10) / 10))),
    button('Fit width', () => setZoom(0)));
  layout();
  boxes.forEach(b => seen.observe(b));
  new ResizeObserver(() => { if (!zoom) layout(); }).observe(root);
}

/* ------------------------------------------------------------------ xlsx */
async function renderXlsx(src, name) {
  const [{ buf, total }] = await Promise.all([fetchBytes(src), need('xlsx')]);
  const wb = XLSX.read(buf, { type: 'array', cellDates: true, dense: true, sheetRows: TABLE_ROWS_MAX + 1 });
  const tabs = el('div', { class: 'sheet-tabs' });
  const body = el('div', { class: 'xlsx' });
  const show = (sheetName, btn) => {
    tabs.querySelectorAll('button').forEach(b => b.classList.toggle('on', b === btn));
    const ws = wb.Sheets[sheetName];
    const rows = XLSX.utils.sheet_to_json(ws, { header: 1, raw: false, defval: '', blankrows: true });
    const width = rows.reduce((m, r) => Math.max(m, r.length), 0);
    const colName = i => XLSX.utils.encode_col(i);
    const table = el('table', { class: 'grid' });
    table.append(el('thead', {}, el('tr', {}, el('th', { class: 'rn' }), ...Array.from({ length: width }, (_, i) => el('th', { text: colName(i) })))));
    const tb = el('tbody');
    rows.slice(0, TABLE_ROWS_MAX).forEach((r, i) => {
      const cells = Array.from({ length: width }, (_, j) => { const v = r[j] ?? ''; return el('td', { text: String(v), title: String(v).length > 40 ? String(v) : '' }); });
      tb.append(el('tr', {}, el('td', { class: 'rn', text: String(i + 1) }), ...cells));
    });
    table.append(tb);
    body.replaceChildren(rows.length === 0 ? el('div', { class: 'msg', text: 'This sheet is empty.' }) : table);
  };
  wb.SheetNames.forEach((n, i) => {
    const b = el('button', { text: n });
    b.addEventListener('click', () => show(n, b));
    tabs.append(b);
  });
  root.replaceChildren(tabs, body);
  if (wb.SheetNames.length) show(wb.SheetNames[0], tabs.querySelector('button'));
  bar(`Workbook · ${wb.SheetNames.length} sheet${wb.SheetNames.length === 1 ? '' : 's'} · ${fmtSize(total)}`);
}

/* ------------------------------------------------------------------ archive contents */
async function renderArchive(api, name) {
  const r = await fetch(api);
  if (!r.ok) throw new Error(`Couldn't list the archive (${r.status}).`);
  const info = await r.json();
  if (info.error) { message("Can't list this archive", info.error, true); return; }
  const ratio = info.size > 0 ? Math.round(100 - (info.archiveSize / info.size) * 100) : 0;
  const filter = el('input', { type: 'search', placeholder: 'Filter…', class: 'filter' });
  let rows = info.entries;
  const table = el('table', { class: 'grid archive' });
  const draw = () => {
    const f = filter.value.trim().toLowerCase();
    const list = f ? rows.filter(e => e.p.toLowerCase().includes(f)) : rows;
    const tb = el('tbody');
    for (const e of list.slice(0, TABLE_ROWS_MAX)) {
      const slash = e.p.lastIndexOf('/');
      const nameCell = el('td', { title: e.p },
        slash > 0 ? el('span', { class: 'dir', text: e.p.substring(0, slash + 1) }) : null,
        el('span', { text: e.p.substring(slash + 1) }),
        e.e ? el('span', { class: 'tag', text: 'encrypted' }) : null,
        e.l ? el('span', { class: 'tag', text: 'link' }) : null);
      tb.append(el('tr', {}, nameCell,
        el('td', { class: 'num', text: e.s >= 0 ? fmtSize(e.s) : '' }),
        el('td', { class: 'num', text: e.c > 0 ? fmtSize(e.c) : '' }),
        el('td', { text: e.m || '' })));
    }
    table.replaceChildren(
      el('thead', {}, el('tr', {}, el('th', { text: 'Name' }), el('th', { text: 'Size' }), el('th', { text: 'Packed' }), el('th', { text: 'Modified' }))),
      tb);
  };
  filter.addEventListener('input', draw);
  draw();
  const parts = [];
  if (info.truncated) parts.push(el('div', { class: 'banner', text: `Showing the first ${info.entries.length.toLocaleString()} files.` }));
  if (info.encrypted) parts.push(el('div', { class: 'banner', text: 'Some files are password protected. Opening or extracting them asks for the password.' }));
  root.replaceChildren(...parts, table);
  bar(`${info.kind} · ${info.files.toLocaleString()} file${info.files === 1 ? '' : 's'}, ${info.folders.toLocaleString()} folder${info.folders === 1 ? '' : 's'} · ` +
      `${fmtSize(info.size)} → ${fmtSize(info.archiveSize)}${ratio > 0 ? ` (${ratio}% smaller)` : ''}`, filter);
}

/* ------------------------------------------------------------------ image / media / hex */
/* --- shared: info drawer (metadata from the Windows property system) --- */
async function loadProps() {
  const url = q.get('props');
  if (!url) return null;
  try { const r = await fetch(url); return r.ok ? await r.json() : null; } catch { return null; }
}

function infoDrawer(rows) {
  const d = el('aside', { class: 'info', hidden: '' });
  const fill = list => d.replaceChildren(el('h3', { text: 'Details' }),
    ...list.filter(r => r && r[1] !== undefined && r[1] !== null && r[1] !== '').map(([k, v]) => el('div', { class: 'kv' }, el('span', { text: k }), el('b', { text: String(v) }))));
  fill(rows);
  return { node: d, fill, toggle: b => { d.hidden = !d.hidden; b?.classList.toggle('on', !d.hidden); } };
}

function fmtTime(sec) {
  if (!isFinite(sec)) return '';
  sec = Math.max(0, sec);
  const h = Math.floor(sec / 3600), m = Math.floor(sec % 3600 / 60), s = Math.floor(sec % 60);
  return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(s).padStart(2, '0');
}

/* --- zoom / pan / rotate surface for an <img> or <canvas> --- */
function zoomSurface(content, naturalW, naturalH, onChange) {
  const stage = el('div', { class: 'stage' }, content);
  let s = 1, rot = 0, tx = 0, ty = 0, fitted = true;
  const apply = () => {
    content.style.transform = `translate(-50%, -50%) translate(${tx}px, ${ty}px) rotate(${rot}deg) scale(${s})`;
    content.classList.toggle('pixelated', s >= 3);
    onChange?.(s, fitted);
  };
  const fitScale = () => {
    const sideways = rot % 180 !== 0;
    const w = sideways ? naturalH : naturalW, h = sideways ? naturalW : naturalH;
    return Math.min((stage.clientWidth - 24) / w, (stage.clientHeight - 24) / h, 1);
  };
  const api = {
    stage,
    fit() { s = fitScale(); tx = ty = 0; fitted = true; apply(); },
    actual() { s = 1; tx = ty = 0; fitted = false; apply(); },
    zoomBy(f, px, py) {
      const ns = Math.min(32, Math.max(0.02, s * f));
      // keep the point under the cursor in place: t' = p - s'/s (p - t)
      const cx = (px ?? stage.clientWidth / 2) - stage.clientWidth / 2, cy = (py ?? stage.clientHeight / 2) - stage.clientHeight / 2;
      tx = cx - ns / s * (cx - tx); ty = cy - ns / s * (cy - ty);
      s = ns; fitted = false; apply();
    },
    rotate(d) { rot = (rot + d + 360) % 360; if (fitted) api.fit(); else apply(); },
    get scale() { return s; },
    get rotation() { return rot; },
  };
  stage.addEventListener('wheel', e => { e.preventDefault(); const r = stage.getBoundingClientRect(); api.zoomBy(e.deltaY < 0 ? 1.15 : 1 / 1.15, e.clientX - r.left, e.clientY - r.top); }, { passive: false });
  let drag = null;
  stage.addEventListener('pointerdown', e => { if (e.button !== 0) return; drag = { x: e.clientX, y: e.clientY, tx, ty }; stage.setPointerCapture(e.pointerId); stage.classList.add('dragging'); });
  stage.addEventListener('pointermove', e => { if (!drag) return; tx = drag.tx + e.clientX - drag.x; ty = drag.ty + e.clientY - drag.y; fitted = false; apply(); });
  const end = () => { drag = null; stage.classList.remove('dragging'); };
  stage.addEventListener('pointerup', end);
  stage.addEventListener('pointercancel', end);
  stage.addEventListener('dblclick', () => (fitted ? api.actual() : api.fit()));
  new ResizeObserver(() => { if (fitted) api.fit(); }).observe(stage);
  return api;
}

function zoomToolbar(z, extra) {
  const pct = el('span', { class: 'stat zoom', text: '' });
  const update = s => { pct.textContent = Math.round(s * 100) + '%'; };
  const items = [
    button('−', () => z.zoomBy(1 / 1.25)), pct, button('+', () => z.zoomBy(1.25)),
    button('Fit', () => z.fit()), button('100%', () => z.actual()),
    button('⟲', () => z.rotate(-90)), button('⟳', () => z.rotate(90)),
    button('⛶', () => toggleFullscreen()),
    ...extra,
  ];
  return { items, update };
}

function toggleFullscreen() {
  if (document.fullscreenElement) document.exitFullscreen(); else document.documentElement.requestFullscreen().catch(() => { });
}

function zoomKeys(z, extra) {
  return e => {
    if (e.ctrlKey || e.altKey) return false;
    switch (e.key) {
      case '+': case '=': z.zoomBy(1.25); return true;
      case '-': case '_': z.zoomBy(1 / 1.25); return true;
      case '0': z.fit(); return true;
      case '1': z.actual(); return true;
      case 'r': z.rotate(90); return true;
      case 'R': z.rotate(-90); return true;
      case 'f': case 'F11': toggleFullscreen(); return true;
      case 'ArrowLeft': case 'PageUp': post('prev'); return true;
      case 'ArrowRight': case 'PageDown': post('next'); return true;
    }
    return extra ? extra(e) : false;
  };
}

/* --- still images (and SVG, shown via <img> so its scripts never run) --- */
async function renderImage(src, name) {
  const rendition = q.get('render');
  if (!rendition && 'ImageDecoder' in window && /\.(gif|webp|png|apng|avif)$/i.test(name)) {
    try { if (await renderAnimated(src, name)) return; } catch (e) { console.warn(e); }
  }
  const img = el('img', { src: rendition || src, alt: name, draggable: 'false' });
  await new Promise((ok, fail) => { img.onload = ok; img.onerror = () => fail(new Error(rendition ? 'Windows has no codec installed for this image type.' : "The image couldn't be decoded.")); });
  const w = img.naturalWidth || 300, h = img.naturalHeight || 150;
  showImageSurface(img, w, h, name, rendition ? 'Converted for display' : null);
}

async function showImageSurface(content, w, h, name, note, extraTools = [], extraKeys = null) {
  const total = await fileSize();
  const info = infoDrawer([['Dimensions', `${w} × ${h} px`], ['Megapixels', (w * h / 1e6).toFixed(1)], ['File size', total ? fmtSize(total) : ''], ['Shown as', note]]);
  let tb;
  const z = zoomSurface(content, w, h, s => tb?.update(s));
  tb = zoomToolbar(z, [
    ...extraTools,
    button('Info', b => info.toggle(b)),
    button('Copy', () => post('copy-image')),
    button('Edit…', () => post('image-tools')),
  ]);
  root.replaceChildren(el('div', { class: 'viewer-split' }, z.stage, info.node));
  bar(`${w} × ${h} px${total ? ' · ' + fmtSize(total) : ''}`, ...tb.items);
  keyHandler = zoomKeys(z, extraKeys);
  requestAnimationFrame(() => z.fit());
  loadProps().then(p => { if (p?.items?.length) info.fill([['Dimensions', `${w} × ${h} px`], ['File size', total ? fmtSize(total) : ''], ['Shown as', note], ...p.items.map(i => [i.label, i.value])]); });
  return z;
}

let _sizeCache;
async function fileSize() {
  if (_sizeCache !== undefined) return _sizeCache;
  try { const r = await fetch(q.get('src'), { method: 'HEAD' }); _sizeCache = Number(r.headers.get('X-File-Size')) || 0; }
  catch { _sizeCache = 0; }
  return _sizeCache;
}

/* --- animated GIF / WebP / APNG / AVIF: frame-accurate playback with WebCodecs ImageDecoder --- */
async function renderAnimated(src, name) {
  const type = /\.gif$/i.test(name) ? 'image/gif' : /\.webp$/i.test(name) ? 'image/webp' : /\.avif$/i.test(name) ? 'image/avif' : 'image/png';
  if (!(await ImageDecoder.isTypeSupported(type))) return false;
  const { buf } = await fetchBytes(src);
  const dec = new ImageDecoder({ data: buf, type });
  await dec.tracks.ready;
  const track = dec.tracks.selectedTrack;
  if (!track || !track.animated) { dec.close(); return false; }
  await dec.completed;
  const count = track.frameCount;
  if (count < 2) { dec.close(); return false; }

  const first = (await dec.decode({ frameIndex: 0 })).image;
  const w = first.displayWidth, h = first.displayHeight;
  const canvas = el('canvas', { width: String(w), height: String(h) });
  const ctx = canvas.getContext('2d');
  ctx.drawImage(first, 0, 0);
  first.close();

  let index = 0, playing = true, speed = 1, timer = 0, totalMs = 0, gen = 0;
  const counter = el('span', { class: 'stat', text: '' });
  const playBtn = button('⏸', () => (playing ? pause() : play()));
  const show = async i => {
    const my = ++gen;
    const r = await dec.decode({ frameIndex: i });
    if (my !== gen) { r.image.close(); return 100; }
    // frames composite on each other only through the decoder; draw the full frame it returns
    ctx.clearRect(0, 0, w, h);
    ctx.drawImage(r.image, 0, 0);
    const ms = r.image.duration ? r.image.duration / 1000 : 100;
    r.image.close();
    index = i;
    counter.textContent = `Frame ${i + 1} / ${count}`;
    return ms <= 10 ? 100 : ms; // browsers treat 0–10 ms GIF delays as 100 ms
  };
  const tick = async () => { const ms = await show((index + 1) % count); if (playing) timer = setTimeout(tick, ms / speed); };
  function play() { playing = true; playBtn.textContent = '⏸'; clearTimeout(timer); tick(); }
  function pause() { playing = false; playBtn.textContent = '▶'; clearTimeout(timer); gen++; }
  const step = d => { pause(); show((index + d + count) % count); };
  const speedSel = el('select', { title: 'Playback speed' }, ...[0.25, 0.5, 1, 1.5, 2, 4].map(v => el('option', { value: String(v), text: v + '×' })));
  speedSel.value = '1';
  speedSel.addEventListener('change', () => { speed = Number(speedSel.value); });

  const z = await showImageSurface(canvas, w, h, name, `Animated, ${count} frames${track.repetitionCount === Infinity ? ', loops forever' : ''}`,
    [button('⏮', () => step(-1)), playBtn, button('⏭', () => step(1)), speedSel, counter],
    e => {
      if (e.key === ',') { step(-1); return true; }
      if (e.key === '.') { step(1); return true; }
      if (e.key === 'k' || e.key === 'p') { playing ? pause() : play(); return true; }
      return false;
    });
  counter.textContent = `Frame 1 / ${count}`;
  timer = setTimeout(tick, 100);
  window.addEventListener('pagehide', () => { clearTimeout(timer); dec.close(); });
  return !!z;
}

/* --- video and audio (Chromium codecs; the host switches to Media Foundation when these fail) --- */
function srtToVtt(text) {
  return 'WEBVTT\n\n' + text.replace(/\r/g, '').replace(/^\d+\n(?=\d\d:)/gm, '').replace(/(\d\d:\d\d:\d\d),(\d\d\d)/g, '$1.$2');
}

async function attachSubtitles(video) {
  const url = q.get('subs');
  if (!url) return null;
  try {
    const { text } = await loadText(url);
    const vtt = /\.srt$/i.test(url) ? srtToVtt(text) : text;
    const track = el('track', { kind: 'subtitles', label: decodeURIComponent(url.split('/').pop()), srclang: 'en', default: '',
      src: URL.createObjectURL(new Blob([vtt], { type: 'text/vtt' })) });
    video.append(track);
    return track;
  } catch { return null; }
}

function speedSelect(media) {
  const sel = el('select', { title: 'Playback speed' }, ...[0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2].map(v => el('option', { value: String(v), text: v + '×' })));
  sel.value = '1';
  sel.addEventListener('change', () => { media.playbackRate = Number(sel.value); });
  return sel;
}

// videoTracks is behind a flag in Chromium; without it, a file in a video container is assumed to have a picture
const hasVideoTrack = m => (m.videoTracks ? m.videoTracks.length > 0 : true);

async function renderMedia(src, name) {
  const isAudio = /\.(mp3|wav|ogg|oga|m4a|aac|flac|opus|weba)$/i.test(name);
  const m = el(isAudio ? 'audio' : 'video', { src, controls: '', preload: 'metadata' });
  activeMedia = m;
  let failed = false;
  m.addEventListener('error', () => {
    if (failed) return;
    failed = true;
    message("This format can't be played by the built-in web player", 'Trying the Windows media player…');
    post('media-unsupported ' + src);
  });
  const info = infoDrawer([]);
  const stat = el('span', { class: 'stat', text: '' });
  const codec = el('span', { class: 'stat', text: '' });
  const total = await fileSize();
  const props = loadProps();

  if (isAudio) {
    const art = el('img', { class: 'art', alt: '', src: q.get('thumb') || '' });
    art.onerror = () => art.replaceWith(el('div', { class: 'art none', text: '♪' }));
    const title = el('div', { class: 'title', text: name });
    const sub = el('div', { class: 'sub', text: '' });
    const wave = el('canvas', { class: 'wave', height: '96' });
    const card = el('div', { class: 'audio-card' }, art, el('div', { class: 'meta' }, title, sub), m, wave);
    root.replaceChildren(el('div', { class: 'viewer-split' }, el('div', { class: 'audio-wrap' }, card), info.node));
    props.then(p => {
      if (!p) return;
      const get = l => p.items?.find(i => i.label === l)?.value;
      if (get('Title')) title.textContent = get('Title');
      sub.textContent = [get('Artist') || get('Album artist'), get('Album'), get('Year')].filter(Boolean).join(' · ');
      info.fill([['File size', fmtSize(total)], ...(p.items || []).map(i => [i.label, i.value])]);
    });
    if (total && total <= WAVEFORM_MAX) drawWaveform(src, wave, m).catch(() => wave.remove());
    else wave.remove();
  } else {
    const thumb = q.get('thumb');
    if (thumb) m.poster = thumb;
    await attachSubtitles(m);
    root.replaceChildren(el('div', { class: 'viewer-split' }, el('div', { class: 'media' }, m), info.node));
    props.then(p => {
      if (p) info.fill([['File size', fmtSize(total)], ...(p.items || []).map(i => [i.label, i.value])]);
      if (p?.codec) codec.textContent = ' · ' + p.codec;
    });
  }
  m.addEventListener('loadedmetadata', () => {
    if (failed) return;
    // Chromium plays the sound but not the picture of some codecs (MPEG-4 Part 2/Xvid, MS-MPEG4…) without raising an
    // error: the video is 0 × 0. Hand those to the Windows player, which usually has the decoder.
    if (!isAudio && m.videoWidth === 0 && m.videoHeight === 0 && hasVideoTrack(m)) {
      failed = true;
      m.pause();
      m.removeAttribute('src');
      m.load();
      message("The built-in web player can't show this video's picture", 'Trying the Windows media player…');
      post('media-unsupported ' + src);
      return;
    }
    stat.textContent = (isAudio ? '' : `${m.videoWidth} × ${m.videoHeight} · `) + fmtTime(m.duration);
  });
  const tools = [stat, codec, speedSelect(m), button('Info', b => info.toggle(b))];
  if (!isAudio) tools.push(button('⛶', () => (document.fullscreenElement ? document.exitFullscreen() : m.requestFullscreen().catch(() => { }))));
  bar(isAudio ? 'Audio' : 'Video', ...tools);
  keyHandler = e => {
    if (e.key === 'ArrowLeft' && !e.ctrlKey) { m.currentTime = Math.max(0, m.currentTime - 5); return true; }
    if (e.key === 'ArrowRight' && !e.ctrlKey) { m.currentTime = Math.min(m.duration || 0, m.currentTime + 5); return true; }
    if (e.key === 'm') { m.muted = !m.muted; return true; }
    if (e.key === 'f' && !isAudio) { document.fullscreenElement ? document.exitFullscreen() : m.requestFullscreen().catch(() => { }); return true; }
    return false;
  };
}

/* waveform: peaks of the decoded audio; click to seek, progress drawn on top */
async function drawWaveform(src, canvas, media) {
  const { buf } = await fetchBytes(src);
  const ac = new OfflineAudioContext(1, 1, 44100);
  const audio = await ac.decodeAudioData(buf);
  const data = audio.getChannelData(0);
  const draw = () => {
    const w = canvas.clientWidth || 600, h = canvas.height;
    canvas.width = w;
    const ctx = canvas.getContext('2d');
    const step = Math.max(1, Math.floor(data.length / w));
    const played = media.duration ? media.currentTime / media.duration * w : 0;
    for (let x = 0; x < w; x++) {
      let min = 1, max = -1;
      for (let i = x * step, end = Math.min(data.length, i + step); i < end; i++) { const v = data[i]; if (v < min) min = v; if (v > max) max = v; }
      ctx.fillStyle = x < played ? '#5aa9ff' : '#4a4a55';
      ctx.fillRect(x, (1 - max) * h / 2, 1, Math.max(1, (max - min) * h / 2));
    }
  };
  draw();
  media.addEventListener('timeupdate', draw);
  new ResizeObserver(draw).observe(canvas);
  canvas.addEventListener('click', e => { if (media.duration) media.currentTime = e.offsetX / canvas.clientWidth * media.duration; });
}

function hexDump(buf, max = 4096) {
  const b = new Uint8Array(buf, 0, Math.min(buf.byteLength, max));
  const lines = [];
  for (let off = 0; off < b.length; off += 16) {
    const chunk = b.subarray(off, off + 16);
    const hex = [...chunk].map(x => x.toString(16).padStart(2, '0')).join(' ').padEnd(47, ' ');
    const asc = [...chunk].map(x => x >= 32 && x < 127 ? String.fromCharCode(x) : '.').join('');
    lines.push(`<span class="off">${off.toString(16).padStart(8, '0')}</span>  ${hex}  <span class="asc">${escapeHtml(asc)}</span>`);
  }
  const d = el('div', { class: 'hex' });
  d.innerHTML = lines.join('\n');
  return d;
}

async function renderAuto(src, name) {
  const head = await fetchBytes(src, 65536);
  if (head.total === 0) { message('Empty file', name); return; }
  if (looksBinary(head.buf)) {
    bar(`Binary file · ${fmtSize(head.total)} · first ${fmtSize(Math.min(4096, head.total))} shown as hex`);
    root.replaceChildren(hexDump(head.buf));
    return;
  }
  await renderCode(src, name, '', false);
}

/* ------------------------------------------------------------------ diff */
function sortKeys(v) {
  if (Array.isArray(v)) return v.map(sortKeys);
  if (v && typeof v === 'object') return Object.fromEntries(Object.keys(v).sort().map(k => [k, sortKeys(v[k])]));
  return v;
}

function splitLines(s) {
  const lines = s.split(/\r\n|\r|\n/);
  if (lines.length && lines[lines.length - 1] === '') lines.pop();
  return lines;
}

function inline(a, b) {
  // character-level highlight within a changed line pair
  const parts = Diff.diffWordsWithSpace(a, b);
  let left = '', right = '';
  for (const p of parts) {
    const t = escapeHtml(p.value);
    if (p.added) right += `<span class="add">${t}</span>`;
    else if (p.removed) left += `<span class="del">${t}</span>`;
    else { left += t; right += t; }
  }
  return [left, right];
}

async function renderDiff() {
  const a = q.get('a'), b = q.get('b'), an = q.get('an') || 'Left', bn = q.get('bn') || 'Right';
  const [ta, tb] = await Promise.all([loadText(a), loadText(b), need('diff')]);
  let ja, jb;
  try { ja = JSON.parse(ta.text.replace(/^﻿/, '')); jb = JSON.parse(tb.text.replace(/^﻿/, '')); } catch { ja = jb = undefined; }
  const isJson = ja !== undefined;
  let normalize = isJson, onlyChanges = true, ignoreWs = false;

  const draw = () => {
    let A = ta.text, B = tb.text;
    if (isJson && normalize) { A = JSON.stringify(sortKeys(ja), null, 2); B = JSON.stringify(sortKeys(jb), null, 2); }
    const parts = Diff.diffLines(A, B, { ignoreWhitespace: ignoreWs, newlineIsToken: false });
    // build aligned rows
    const rows = []; let ln = 1, rn = 1, adds = 0, dels = 0;
    for (let i = 0; i < parts.length; i++) {
      const p = parts[i];
      const lines = splitLines(p.value);
      if (p.removed && parts[i + 1]?.added) {
        const next = splitLines(parts[i + 1].value);
        const n = Math.max(lines.length, next.length);
        for (let k = 0; k < n; k++) {
          const l = lines[k], r = next[k];
          if (l !== undefined && r !== undefined) {
            const [hl, hr] = inline(l, r);
            rows.push({ t: 'chg', ln: ln++, rn: rn++, l: hl, r: hr, html: true });
          } else if (l !== undefined) rows.push({ t: 'del', ln: ln++, l });
          else rows.push({ t: 'add', rn: rn++, r });
        }
        dels += lines.length; adds += next.length; i++;
      } else if (p.removed) { lines.forEach(l => rows.push({ t: 'del', ln: ln++, l })); dels += lines.length; }
      else if (p.added) { lines.forEach(r => rows.push({ t: 'add', rn: rn++, r })); adds += lines.length; }
      else lines.forEach(l => rows.push({ t: 'same', ln: ln++, rn: rn++, l, r: l }));
    }

    stat.innerHTML = `<b class="del">−${dels}</b> / <b class="add">+${adds}</b> lines`;
    if (adds === 0 && dels === 0) {
      root.replaceChildren(el('div', { class: 'same', text: normalize && isJson ? 'The two JSON documents are identical (ignoring key order).' : 'The files are identical.' }));
      return;
    }

    const table = el('table', { class: 'diff' });
    table.append(el('colgroup', {}, el('col', { class: 'n' }), el('col'), el('col', { class: 'n' }), el('col')));
    table.append(el('thead', {}, el('tr', {}, el('th', { colspan: '2', text: an, title: an }), el('th', { colspan: '2', text: bn, title: bn }))));
    const body = el('tbody');
    const CONTEXT = 3;
    const keep = rows.map(() => !onlyChanges);
    if (onlyChanges) rows.forEach((r, i) => { if (r.t !== 'same') for (let k = Math.max(0, i - CONTEXT); k <= Math.min(rows.length - 1, i + CONTEXT); k++) keep[k] = true; });

    const cell = (cls, content, html) => { const td = el('td', { class: cls }); if (html) td.innerHTML = content; else td.textContent = content; return td; };
    const rowEl = r => {
      const tr = el('tr');
      const leftCls = r.t === 'del' || r.t === 'chg' ? 'del' : r.t === 'add' ? 'empty' : '';
      const rightCls = r.t === 'add' || r.t === 'chg' ? 'add' : r.t === 'del' ? 'empty' : '';
      tr.append(cell('n', r.ln ?? ''), cell(leftCls, r.l ?? '', r.html), cell('n', r.rn ?? ''), cell(rightCls, r.r ?? '', r.html));
      return tr;
    };
    for (let i = 0; i < rows.length;) {
      if (keep[i]) { body.append(rowEl(rows[i])); i++; continue; }
      let j = i; while (j < rows.length && !keep[j]) j++;
      const hidden = rows.slice(i, j);
      const fold = el('tr', { class: 'fold' }, el('td', { colspan: '4', text: `⋯ ${hidden.length} unchanged line${hidden.length === 1 ? '' : 's'} ⋯` }));
      fold.addEventListener('click', () => fold.replaceWith(...hidden.map(rowEl)));
      body.append(fold);
      i = j;
    }
    table.append(body);
    root.replaceChildren(table);
  };

  const stat = el('span', { class: 'stat' });
  bar('Text compare', stat,
    button('Only changes', bt => { onlyChanges = !onlyChanges; bt.classList.toggle('on', onlyChanges); draw(); }, true),
    button('Ignore whitespace', bt => { ignoreWs = !ignoreWs; bt.classList.toggle('on', ignoreWs); draw(); }),
    isJson ? button('Normalize JSON', bt => { normalize = !normalize; bt.classList.toggle('on', normalize); draw(); }, true) : null);
  draw();
}

/* ------------------------------------------------------------------ dispatch */
(async () => {
  try {
    const mode = q.get('mode') || 'view';
    if (mode === 'msg') { message(q.get('title') || '', q.get('text') || '', q.get('error') === '1'); return; }
    if (mode === 'diff') { await renderDiff(); return; }
    const src = q.get('src'), kind = q.get('kind'), name = q.get('name') || '', lang = q.get('lang') || '';
    switch (kind) {
      case 'markdown': await renderMarkdown(src, name); break;
      case 'json': await renderJson(src, name); break;
      case 'code': await renderCode(src, name, lang, false); break;
      case 'text': await renderCode(src, name, '', true); break;
      case 'csv': await renderCsv(src, name); break;
      case 'docx': await renderDocx(src, name); break;
      case 'xlsx': await renderXlsx(src, name); break;
      case 'image': await renderImage(src, name); break;
      case 'media': await renderMedia(src, name); break;
      case 'archive': await renderArchive(q.get('api'), name); break;
      case 'pdfpages': await renderPdfPages(name); break;
      default: await renderAuto(src, name); break;
    }
  } catch (e) {
    console.error(e);
    message("Couldn't preview this file", e && e.message ? e.message : String(e), true);
  }
})();
