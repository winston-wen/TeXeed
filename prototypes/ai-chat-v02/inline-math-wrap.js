/* Let the browser fit complete TeX atoms into the space left on each reading line. */
(() => {
  'use strict';
  const cache = new Map();
  const widths = new Map();
  const processed = new WeakSet();
  const fences = new Map([['(', ')'], ['[', ']'], ['{', '}'], ['⟨', '⟩'], ['⌊', '⌋'], ['⌈', '⌉']]);
  const pairedBars = new Set(['|', '∣', '‖', '∥']);
  const candidate = /[=+−\-*/<>,;≤≥≠≈≡×÷∈∪∩\\]/u;

  function split(rendered) {
    const html = rendered.querySelector(':scope > .katex-html');
    if (!html || !html.children.length || [...html.children].some(el => !el.classList.contains('base'))) return false;
    const groups = [];
    const open = [];
    let group = [], previous = null, cut = false;
    for (const base of html.children) {
      for (const atom of base.children) {
        if (atom.classList.contains('strut')) continue;
        if (atom.classList.contains('mspace')) {
          if (atom.classList.contains('nobreak')) cut = false;
          group.push({ atom, base });
          continue;
        }
        const glyph = atom.textContent.trim();
        const operator = atom.matches('.mrel,.mbin,.mpunct') || (glyph === '/' && atom.classList.contains('mord'));
        if (cut && !operator) { groups.push(group); group = []; }
        const enclosing = fences.get(glyph);
        if (enclosing) open.push(enclosing);
        else if (pairedBars.has(glyph) && atom.classList.contains('mord')) {
          if (open.at(-1) === glyph) open.pop(); else open.push(glyph);
        } else if (open.at(-1) === glyph) open.pop();
        else if (atom.classList.contains('mopen')) open.push(null);
        else if (atom.classList.contains('mclose')) open.pop();
        cut = !open.length && operator && !!previous;
        group.push({ atom, base });
        previous = atom;
      }
    }
    if (!groups.length || !group.length || open.length) return false;
    groups.push(group);
    const output = document.createDocumentFragment();
    for (const group of groups) {
      // Keep each original KaTeX strut with the atoms it measured. Nested boxes
      // (fractions, scripts, roots and matrices) move as complete DOM subtrees.
      const chunk = document.createElement('span');
      chunk.className = 'math-chunk';
      let lastBase = null, run;
      for (const { atom, base } of group) {
        if (lastBase !== base) {
          run = base.cloneNode(false);
          const strut = base.querySelector(':scope > .strut');
          if (strut) run.append(strut.cloneNode(true));
          chunk.append(run);
          lastBase = base;
        }
        run.append(atom);
      }
      if (output.childNodes.length) output.append(document.createElement('wbr'));
      output.append(chunk);
    }
    html.replaceChildren(output);
    return true;
  }

  function presentation(owner, tex) {
    if (cache.has(tex)) return cache.get(tex);
    let rendered = owner.matches('.katex') ? owner : owner.querySelector(':scope > .katex');
    if (rendered) rendered = rendered.cloneNode(true);
    else {
      // Bundled and historical answers can hold a single SVG. Only substitute it
      // when the original TeX also has an equivalent breakable KaTeX rendering.
      const temporary = document.createElement('span');
      try { katex.render(tex, temporary, { throwOnError: true, trust: false, strict: 'ignore', maxExpand: 1000, maxSize: 20 }); }
      catch { return null; }
      rendered = temporary.firstElementChild;
    }
    const result = split(rendered) ? rendered : null;
    if (cache.size >= 192) cache.clear();
    cache.set(tex, result);
    return result;
  }

  function refreshChineseSvg(svg) {
    const owner = svg.parentElement, tex = owner.dataset.tex;
    if (svg.dataset.cjkFace === 'serif' || !tex || !/\p{Script=Han}/u.test(tex)) return;
    const display = owner.dataset.display === 'true';
    const source = display ? tex.replace(/\\(?:label|tag\*?)\{[^{}]+\}/g, '').trim() : tex;
    try { owner.innerHTML = compileRevisionFormula(source, display); }
    catch { /* Preserve the typeset SVG for sources unsupported by KaTeX. */ }
  }

  window.prepareReadingMath = function (scope) {
    scope.querySelectorAll('.math[data-tex] > svg').forEach(refreshChineseSvg);
    const pending = [];
    const selector = '.math[data-display=false], :is(.question-text,.snapshot-scroll,.branch-origin-view,.streaming-answer) .katex';
    for (const owner of scope.querySelectorAll(selector)) {
      if (processed.has(owner) || owner.classList.contains('wrapped-math') ||
          (owner.matches('.katex') && owner.closest('.math,.katex-display,.quote-math-window'))) continue;
      const tex = owner.dataset.tex ?? owner.querySelector('annotation')?.textContent;
      if (!tex || !candidate.test(tex)) continue;
      const svg = owner.querySelector(':scope > svg');
      // All layout reads precede the changes, including for repeated formulas.
      let width = svg?.width.baseVal.unitType === SVGLength.SVG_LENGTHTYPE_EMS
        ? svg.width.baseVal.valueInSpecifiedUnits : widths.get(tex);
      if (width === undefined) {
        width = [...owner.querySelectorAll('.katex-html > .base')].reduce((sum, base) => sum + base.getBoundingClientRect().width, 0) / parseFloat(getComputedStyle(owner).fontSize);
        if (width) {
          if (widths.size >= 256) widths.clear();
          widths.set(tex, width);
        }
      }
      if (!width) continue;
      processed.add(owner);
      if (width >= 6) pending.push([owner, tex]);
    }
    for (const [owner, tex] of pending) {
      const rendered = presentation(owner, tex);
      if (!rendered) continue;
      if (owner.matches('.katex')) {
        owner.querySelector('.katex-html').replaceWith(rendered.querySelector('.katex-html').cloneNode(true));
      } else owner.replaceChildren(rendered.cloneNode(true));
      owner.classList.add('wrapped-math');
    }
  };
})();
