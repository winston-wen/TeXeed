/* A quote is abbreviated only after complete math expressions have been parsed. */
(function (root) {
  'use strict';
  const cache = new Map();
  const escape = value => String(value ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));
  function escaped(text, index) {
    let slashes = 0; for (let i = index - 1; i >= 0 && text[i] === '\\'; i--) slashes++;
    return slashes % 2 !== 0;
  }
  function endOf(text, delimiter, from) {
    for (let at = text.indexOf(delimiter, from); at >= 0; at = text.indexOf(delimiter, at + delimiter.length))
      if (!escaped(text, at)) return at;
    return -1;
  }
  function tokens(source, budget) {
    const text = String(source ?? ''), result = []; let plain = '', i = 0, consumed = 0;
    const flush = () => { if (plain) { result.push({ text: plain }); consumed += Array.from(plain.replace(/\s+/g, ' ')).length; plain = ''; } };
    while (i < text.length && consumed + plain.length <= budget) {
      if (text[i] === '`' && !escaped(text, i)) {
        let n = 1; while (text[i + n] === '`') n++;
        const mark = '`'.repeat(n), end = text.indexOf(mark, i + n);
        if (end >= 0) { flush(); const code = text.slice(i + n, end); result.push({ code }); consumed += Array.from(code).length; i = end + n; continue; }
      }
      const open = !escaped(text, i) && (text.startsWith('$$', i) ? '$$' : text.startsWith('\\(', i) ? '\\(' : text.startsWith('\\[', i) ? '\\[' : text[i] === '$' ? '$' : null);
      if (open) {
        const close = open === '\\(' ? '\\)' : open === '\\[' ? '\\]' : open;
        const end = endOf(text, close, i + open.length);
        if (end > i + open.length && !(open === '$' && text.slice(i, end).includes('\n'))) {
          flush(); const math = text.slice(i + open.length, end); result.push({ math, raw: text.slice(i, end + close.length) }); consumed += Math.min(20, Array.from(math).length); i = end + close.length; continue;
        }
        if (open.length > 1) { plain += open; i += open.length; continue; }
      }
      const character = String.fromCodePoint(text.codePointAt(i));
      plain += character; i += character.length;
    }
    flush(); if (i < text.length) result.push({ more: true }); return result;
  }
  root.renderReferenceSummary = function (source, budget = 120) {
    source = String(source ?? '');
    const key = source.length <= 4096 ? budget + '\n' + source : null;
    if (key !== null && cache.has(key)) return cache.get(key);
    const parts = tokens(source, budget), out = []; let remaining = budget, truncated = false;
    for (let i = 0; i < parts.length; i++) {
      const part = parts[i];
      if (part.more) { truncated = true; break; }
      if (remaining <= 0) { truncated = true; break; }
      if (part.math !== undefined) {
        try {
          const rendered = root.katex.renderToString(part.math, { displayMode: false, throwOnError: true, trust: false, strict: 'ignore', maxExpand: 1000, maxSize: 20 });
          out.push('<span class="quote-math-window" role="math" aria-label="' + escape(part.math) + '">' + rendered + '</span><span class="quote-math-more" aria-hidden="true" hidden>…</span>');
        } catch { out.push(escape(part.raw)); }
        remaining -= Math.min(20, Array.from(part.math).length); continue;
      }
      const text = String(part.code ?? part.text).replace(/\s+/g, ' '), chars = Array.from(text), shown = chars.slice(0, remaining).join('');
      out.push(part.code !== undefined ? '<code>' + escape(shown) + '</code>' : escape(shown));
      remaining -= Math.min(chars.length, remaining);
      if (chars.length > Array.from(shown).length) { truncated = true; break; }
    }
    const result = out.join('') + (truncated ? '<span class="quote-text-more">…</span>' : '');
    if (key !== null && result.length <= 32768) {
      if (cache.size >= 128) cache.delete(cache.keys().next().value);
      cache.set(key, result);
    }
    return result;
  };
  let pending = false;
  root.refreshReferenceClips = function () {
    if (pending) return; pending = true;
    requestAnimationFrame(() => {
      pending = false;
      for (const box of document.querySelectorAll('.quote-math-window')) {
        const marker = box.nextElementSibling;
        if (marker?.classList.contains('quote-math-more')) marker.hidden = box.scrollWidth <= box.clientWidth + 1;
      }
    });
  };
  root.addEventListener('resize', root.refreshReferenceClips, { passive: true });
  document.fonts?.ready.then(root.refreshReferenceClips);
  document.fonts?.addEventListener?.('loadingdone', root.refreshReferenceClips);
})(globalThis);
