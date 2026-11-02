// Minimal safe markdown renderer.
// Constraint: the input is HTML-escaped before parsing and the parser only
// wraps escaped text in its own tags, so user markup can never reach the DOM
// raw and only http/https links ever become anchors.

function esc(s) {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

export function renderMarkdown(src) {
  if (src == null) return '';
  const lines = String(src).split(/\r\n|\r|\n/).map(esc);
  return parseBlocks(lines);
}

function parseBlocks(lines) {
  const out = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (!line.trim()) { i++; continue; }
    const fence = line.match(/^```(.*)$/);
    if (fence) {
      const body = [];
      i++;
      while (i < lines.length && !lines[i].startsWith('```')) { body.push(lines[i]); i++; }
      if (i < lines.length) i++;
      out.push(codeBlock(body.join('\n'), fence[1].trim()));
      continue;
    }
    if (/^\s*([-*_])(\s*\1){2,}\s*$/.test(line)) { out.push('<hr>'); i++; continue; }
    const heading = line.match(/^(#{1,4})\s+(.*)$/);
    if (heading) {
      const level = heading[1].length;
      out.push(`<h${level}>${inline(heading[2].replace(/\s+#+\s*$/, ''))}</h${level}>`);
      i++;
      continue;
    }
    if (line.includes('|') && isTableSeparator(lines[i + 1] || '')) {
      const [html, consumed] = table(lines, i);
      out.push(html);
      i += consumed;
      continue;
    }
    // Lines are pre-escaped, so a blockquote marker is &gt; here.
    if (/^&gt;\s?/.test(line)) {
      const quote = [];
      while (i < lines.length && /^&gt;\s?/.test(lines[i])) { quote.push(lines[i].replace(/^&gt;\s?/, '')); i++; }
      out.push(`<blockquote>${parseBlocks(quote)}</blockquote>`);
      continue;
    }
    if (/^(\s*)(?:[-*+]|\d+[.)])\s+/.test(line)) {
      const [html, consumed] = list(lines, i);
      out.push(html);
      i += consumed;
      continue;
    }
    const para = [line];
    i++;
    while (i < lines.length && lines[i].trim() && !isBlockStart(lines[i], lines[i + 1] || '')) {
      para.push(lines[i]);
      i++;
    }
    out.push(`<p>${para.map(inline).join('<br>')}</p>`);
  }
  return out.join('');
}

function isBlockStart(line, next) {
  return line.startsWith('```')
    || /^\s*([-*_])(\s*\1){2,}\s*$/.test(line)
    || /^#{1,4}\s+/.test(line)
    || /^&gt;\s?/.test(line)
    || /^(\s*)(?:[-*+]|\d+[.)])\s+/.test(line)
    || (line.includes('|') && isTableSeparator(next));
}

function isTableSeparator(line) {
  if (!line.includes('|') || !line.includes('-')) return false;
  return splitRow(line).every(c => /^:?-+:?$/.test(c));
}

function splitRow(line) {
  let s = line.trim();
  if (s.startsWith('|')) s = s.slice(1);
  if (s.endsWith('|')) s = s.slice(0, -1);
  return s.split('|').map(c => c.trim());
}

function table(lines, i) {
  const header = splitRow(lines[i]);
  const aligns = splitRow(lines[i + 1]).map(c =>
    c.startsWith(':') && c.endsWith(':') ? 'center' : c.endsWith(':') ? 'right' : 'left');
  const rows = [];
  let j = i + 2;
  while (j < lines.length && lines[j].trim() && lines[j].includes('|')) {
    rows.push(splitRow(lines[j]));
    j++;
  }
  const cols = header.length;
  const th = header.map((c, k) => `<th class="ta-${aligns[k] || 'left'}">${inline(c)}</th>`).join('');
  const tb = rows.map(r =>
    `<tr>${Array.from({ length: cols }, (_, k) => `<td class="ta-${aligns[k] || 'left'}">${inline(r[k] ?? '')}</td>`).join('')}</tr>`).join('');
  return [`<div class="md-table-wrap"><table><thead><tr>${th}</tr></thead><tbody>${tb}</tbody></table></div>`, j - i];
}

// One nesting level: two or more leading spaces mark a child item.
function list(lines, i) {
  const items = [];
  let j = i;
  while (j < lines.length) {
    const line = lines[j];
    const m = line.match(/^(\s*)([-*+]|\d+[.)])\s+(.*)$/);
    if (!m) {
      if (!line.trim() && /^(\s*)(?:[-*+]|\d+[.)])\s+/.test(lines[j + 1] || '')) { j++; continue; }
      break;
    }
    items.push({ level: m[1].length >= 2 ? 1 : 0, text: m[3], ordered: /\d/.test(m[2]) });
    j++;
  }
  let html = '';
  let k = 0;
  while (k < items.length) {
    const first = items[k];
    let n = k;
    const entries = [];
    // Consume one level-0 item plus any level-1 items that follow it.
    const collect = () => {
      const entry = { text: items[n].text, children: [] };
      n++;
      while (n < items.length && items[n].level === 1) { entry.children.push(items[n].text); n++; }
      return entry;
    };
    entries.push(collect());
    while (n < items.length && items[n].level === 0 && items[n].ordered === first.ordered) {
      entries.push(collect());
    }
    const tag = first.ordered ? 'ol' : 'ul';
    const items_ = entries.map(e =>
      `<li>${inline(e.text)}${e.children.length ? `<ul>${e.children.map(c => `<li>${inline(c)}</li>`).join('')}</ul>` : ''}</li>`).join('');
    html += `<${tag}>${items_}</${tag}>`;
    k = n;
  }
  return [html, j - i];
}

function codeBlock(code, lang) {
  const label = `<span class="md-lang">${lang || ''}</span>`;
  return `<div class="md-code"><div class="md-code-head">${label}<button type="button" class="md-copy" data-copy>Copy</button></div><pre><code>${code}</code></pre></div>`;
}

function inline(text) {
  const codes = [];
  let s = String(text).replace(/`([^`]+)`/g, (m, c) => {
    codes.push(c);
    return `\u0001${codes.length - 1}\u0001`;
  });
  s = s.replace(/\[([^\]]*)\]\(([^)\s]*)\)/g, (m, label, url) => {
    const decoded = url.replace(/&amp;/g, '&');
    if (!/^https?:\/\//i.test(decoded)) return m;
    const host = (decoded.match(/^https?:\/\/([^/?#]+)/i) || [])[1] || '';
    return `<a href="${url}" target="_blank" rel="noopener noreferrer">${label || url}</a><span class="md-host">(${esc(host)})</span>`;
  });
  s = s.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
  s = s.replace(/~~([^~]+)~~/g, '<del>$1</del>');
  s = s.replace(/(^|[^*\w])\*([^*\n]+)\*(?!\w)/g, '$1<em>$2</em>');
  s = s.replace(/\u0001(\d+)\u0001/g, (m, idx) => `<code>${codes[+idx]}</code>`);
  return s;
}
